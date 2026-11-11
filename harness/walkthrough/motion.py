"""Motion-event emission before capture (ISS-044).

The app repaints on cursor motion. A motion event is emitted before every
capture so transitional frames are not mistaken for settled states. The motion
source is injectable (real xdotool sweep, or a no-op for tests).
"""
from __future__ import annotations

from dataclasses import dataclass
from typing import Callable


@dataclass
class MotionEvent:
    kind: str  # "sweep" | "nudge" | "click-prep"
    x: int
    y: int


def default_motion(motion_fn: Callable[[MotionEvent], None] | None = None):
    """Return a callable that emits one motion event then optionally calls motion_fn."""
    def emit(x: int = 10, y: int = 10, kind: str = "sweep"):
        evt = MotionEvent(kind=kind, x=x, y=y)
        if motion_fn is not None:
            motion_fn(evt)
        return evt

    return emit


def make_sweep_motion(sweep_fn: Callable[[], None]):
    """Wrap a real sweep (e.g. xdotool mousemove) as a motion source."""
    def fn(evt: MotionEvent | None = None):
        sweep_fn()
    return fn
