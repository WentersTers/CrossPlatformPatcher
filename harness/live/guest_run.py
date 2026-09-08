"""Tier-0 remote driver: QGA guest-exec behind launch()'s Popen shape.

launch.launch(cmd, log, timeout) takes popen_factory; this module adapts QGA
guest-exec/guest-exec-status to that interface so the SAME Tier-0 code drives
local processes (tests) and guest processes (Session 6): exec returns
immediately with a QGA pid, poll maps exited-status, communicate returns the
captured output of the final status read.

Timeout kills: the guest command should be wrapped in timeout(1) (e.g.
["timeout","300","bash","run.sh"]) so termination is guaranteed even if this
side goes away; kill_fn (best-effort pkill) covers the gap. QGA has no
terminate primitive — documented, not hidden.
"""
from __future__ import annotations

from typing import Any, Callable


class GuestRunFactory:
    """exec_fn(argv) -> qga pid (int).
    status_fn(pid) -> {"exited": bool, "exitcode": int|None,
                       "out": str, "err": str}.
    kill_fn(pid) -> None, best effort (may be None: timeout(1) owns death).
    """

    def __init__(self, exec_fn: Callable[[list[str]], int],
                 status_fn: Callable[[int], dict[str, Any]],
                 kill_fn: Callable[[int], None] | None = None):
        self._exec = exec_fn
        self._status = status_fn
        self._kill = kill_fn

    def __call__(self, cmd: list[str]) -> _QgaProc:
        return _QgaProc(self, self._exec(list(cmd)))


class _QgaProc:
    def __init__(self, factory: GuestRunFactory, pid: int):
        self._factory = factory
        self._pid = pid
        self._code: int | None = None
        self._out = ""
        self._err = ""

    def poll(self) -> int | None:
        if self._code is not None:
            return self._code
        st = self._factory._status(self._pid)
        if st.get("exited"):
            self._code = st.get("exitcode", -1)
            self._out = st.get("out", "")
            self._err = st.get("err", "")
            return self._code
        return None

    def communicate(self) -> tuple[str, str]:
        self.poll()
        return self._out, self._err

    def kill(self) -> None:
        if self._factory._kill is not None:
            self._factory._kill(self._pid)

    @property
    def returncode(self) -> int | None:
        return self._code
