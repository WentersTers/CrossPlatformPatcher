"""Tier A — template matching, deterministic, authoritative per D9.

Edge maps (Canny), not raw pixels: Mac/Linux/Windows render PNG sprites with
different color profiles and alpha compositing; edges preserve structure.
Multi-scale handles DPI variance. Per-state thresholds live in sidecars (§7).
"""
from __future__ import annotations

from dataclasses import dataclass

import cv2
import numpy as np

SCALES = (0.8, 0.9, 1.0, 1.1, 1.2)

# pet_absent band (pre-launch condition, NOT a failure): nothing pet-like in
# the ROI at all — boot screens, login, bare desktop. Distinct from ambiguous
# (something pet-like but unclear, needs VLM). Absent burns no VLM tokens.
PET_ABSENT = "pet_absent"
# Best-score-across-ALL-states below this ceiling => absent, not ambiguous.
ABSENT_CEILING = 0.25
# Edge-density floor: an ROI with almost no edges holds no sprite. This is the
# ROI-confidence half of absent detection (an empty fixed region during boot),
# and it also sidesteps the uniform-image matchTemplate hazard (0/0 scores).
EDGE_FLOOR_FRACTION = 0.01
# Callers with a real ROI-locate score (template_locate) pass it in; below
# this floor the frame is absent without consulting state thresholds.
ROI_CONFIDENCE_FLOOR = 0.30


@dataclass
class StateMatch:
    state: str
    score: float
    method: str = "template"
    needs_vlm: bool = False


def edge_map(gray: np.ndarray) -> np.ndarray:
    if gray.dtype != np.uint8:
        gray = gray.astype(np.uint8)
    return cv2.Canny(gray, 50, 150)


def to_gray(img: np.ndarray) -> np.ndarray:
    if img.ndim == 2:
        return img.astype(np.uint8)
    return cv2.cvtColor(img, cv2.COLOR_BGR2GRAY)


def _match_at_scales(pet_edges: np.ndarray, tmpl_edges: np.ndarray) -> float:
    best = 0.0
    th, tw = tmpl_edges.shape[:2]
    for s in SCALES:
        nw, nh = max(1, int(tw * s)), max(1, int(th * s))
        if nh > pet_edges.shape[0] or nw > pet_edges.shape[1]:
            continue
        resized = cv2.resize(tmpl_edges, (nw, nh), interpolation=cv2.INTER_LINEAR)
        if resized.shape[0] > pet_edges.shape[0] or resized.shape[1] > pet_edges.shape[1]:
            continue
        m = cv2.matchTemplate(pet_edges, resized, cv2.TM_CCOEFF_NORMED)
        best = max(best, float(m.max()))
    return best


def sidecar_threshold(state: str, sidecars: dict[str, dict]) -> float:
    return float(sidecars.get(state, {}).get("min_confidence", 0.80))


class StateTemplateMatcher:
    def __init__(self, golden_states: dict[str, list[np.ndarray]],
                 sidecars: dict[str, dict] | None = None,
                 absent_ceiling: float = ABSENT_CEILING,
                 edge_floor: float = EDGE_FLOOR_FRACTION,
                 roi_floor: float = ROI_CONFIDENCE_FLOOR):
        """golden_states: state -> list of template images (BGR or gray)."""
        self.golden_states = golden_states
        self.sidecars = sidecars or {}
        self.absent_ceiling = absent_ceiling
        self.edge_floor = edge_floor
        self.roi_floor = roi_floor
        # precompute edge maps once
        self._edges: dict[str, list[np.ndarray]] = {}
        for state, tmpls in golden_states.items():
            self._edges[state] = [edge_map(to_gray(t)) for t in tmpls]

    def classify(self, screenshot: np.ndarray,
                 pet_roi: tuple[int, int, int, int] | None,
                 roi_confidence: float | None = None) -> StateMatch:
        """Three bands: classified (>= per-state threshold) / ambiguous
        (pet-like but unclear, needs VLM) / pet_absent (nothing there,
        needs_vlm=False — never burn tokens on a boot screen)."""
        if pet_roi is not None:
            x, y, w, h = pet_roi
            pet = screenshot[y: y + h, x: x + w]
        else:
            pet = screenshot
        if pet.size == 0:
            return StateMatch("ambiguous", 0.0, "template", needs_vlm=True)
        if roi_confidence is not None and roi_confidence < self.roi_floor:
            return StateMatch(PET_ABSENT, float(roi_confidence), "template",
                              needs_vlm=False)
        pet_edges = edge_map(to_gray(pet))
        if pet_edges.size and float((pet_edges > 0).mean()) < self.edge_floor:
            return StateMatch(PET_ABSENT, 0.0, "template", needs_vlm=False)
        best_state = "ambiguous"
        best_score = 0.0
        for state, tmpl_edges_list in self._edges.items():
            for te in tmpl_edges_list:
                s = _match_at_scales(pet_edges, te)
                if s > best_score:
                    best_score = s
                    best_state = state
        if best_score < self.absent_ceiling:
            return StateMatch(PET_ABSENT, best_score, "template",
                              needs_vlm=False)
        thr = sidecar_threshold(best_state, self.sidecars) if best_state != "ambiguous" else 1.0
        if best_state != "ambiguous" and best_score >= thr:
            return StateMatch(best_state, best_score, "template", needs_vlm=False)
        return StateMatch("ambiguous", best_score, needs_vlm=True)
