"""screenshot() guards (§8): PPM/PNG sniff, never hardcoded; timestamp recorded."""
from __future__ import annotations

import time
from dataclasses import dataclass


@dataclass
class Shot:
    fmt: str
    ts: float
    payload: bytes


def sniff_format(header: bytes) -> str:
    if header[:2] == b"P6" or header[:2] == b"P5":
        return "ppm"
    if header[:8] == b"\x89PNG\r\n\x1a\n":
        return "png"
    return "unknown"


def take_screenshot(capture_fn, time_fn=time.monotonic) -> Shot:
    payload = capture_fn()
    fmt = sniff_format(bytes(payload[:8]))
    if fmt == "unknown":
        raise ValueError("screenshot format not PPM/PNG")
    return Shot(fmt, time_fn(), bytes(payload))
