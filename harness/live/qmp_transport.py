"""QMP input transport over `virsh qemu-monitor-command` (Session 2).

Press and release are SEPARATE monitor commands by protocol (QMP
input-send-event is per-event; nothing atomic exists below the ledger).
Absolute tablet coords scale 0-32767; mapped from the fixed 1920x1080 frame.
Every send is timed (per-event round-trip feeds the metrics) and appended to
an operator-supplied send log — the artifact the kill test adjudicates.
"""
from __future__ import annotations

import subprocess
import time
from dataclasses import dataclass, field
from typing import Callable

WIDTH, HEIGHT = 1920, 1080
ABS_MAX = 32767


def to_absolute(x: int, y: int) -> tuple[int, int]:
    """Fixed-frame pixels -> tablet absolute range (clamped)."""
    ax = min(ABS_MAX, max(0, round(x / WIDTH * ABS_MAX)))
    ay = min(ABS_MAX, max(0, round(y / HEIGHT * ABS_MAX)))
    return ax, ay


def _default_runner(cmd: list[str]) -> str:
    """Real runner: loud on transport failure (§8 — a dead monitor command
    must raise, never return empty output for the ledger to ack). Custom
    injected runners keep the str-returning contract; they should raise
    on failure the same way."""
    import subprocess
    p = subprocess.run(cmd, capture_output=True, text=True, timeout=60)
    if p.returncode != 0:
        from harness.vms.pool import LibvirtConnectionError
        raise LibvirtConnectionError(
            f"qemu-monitor-command failed: {p.stderr.strip()[:200]}")
    return p.stdout


@dataclass
class QmpTransport:
    """Sends input events. runner(cmd: list[str]) -> str injects fakes.

    Default argv is local virsh; pass argv_prefix for the ssh hop
    (live/virsh_argv.prefix_for(..., ssh_hop=True)). Plane discipline: the
    hop is the CONTROL plane (~280ms/event measured) — per-event input over
    it works and every send's dt_s measures the true cost honestly, but the
    host-local monitor socket stays the fast path (9-16ms) for anything at
    watchdog cadence. Never route per-event input through the hop silently:
    construct the prefix explicitly at the call site.
    """
    domain: str = "ubuntu-2204-stage0"
    uri: str = "qemu:///system"
    runner: Callable[[list[str]], str] | None = None
    send_log: list = field(default_factory=list)
    time_fn: Callable[[], float] | None = None
    argv_prefix: list[str] | None = None

    def _now(self) -> float:
        import time as _t
        return (self.time_fn or _t.monotonic)()

    def _run(self, payload: str) -> tuple[str, float]:
        if self.argv_prefix is not None and self.argv_prefix[:1] == ["ssh"]:
            # ssh-hop: the local ssh joins argv with spaces and the remote
            # shell word-splits, eating the JSON quotes (quote-eating
            # family, 3rd member: QGA --cmd, virsh-argv, now QMP-monitor —
            # every pointer event over the hop died in libvirt's JSON
            # parser while ledgers acked). Quote the --cmd payload into ONE
            # element. Local path passes argv lists straight to subprocess
            # (no shell), where quoting would corrupt the payload — hence
            # conditional on the hop, never blanket.
            import shlex
            payload = shlex.quote(payload)
        cmd = (list(self.argv_prefix) if self.argv_prefix is not None
               else ["virsh", "-c", self.uri]) + \
            ["qemu-monitor-command", self.domain, "--cmd", payload]
        run = self.runner or _default_runner
        t0, out = self._now(), run(cmd)
        return out, self._now() - t0

    @staticmethod
    def _press_payload(ax: int, ay: int) -> str:
        import json
        return json.dumps({"execute": "input-send-event", "arguments": {
            "events": [{"type": "abs", "data": {"axis": "x", "value": ax}},
                       {"type": "abs", "data": {"axis": "y", "value": ay}},
                       {"type": "btn", "data": {"down": True,
                                                "button": "left"}}]}})

    @staticmethod
    def _release_payload() -> str:
        import json
        return json.dumps({"execute": "input-send-event", "arguments": {
            "events": [{"type": "btn", "data": {"down": False,
                                                "button": "left"}}]}})

    def press(self, x: int, y: int) -> float:
        """Move + button-down as one monitor command. Returns round-trip s."""
        ax, ay = to_absolute(x, y)
        out, dt = self._run(self._press_payload(ax, ay))
        self.send_log.append({"event": "press", "x": x, "y": y,
                              "ax": ax, "ay": ay, "dt_s": dt, "reply": out[:80]})
        return dt

    def move(self, x: int, y: int) -> float:
        """Absolute move with no button state — the pre-shot positioning move.

        Protocol fix (Session 4): capturing the before-shot AFTER the move
        puts the cursor in BOTH frames, cancelling cursor motion out of the
        verify pair. Without this, cursor-at-aim pixels read as action effect
        in near-field analysis.
        """
        import json
        ax, ay = to_absolute(x, y)
        payload = json.dumps({"execute": "input-send-event", "arguments": {
            "events": [{"type": "abs", "data": {"axis": "x", "value": ax}},
                       {"type": "abs", "data": {"axis": "y", "value": ay}}]}})
        out, dt = self._run(payload)
        self.send_log.append({"event": "move", "x": x, "y": y,
                              "ax": ax, "ay": ay, "dt_s": dt, "reply": out[:80]})
        return dt

    def release(self, x: int, y: int) -> float:
        """Button-up as its own monitor command. Returns round-trip s."""
        out, dt = self._run(self._release_payload())
        self.send_log.append({"event": "release", "x": x, "y": y,
                              "dt_s": dt, "reply": out[:80]})
        return dt
