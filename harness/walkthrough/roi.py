"""ROI location helpers for the walkthrough engine.

Reuses the gate2_5_state.pet_roi locate_pet_roi logic but keeps the opencv
import lazy so this module stays importable in cv2-less environments (tests).
"""
from __future__ import annotations

from typing import Any, Callable, Optional


def _pet_roi_module():
    from harness.gates.gate2_5_state import pet_roi as _m
    return _m


def fixed_roi(config: dict | None):
    return _pet_roi_module().fixed_roi(config)


def template_locate(screenshot: Any, templates: list[Any]):
    return _pet_roi_module().template_locate(screenshot, templates)


def locate_pet_roi(screenshot: Any, config: dict | None = None,
                   templates: list[Any] | None = None,
                   vlm_bbox_fn: Callable[[Any], Optional[tuple[int, int, int, int]]] | None = None,
                   last_roi: tuple[int, int, int, int] | None = None):
    return _pet_roi_module().locate_pet_roi(screenshot, config=config, templates=templates,
                                            vlm_bbox_fn=vlm_bbox_fn, last_roi=last_roi)
