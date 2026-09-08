"""Lock-screen golden test: recognized as console-stolen, never divergence/state."""
import cv2
import numpy as np

from harness.gates.gate2_5_state.template_matcher import StateTemplateMatcher
from harness.watchdog.lockscreen import is_lock_screen, watchdog_observed


def _lock_screen(h=120, w=160) -> np.ndarray:
    """Synthetic Windows-style lock screen: flat blue field + centered clock block."""
    img = np.zeros((h, w, 3), dtype=np.uint8)
    img[:, :] = (120, 60, 20)  # dark blue (BGR)
    cv2.rectangle(img, (w // 2 - 20, h // 2 - 12), (w // 2 + 20, h // 2 + 12),
                  (255, 255, 255), -1)
    return img


def _desktop_with_pet(h=120, w=160) -> np.ndarray:
    img = np.full((h, w, 3), 40, dtype=np.uint8)
    cv2.circle(img, (w // 2, h // 2), 12, (200, 200, 200), 2)
    return img


def test_lock_screen_recognized():
    lock = _lock_screen()
    assert is_lock_screen(lock, [lock]) is True
    assert is_lock_screen(_desktop_with_pet(), [lock]) is False


def test_lock_screen_suppresses_divergence():
    # worker claims active + stable fb + zero logs would normally diverge...
    out = watchdog_observed(True, True, 0, lock_screen=True)
    assert out["divergence"] is False
    assert out["console"] == "locked-or-stolen"
    # ...while a real stuck worker still fires
    out2 = watchdog_observed(True, True, 0, lock_screen=False)
    assert out2["divergence"] is True and out2["console"] == "agent"


def test_lock_screen_never_a_pet_state():
    """At production thresholds (0.80-0.85), a lock screen must never classify
    as a pet state — ambiguous or pet_absent both satisfy this, and absent is
    strictly better (no VLM tokens burned on a lock screen). The frame is
    owned by the watchdog path, never the state matcher."""
    goldens = {"idle": [_desktop_with_pet()]}
    m = StateTemplateMatcher(goldens, {"idle": {"min_confidence": 0.80}})
    r = m.classify(_lock_screen(), None)
    assert r.state in ("ambiguous", "pet_absent")
    assert r.state != "idle" and r.needs_vlm is (r.state == "ambiguous")
    # the watchdog path, not the matcher, owns this frame
    assert is_lock_screen(_lock_screen(), [_lock_screen()]) is True
