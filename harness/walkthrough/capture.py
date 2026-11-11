"""Capture helpers for the walkthrough engine.

Wraps guest framebuffer capture (PNG/JPEG bytes) with format sniffing and
base64 transport. The actual byte source is injected (capture_fn) so the same
code runs against a live guest, a recorded session, or a test fixture.
"""
from __future__ import annotations

import base64
import hashlib
from dataclasses import dataclass
from typing import Callable

def _sniff_format(header: bytes) -> str:
    if header[:2] == b"P6" or header[:2] == b"P5":
        return "ppm"
    if header[:8] == b"\x89PNG\r\n\x1a\n":
        return "png"
    return "unknown"


def _sniff_extended(header: bytes) -> str:
    # jpeg magic
    if header[:3] == b"\xff\xd8\xff":
        return "jpeg"
    return _sniff_format(header)


@dataclass
class CapturedFrame:
    payload: bytes
    fmt: str  # "png" | "ppm" | "jpeg" | "unknown"
    sha256: str
    ts: float

    @property
    def is_valid(self) -> bool:
        return self.fmt in ("png", "ppm", "jpeg")


def capture_frame(capture_fn: Callable[[], bytes], time_fn=lambda: __import__("time").monotonic()) -> CapturedFrame:
    payload = bytes(capture_fn())
    fmt = _sniff_extended(payload[:8])
    return CapturedFrame(payload=payload, fmt=fmt,
                        sha256=hashlib.sha256(payload).hexdigest(),
                        ts=time_fn())


def decode_base64_frame(b64: str) -> bytes:
    """Decode a base64-encoded frame (as transported over SSH stdout)."""
    return base64.b64decode(b64, validate=False)


def make_capture_from_bytes(frames: list[bytes]):
    """Test helper: round-robin capture source over a list of payloads."""
    idx = {"i": 0}

    def fn():
        if not frames:
            return b""
        f = frames[idx["i"] % len(frames)]
        idx["i"] += 1
        return f

    return fn
