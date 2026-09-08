"""ROI detection (pet_roi): fixed config -> template-locate -> VLM bbox (§6.5).

ROI tracked frame-to-frame (pets move when acting).
"""
from __future__ import annotations

from typing import Callable, Optional

import cv2
import numpy as np


def fixed_roi(config: dict | None) -> tuple[int, int, int, int] | None:
    if not config:
        return None
    roi = config.get("pet_roi") or config.get("fixed_roi")
    if roi and len(roi) == 4:
        return (int(roi[0]), int(roi[1]), int(roi[2]), int(roi[3]))
    return None


def template_locate(screenshot: np.ndarray, templates: list[np.ndarray]) -> tuple[int, int, int, int] | None:
    """Coarse multi-scale template match of best-scoring state frame over full shot."""
    gray = cv2.cvtColor(screenshot, cv2.COLOR_BGR2GRAY) if screenshot.ndim == 3 else screenshot
    best: tuple[float, tuple[int, int, int, int]] | None = None
    for tmpl in templates:
        tg = cv2.cvtColor(tmpl, cv2.COLOR_BGR2GRAY) if tmpl.ndim == 3 else tmpl
        for s in (0.8, 1.0, 1.2):
            nw, nh = max(1, int(tg.shape[1] * s)), max(1, int(tg.shape[0] * s))
            if nh > gray.shape[0] or nw > gray.shape[1]:
                continue
            r = cv2.resize(tg, (nw, nh))
            m = cv2.matchTemplate(gray, r, cv2.TM_CCOEFF_NORMED)
            _, mx, _, ml = cv2.minMaxLoc(m)
            if best is None or mx > best[0]:
                best = (float(mx), (int(ml[0]), int(ml[1]), nw, nh))
    if best and best[0] >= 0.3:
        return best[1]
    return None


VlmBboxFn = Callable[[np.ndarray], Optional[tuple[int, int, int, int]]]


def locate_pet_roi(screenshot: np.ndarray, config: dict | None = None,
                   templates: list[np.ndarray] | None = None,
                   vlm_bbox_fn: VlmBboxFn | None = None,
                   last_roi: tuple[int, int, int, int] | None = None) -> tuple[int, int, int, int] | None:
    """Priority: (1) fixed config, (2) template-locate, (3) VLM bbox last resort."""
    if (r := fixed_roi(config)) is not None:
        return r
    if templates:
        if (r := template_locate(screenshot, templates)) is not None:
            return r
    if vlm_bbox_fn is not None:
        return vlm_bbox_fn(screenshot)
    return last_roi
