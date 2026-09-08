"""Tier-0 launcher telemetry, host side (Session 6): timestamped stdout/stderr
tee, exit-code capture, process-liveness polling, timeout kill.

Runs the app command, mirrors output to a log with start/end markers and
per-poll liveness samples, and returns the verdict inputs Gate 1 needs
(exit code + log path). A game that never exits hits `timeout_s`: the
process is killed and the verdict records timeout, not a hang.
"""
from __future__ import annotations

import subprocess
import time
from datetime import datetime, timezone
from pathlib import Path
from typing import Callable


def _iso() -> str:
    return datetime.now(timezone.utc).isoformat()


def launch(cmd: list[str], log_path: str | Path, timeout_s: float,
           poll_s: float = 5.0,
           popen_factory: Callable | None = None,
           sleep_fn=None, time_fn=None) -> dict:
    """Run cmd to exit-or-timeout. Returns exit_code (None on timeout),
    timed_out, log_path, alive_samples, duration_s."""
    _sleep = sleep_fn or time.sleep
    _clock = time_fn or time.monotonic
    log = Path(log_path)
    log.parent.mkdir(parents=True, exist_ok=True)
    factory = popen_factory or (lambda c: subprocess.Popen(
        c, stdout=subprocess.PIPE, stderr=subprocess.STDOUT, text=True))
    start, start_iso = _clock(), _iso()
    proc = factory(cmd)
    alive = 0
    timed_out = False
    while proc.poll() is None:
        if _clock() - start >= timeout_s:
            timed_out = True
            try:
                proc.kill()
            except Exception:
                pass
            break
        _sleep(poll_s)
        alive += 1
    out, _ = proc.communicate()
    code = None if timed_out else proc.returncode
    with log.open("w", encoding="utf-8") as f:
        f.write(f"[tier0] start={start_iso} cmd={cmd}\n")
        f.write(out or "")
        if not (out or "").endswith("\n"):
            f.write("\n")
        f.write(f"[tier0] end={_iso()} exit={code} timed_out={timed_out} "
                f"alive_samples={alive} duration_s={round(_clock() - start, 1)}\n")
    return {"exit_code": code, "timed_out": timed_out,
            "log_path": str(log), "alive_samples": alive,
            "duration_s": round(_clock() - start, 1)}
