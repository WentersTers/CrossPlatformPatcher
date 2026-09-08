"""exec(cmd) guards (§8): Windows GUI auto-wrap in schtasks /run (Session 0);
always returns {exit_code, stdout, stderr}."""
from __future__ import annotations

from dataclasses import dataclass


@dataclass
class ExecResult:
    exit_code: int
    stdout: str
    stderr: str
    wrapped: bool = False
    wrapped_cmd: str = ""


def wrap_windows_gui(cmd: str, is_windows: bool, is_gui: bool) -> tuple[str, bool]:
    if is_windows and is_gui:
        return f'schtasks /run /tn "HarnessGUI\\{cmd}"', True
    return cmd, False


def build_result(exit_code: int, stdout: str, stderr: str,
                 wrapped: bool = False, wrapped_cmd: str = "") -> ExecResult:
    return ExecResult(exit_code, stdout, stderr, wrapped, wrapped_cmd)
