"""assert_state / assert_state_sequence worker-facing API (§8).

Blocking; samples 2Hz (animated) / 0.5Hz (static); Tier A -> Tier B fallback;
VLM cap 5 calls; returns {ok, state, confidence, method, frames_sampled} —
worker never needs to know which tier answered.
"""
from __future__ import annotations

import time
from dataclasses import dataclass
from typing import Callable


@dataclass
class AssertStateResult:
    ok: bool
    state: str
    confidence: float
    method: str
    frames_sampled: int


ANIMATED = {"listening", "speaking"}


def assert_state(expected_state: str, timeout_s: float, min_duration_s: float,
                 capture_fn: Callable[[], object],
                 template_matcher, vlm=None,
                 roi_fn: Callable[[object], tuple | None] | None = None,
                 time_fn=time.monotonic, sleep_fn=None,
                 is_animated: bool | None = None) -> AssertStateResult:
    animated = is_animated if is_animated is not None else expected_state in ANIMATED
    interval = 0.5 if animated else 2.0
    if vlm is not None and hasattr(vlm, "reset_budget"):
        vlm.reset_budget()
    start = time_fn()
    first_t: float | None = None
    frames = 0
    last = None
    while time_fn() - start < timeout_s:
        shot = capture_fn()
        frames += 1
        roi = roi_fn(shot) if roi_fn else None
        m = template_matcher.classify(shot, roi)
        if getattr(m, "needs_vlm", False) and vlm is not None:
            v = vlm.classify(shot, roi)
            if v is not None:
                m = v
            else:
                last = AssertStateResult(False, "uncertain", 0.0, "uncertain", frames)
                break
        state = getattr(m, "state", "ambiguous")
        conf = float(getattr(m, "confidence", getattr(m, "score", 0.0)))
        method = getattr(m, "method", "template")
        last = AssertStateResult(state == expected_state, state, conf, method, frames)
        if state == expected_state:
            if first_t is None:
                first_t = time_fn()
            if time_fn() - first_t >= min_duration_s:
                last.ok = True
                break
        else:
            first_t = None
        if sleep_fn is not None:
            sleep_fn(interval)
        else:
            # tests inject fake time/sleep; in production callers pass real sleep.
            # Avoid real sleeping in unit tests: break if time_fn is fake-advanced only
            # by caller. If no sleep_fn and real clock, do a short real sleep.
            try:
                time.sleep(0)
            except Exception:
                pass
            # Prevent infinite tight loop on real clock without sleep_fn:
            # advance is time-based, so loop naturally exits on timeout.
    assert last is not None
    return last
