"""inject_audio guards (§8/H-R §4): libasound2t64 on 24.04+, ldd before first launch,
16kHz mono s16le, sox gain 0.4-0.7."""
from __future__ import annotations

from dataclasses import dataclass


@dataclass
class AudioParams:
    rate: int = 16000
    channels: int = 1
    fmt: str = "s16le"


def validate_audio_params(rate: int, channels: int, fmt: str) -> list[str]:
    failures = []
    if rate != 16000:
        failures.append(f"rate {rate} != 16000")
    if channels != 1:
        failures.append("must be mono")
    if fmt != "s16le":
        failures.append(f"fmt {fmt!r} != s16le")
    return failures


def validate_gain(gain: float) -> bool:
    return 0.4 <= gain <= 0.7


def check_libasound(requested_os: str, installed: list[str]) -> list[str]:
    if "24.04" in requested_os or "ubuntu" in requested_os.lower():
        if "libasound2t64" not in installed:
            return ["missing libasound2t64 (Ubuntu 24.04+)"]
    return []
