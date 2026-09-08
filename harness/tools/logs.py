"""get_logs per-OS standard pull (H-R §9)."""
from __future__ import annotations


def get_log_commands(os_name: str) -> list[str]:
    k = os_name.lower()
    if "window" in k:
        return ["wevtutil qe Application /c:50 /f:text",
                "%APPDATA%\\PAIcom\\*.log", "launcher.log", "launcher-runtime.log"]
    return ["journalctl --no-pager -n 200", "dmesg | tail -n 100",
            "tail -n 200 launcher.log", "tail -n 200 launcher-runtime.log",
            "find ~ -name '*.log' -mmin -5"]
