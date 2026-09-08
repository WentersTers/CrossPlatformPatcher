"""click(x,y) guards (§8): 120s freshness, 1920x1080 bounds, QMP press/release
split, post-action SSIM -> NO_VISUAL_CHANGE, USB tablet absolute coords."""
from __future__ import annotations

from dataclasses import dataclass
from typing import Callable

import numpy as np

from harness.gates.gate2_ssim_ocr import (NO_CHANGE_THRESHOLD, ssim_gray,
                                          to_gray)
from harness.tools.guards import StaleScreenshotError

WIDTH, HEIGHT = 1920, 1080
FRESHNESS_S = 120.0


@dataclass
class ClickResult:
    ok: bool
    no_visual_change: bool = False
    evidence: dict | None = None


def check_bounds(x: int, y: int) -> None:
    if not (0 <= x < WIDTH and 0 <= y < HEIGHT):
        raise ValueError(f"click ({x},{y}) outside {WIDTH}x{HEIGHT}")


def check_freshness(screenshot_ts: float, now_ts: float) -> None:
    if now_ts - screenshot_ts > FRESHNESS_S:
        raise StaleScreenshotError("STALE_SCREENSHOT: older than 120s")


def click(x: int, y: int, screenshot_ts: float, now_ts: float,
          qmp_press: Callable[[int, int], None],
          qmp_release: Callable[[int, int], None],
          before_img: np.ndarray | None = None,
          after_img: np.ndarray | None = None,
          tablet_absolute: bool = True,
          ssim_no_change_threshold: float = NO_CHANGE_THRESHOLD) -> ClickResult:
    """Press and release as SEPARATE QMP calls (merged = silent success, no click)."""
    check_bounds(x, y)
    check_freshness(screenshot_ts, now_ts)
    if not tablet_absolute:
        raise ValueError("USB tablet absolute coords required in domain XML")
    qmp_press(x, y)      # separate call 1
    qmp_release(x, y)    # separate call 2
    no_change = False
    score = None
    if before_img is not None and after_img is not None:
        score = ssim_gray(to_gray(before_img), to_gray(after_img))
        no_change = score >= ssim_no_change_threshold
    return ClickResult(ok=True, no_visual_change=no_change,
                       evidence={"ssim": score})


def verify_effect(before_img: np.ndarray, after_img: np.ndarray,
                  x: int, y: int, radius: int = 100,
                  change_threshold: float = NO_CHANGE_THRESHOLD,
                  identical_floor: float = 0.999) -> dict:
    """Two-tier click verify: magnitude first, region analysis in the gray zone.

    - ssim < change_threshold -> "change" (gross effect, no region work)
    - ssim >= identical_floor -> "none" (nothing, not even cursor)
    - gray zone in between -> localize_change at the aim point: "effect" iff
      the near-field mean dominates the far field 3:1 (Session-3 defocus case:
      near 0.0 vs far 1.27 reads noise; a real local flip reads effect).
    Cursor caveat: cursor motion AT the aim point reads as effect here. The
    driver cancels it by protocol (before-shot AFTER the positioning move, so
    the cursor is in both frames). Without that ordering, treat "effect" on
    empty clicks as suspect — the evidence dict carries the numbers either way.
    """
    from harness.gates.gate2_ssim_ocr import localize_change
    score = ssim_gray(to_gray(before_img), to_gray(after_img))
    if score < change_threshold:
        return {"verdict": "change", "ssim": score, "evidence": {}}
    if score >= identical_floor:
        return {"verdict": "none", "ssim": score, "evidence": {}}
    loc = localize_change(before_img, after_img, at_xy=(x, y), radius=radius)
    far = max(loc["mean_far"], 0.05)
    if loc["changed_px"] > 0 and loc["mean_near"] > 3 * far:
        return {"verdict": "effect", "ssim": score, "evidence": loc}
    return {"verdict": "noise", "ssim": score, "evidence": loc}
