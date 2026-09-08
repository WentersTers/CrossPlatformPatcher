"""QGA-over-hop guest execution: the Session-7 launch channel (item 3).

Builds the exec/status/kill callables behind launch()'s Popen shape
(live/guest_run.py GuestRunFactory) out of `virsh qemu-agent-command`
over the ssh hop. Quoting discipline (Session-7 seam finding): Windows
ssh joins argv with spaces and the remote shell word-splits, so the
--cmd JSON payload is shlex.quote()d into ONE argv element — unquoted
payloads die in libvirt's JSON parser with the quotes eaten by bash.

- env: QGA guest-exec has no environment parameter, so env (notably
  DISPLAY=:0 past (c)) is prepended as `env K=V ...` argv. Explicit.
- kill: QGA has no terminate primitive; kill() is best-effort guest-exec
  `kill -9`. The command should still be wrapped in timeout(1) — timeout
  owns death, kill covers the gap (guest_run.py contract).
- output: out-data/err-data arrive base64; decoded to str here so the
  factory surface stays plain text.
"""
from __future__ import annotations

import base64
import json
import shlex
import subprocess
from typing import Any, Callable


class QgaError(Exception):
    """Guest-agent failure: agent down, bad pid, undecodable output."""


def _libvirt_error(msg: str) -> Exception:
    from harness.vms.pool import LibvirtConnectionError
    return LibvirtConnectionError(msg)


class QgaBackend:
    """virsh_prefix: live/virsh_argv.prefix_for(...) list.
    run(cmd: list[str]) -> (rc, out, err); defaults to local subprocess.
    """

    def __init__(self, domain: str, virsh_prefix: list[str],
                 run: Callable[[list[str]], tuple] | None = None,
                 env: dict[str, str] | None = None):
        self.domain = domain
        self.prefix = list(virsh_prefix)
        self.env = dict(env or {})
        self._run = run or self._subprocess

    @staticmethod
    def _subprocess(cmd: list[str]) -> tuple[int, str, str]:
        try:
            p = subprocess.run(cmd, capture_output=True, text=True,
                               timeout=120)
        except OSError as e:
            raise _libvirt_error(
                f"qga transport not runnable: {e}") from e
        return p.returncode, p.stdout, p.stderr

    def _agent(self, cmd_obj: dict) -> Any:
        payload = shlex.quote(json.dumps(cmd_obj, separators=(",", ":")))
        rc, out, err = self._run(
            [*self.prefix, "qemu-agent-command", self.domain,
             "--cmd", payload])
        if rc != 0:
            raise _libvirt_error(
                f"qemu-agent-command failed: {err.strip()[:300]}")
        try:
            return json.loads(out)["return"]
        except (ValueError, KeyError) as e:
            raise QgaError(f"unparseable agent reply: {out[:200]!r}") from e

    def _argv(self, argv: list[str]) -> list[str]:
        env_prefix: list[str] = []
        for k, v in self.env.items():
            env_prefix += [f"{k}={v}"]
        if env_prefix:
            return ["env", *env_prefix, *argv]
        return list(argv)

    def exec(self, argv: list[str]) -> int:
        """guest-exec argv -> agent pid. Loud on agent/transport failure."""
        if not argv:
            raise QgaError("exec needs a non-empty argv")
        full = self._argv(list(argv))
        try:
            ret = self._agent({"execute": "guest-exec",
                               "arguments": {"path": full[0],
                                             "arg": full[1:],
                                             "capture-output": True}})
            return int(ret["pid"])
        except KeyError as e:
            raise QgaError(f"guest-exec returned no pid: {ret!r}") from e

    def status(self, pid: int) -> dict:
        """Poll guest-exec-status -> GuestRunFactory shape (decoded str)."""
        ret = self._agent({"execute": "guest-exec-status",
                           "arguments": {"pid": int(pid)}})
        if not isinstance(ret, dict) or "exited" not in ret:
            raise QgaError(f"bad exec-status for pid {pid}: {ret!r}")
        out, err = "", ""
        try:
            if ret.get("out-data"):
                out = base64.b64decode(ret["out-data"]).decode("utf-8",
                                                               errors="replace")
            if ret.get("err-data"):
                err = base64.b64decode(ret["err-data"]).decode("utf-8",
                                                               errors="replace")
        except (ValueError, UnicodeError) as e:
            raise QgaError(f"undecodable output for pid {pid}") from e
        return {"exited": bool(ret["exited"]),
                "exitcode": ret.get("exitcode"),
                "out": out, "err": err}

    def kill(self, pid: int) -> None:
        """Best-effort SIGKILL via a fresh guest-exec. Never raises: a dead
        pid or racy kill must not fail the timeout-kill path (§8: kill is
        the backstop, timeout(1) owns death)."""
        try:
            self._agent({"execute": "guest-exec",
                         "arguments": {"path": "/bin/kill",
                                       "arg": ["-9", str(int(pid))],
                                       "capture-output": True}})
        except Exception:
            pass
