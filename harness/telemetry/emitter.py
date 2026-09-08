"""Synthetic telemetry emitter: mock-world stand-in for Tier-1 in-app hooks.

Reference shape for the real emitter (Phase 0 branch): async buffered writer,
env-gated by PAICOM_TELEMETRY=1, sequence-numbered, never blocking the loop.
Tests drive the consumer through this; `drop_next` simulates event loss.
"""
from __future__ import annotations

import json
from datetime import datetime, timezone
from typing import Callable


def _iso(dt: datetime) -> str:
    return dt.astimezone(timezone.utc).isoformat().replace("+00:00", "Z")


class SyntheticEmitter:
    def __init__(self, now_fn: Callable[[], datetime] | None = None, pid: int = 3121):
        self._now = now_fn or (lambda: datetime.now(timezone.utc))
        self.pid = pid
        self._seq = 0
        self._drop = 0
        self.lines: list[str] = []

    def drop_next(self, n: int = 1) -> None:
        """Simulate dropped writes (full disk, killed writer): seq WILL gap."""
        self._drop += n

    def _emit(self, event: str, **fields) -> dict | None:
        seq = self._seq
        self._seq += 1
        if self._drop > 0:
            self._drop -= 1
            return None  # written seq is lost; consumer sees a gap
        obj = {"v": 1, "seq": seq, "ts": _iso(self._now()),
               "pid": self.pid, "event": event, **fields}
        self.lines.append(json.dumps(obj))
        return obj

    def heartbeat(self, loop: str = "main") -> dict | None:
        return self._emit("heartbeat", loop=loop)

    def state_change(self, frm: str, to: str, trigger: str = "",
                     settle_ms: int = 500) -> dict | None:
        return self._emit("state_change", **{"from": frm, "to": to,
                                             "trigger": trigger,
                                             "settle_ms": settle_ms})

    def animation_start(self, name: str, state: str, frames: int = 8) -> dict | None:
        return self._emit("animation_start", name=name, state=state, frames=frames)

    def command_exec(self, cmd: str, status: str = "ok", arg: str = "") -> dict | None:
        return self._emit("command_exec", cmd=cmd, status=status, arg=arg)

    def speak_start(self, text: str) -> dict | None:
        return self._emit("speak_start", text=text)

    def speak_end(self, duration_ms: int = 0) -> dict | None:
        return self._emit("speak_end", duration_ms=duration_ms)

    def vosk_result(self, text: str, confidence: float = 0.0) -> dict | None:
        return self._emit("vosk_result", text=text, confidence=confidence)

    def error(self, source: str, detail: str) -> dict | None:
        return self._emit("error", source=source, detail=detail)
