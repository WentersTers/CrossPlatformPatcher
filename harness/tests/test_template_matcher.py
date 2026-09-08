"""Tier A: Canny edges survive compositing; multi-scale; per-state thresholds (D9)."""
import cv2
import numpy as np

from harness.gates.gate2_5_state.template_matcher import StateTemplateMatcher


def _sprite(kind: str, size: int = 60) -> np.ndarray:
    img = np.zeros((size, size, 3), dtype=np.uint8)
    if kind == "idle":
        cv2.circle(img, (size // 2, size // 2), 15, (255, 255, 255), 2)
    elif kind == "listening":
        cv2.circle(img, (size // 2, size // 2), 15, (255, 255, 255), 2)
        cv2.line(img, (size // 2 + 15, size // 2 - 10), (size // 2 + 22, size // 2 - 18), (255, 255, 255), 3)
    elif kind == "speaking":
        cv2.rectangle(img, (15, 15), (size - 15, size - 15), (255, 255, 255), 2)
    return img


def _canvas(sprite: np.ndarray, tint: int = 0) -> np.ndarray:
    canvas = np.full((120, 120, 3), tint, dtype=np.uint8)
    canvas[30:90, 30:90] = sprite
    return canvas


def test_tier_a_authoritative_above_threshold():
    goldens = {"idle": [_sprite("idle")], "listening": [_sprite("listening")]}
    m = StateTemplateMatcher(goldens, {"idle": {"min_confidence": 0.4},
                                       "listening": {"min_confidence": 0.4}})
    shot = _canvas(_sprite("idle"))
    r = m.classify(shot, (30, 30, 60, 60))
    assert r.state == "idle" and not r.needs_vlm and r.method == "template"
    assert r.score >= 0.4


def test_edges_survive_compositing_shift():
    """Same structure on different background tint still matches (edges, not pixels)."""
    goldens = {"idle": [_sprite("idle")]}
    m = StateTemplateMatcher(goldens, {"idle": {"min_confidence": 0.4}})
    for tint in (0, 40, 90):
        r = m.classify(_canvas(_sprite("idle"), tint), (30, 30, 60, 60))
        assert r.state == "idle", f"tint {tint} scored {r.score}"


def test_ambiguous_below_threshold_needs_vlm():
    goldens = {"idle": [_sprite("idle")]}
    m = StateTemplateMatcher(goldens, {"idle": {"min_confidence": 0.9999}})
    r = m.classify(_canvas(_sprite("speaking")), (30, 30, 60, 60))
    assert r.state == "ambiguous" and r.needs_vlm


def test_multiscale_handles_dpi_variance():
    base = _sprite("idle", 60)
    big = cv2.resize(base, (66, 66))  # ~1.1x DPI
    goldens = {"idle": [base]}
    m = StateTemplateMatcher(goldens, {"idle": {"min_confidence": 0.4}})
    canvas = np.zeros((130, 130, 3), dtype=np.uint8)
    canvas[30:96, 30:96] = big
    r = m.classify(canvas, (30, 30, 66, 66))
    assert r.state == "idle"
