"""Gate 2 — deterministic, per-OS calibrated (§6).

- SSIM vs goldens/{os}/{step}.png, threshold in each golden's sidecar
- OCR scoped to the ACTIVE WINDOW region (full-desktop OCR false-positives
  on background log viewers showing old errors)
- OCR must contain expected_text; must NOT match ERROR|Exception|DllNotFound|libvosk
  within the active window.
"""
from __future__ import annotations

import json
import math
import re
from dataclasses import dataclass, field
from pathlib import Path
from typing import Callable

import numpy as np

FORBIDDEN_RE = re.compile(r"ERROR|Exception|DllNotFound|libvosk")

# NO_VISUAL_CHANGE threshold (Session-3 measured bands, n=6 no-change):
# idle 1.0, banner 0.99986, tick 0.99954, cursor 0.99805, defocus 0.97752
# vs change band: window-open 0.21908 (n=1). 0.95 sits below the entire
# no-change band with margin and far above gross change.
# KNOWN RESIDUAL: magnitude alone cannot separate defocus-noise (0.978)
# from a small-dialog signal (~0.97?) — inverted order. That class needs
# localize_change() region analysis, not threshold tuning. Do not move this
# constant on single samples; move it on band distributions.
NO_CHANGE_THRESHOLD = 0.95


@dataclass
class Gate2Result:
    passed: bool
    failures: list[str] = field(default_factory=list)
    evidence: dict = field(default_factory=dict)


def ssim_gray(a: np.ndarray, b: np.ndarray) -> float:
    """Whole-image SSIM (luminance/contrast/structure) on uint8 grayscale.

    Avoids skimage dependency; returns 1.0 for identical images.
    """
    if a.shape != b.shape:
        raise ValueError(f"shape mismatch {a.shape} vs {b.shape}")
    x = a.astype(np.float64)
    y = b.astype(np.float64)
    mux, muy = x.mean(), y.mean()
    sigx2 = ((x - mux) ** 2).mean()
    sigy2 = ((y - muy) ** 2).mean()
    sigxy = ((x - mux) * (y - muy)).mean()
    L = 255.0
    c1, c2 = (0.01 * L) ** 2, (0.03 * L) ** 2
    num = (2 * mux * muy + c1) * (2 * sigxy + c2)
    den = (mux ** 2 + muy ** 2 + c1) * (sigx2 + sigy2 + c2)
    if den == 0:
        return 1.0 if num == 0 else 0.0
    v = num / den
    return float(max(0.0, min(1.0, v)))


def to_gray(img: np.ndarray) -> np.ndarray:
    if img.ndim == 2:
        return img
    if img.shape[2] >= 3:
        # BT.601 luma
        g = img[:, :, 0] * 0.30 + img[:, :, 1] * 0.59 + img[:, :, 2] * 0.11
        return g.astype(np.uint8)
    return img[:, :, 0]


def load_sidecar(sidecar_path: str | Path) -> dict:
    p = Path(sidecar_path)
    if not p.exists():
        return {}
    return json.loads(p.read_text(encoding="utf-8"))


def check_ssim(actual: np.ndarray, golden: np.ndarray, threshold: float) -> tuple[bool, float]:
    score = ssim_gray(to_gray(actual), to_gray(golden))
    return score >= threshold, score


def crop_roi(img: np.ndarray, roi: tuple[int, int, int, int] | None) -> np.ndarray:
    """roi = (x, y, w, h). None = full image (discouraged; tests pass explicit roi)."""
    if roi is None:
        return img
    x, y, w, h = roi
    return img[y: y + h, x: x + w]


def localize_change(before: np.ndarray, after: np.ndarray,
                    at_xy: tuple[int, int] | None = None,
                    radius: int = 100, thresh: int = 10) -> dict:
    """Where did a before/after pair actually change? Answers whether an
    action's effect landed where aimed (Session-2 finding: an 'empty' click
    scored 0.97752 from terminal defocus-restyle 500px away while the click
    point itself measured exactly 0.0).

    Returns changed-pixel count, bounding box, max delta, and — when at_xy
    is given — mean delta near vs far from the aim point.
    """
    a = to_gray(before).astype(int)
    b = to_gray(after).astype(int)
    if a.shape != b.shape:
        raise ValueError(f"shape mismatch {a.shape} vs {b.shape}")
    d = np.abs(a - b)
    changed = d > thresh
    out: dict = {"changed_px": int(changed.sum()),
                 "total_px": int(d.size),
                 "max_delta": float(d.max())}
    ys, xs = np.where(changed)
    out["bbox"] = ([int(xs.min()), int(ys.min()), int(xs.max()), int(ys.max())]
                   if len(xs) else None)
    if at_xy is not None:
        yy, xx = np.mgrid[0:d.shape[0], 0:d.shape[1]]
        near = (xx - at_xy[0]) ** 2 + (yy - at_xy[1]) ** 2 < radius ** 2
        out["mean_near"] = float(d[near].mean())
        out["mean_far"] = float(d[~near].mean())
    return out


def check_ocr(ocr_text: str, expected_text: str | None) -> list[str]:
    failures = []
    if expected_text and expected_text not in ocr_text:
        failures.append(f"OCR missing expected_text {expected_text!r}")
    if FORBIDDEN_RE.search(ocr_text):
        failures.append("OCR matched forbidden ERROR|Exception|DllNotFound|libvosk in active window")
    return failures


OcrFunc = Callable[[np.ndarray], str]


def evaluate(actual: np.ndarray, golden: np.ndarray, sidecar: dict,
             ocr_func: OcrFunc | None = None,
             active_window_roi: tuple[int, int, int, int] | None = None,
             expected_text: str | None = None,
             step_name: str = "") -> Gate2Result:
    failures: list[str] = []
    evidence: dict = {"step": step_name}
    threshold = float(sidecar.get("ssim_threshold", 0.90))
    evidence["ssim_threshold"] = threshold
    ok, score = check_ssim(actual, golden, threshold)
    evidence["ssim"] = score
    if not ok:
        failures.append(f"SSIM {score:.4f} < threshold {threshold}")

    if ocr_func is not None:
        roi_img = crop_roi(actual, active_window_roi)
        text = ocr_func(roi_img)
        evidence["ocr_text"] = text[:500]
        evidence["ocr_roi"] = active_window_roi
        failures.extend(check_ocr(text, expected_text))
    elif expected_text is not None:
        failures.append("expected_text required but no ocr_func provided")

    return Gate2Result(passed=not failures, failures=failures, evidence=evidence)
