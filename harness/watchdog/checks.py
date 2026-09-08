"""Watchdog deterministic checks (§5): qga, framebuffer hash, log growth, lifecycle."""
from __future__ import annotations

import hashlib
from collections import deque
from dataclasses import dataclass


@dataclass
class WatchdogStatus:
    vm_id: str
    qga_state: str  # ok | unknown
    fb_hash: str
    log_bytes: int
    console_owner: str = "agent"


def qga_check(responding: bool) -> str:
    """Missing agent = 'unknown', never healthy (golden images assert QGA)."""
    return "ok" if responding else "unknown"


def framebuffer_hash(gray_downscaled: bytes) -> str:
    """Hash of already-downscaled grayscale bytes (caller downscales).

    Downscale+grayscale REDUCES cursor-blink/clock-second deltas but does not
    eliminate them (Session 2 measured fb_distinct=2 on a blinking terminal).
    Stability judgments belong to HashWindow below, never to == comparisons.
    """
    return hashlib.md5(gray_downscaled).hexdigest()


def downscale_grayscale_hash(rgb: bytes, width: int, height: int) -> str:
    """Deterministic hash from raw RGB bytes: grayscale + 160x90 downsample.

    Pure-stdlib nearest-neighbor so tests need no guest.
    """
    import struct
    # rgb length must be w*h*3
    gw, gh = 160, 90
    out = bytearray(gw * gh)
    for y in range(gh):
        sy = min(height - 1, int(y * height / gh))
        for x in range(gw):
            sx = min(width - 1, int(x * width / gw))
            i = (sy * width + sx) * 3
            r, g, b = rgb[i], rgb[i + 1], rgb[i + 2]
            out[y * gw + x] = (r * 30 + g * 59 + b * 11) // 100
    return hashlib.md5(bytes(out)).hexdigest()


def log_delta(prev_bytes: int, cur_bytes: int) -> int:
    return max(0, cur_bytes - prev_bytes)


def detect_divergence(claimed_active: bool, fb_stable: bool, delta_bytes: int) -> bool:
    """Static framebuffer is NOT standalone evidence of stuck.

    Divergence fires only on cross-reference: worker claims active work AND
    framebuffer stable AND log delta zero.
    """
    return bool(claimed_active and fb_stable and delta_bytes == 0)


class HashWindow:
    """Rolling-set stability: stable = member of the last-K hashes.

    A two-state periodic alternation (terminal cursor blink) lands inside the
    set and reads stable; genuine change produces a novel hash and reads
    unstable. Real change is novel; blink is repetitive — that is the
    discriminator. Cold window (no history) reads unstable: safer against
    false divergence at startup.
    """

    def __init__(self, k: int = 4):
        self._window: deque[str] = deque(maxlen=max(1, k))

    def observe(self, fb_hash: str) -> bool:
        """Record one hash; return whether it was already recently seen."""
        stable = fb_hash in self._window
        self._window.append(fb_hash)
        return stable

    def __len__(self) -> int:
        return len(self._window)
