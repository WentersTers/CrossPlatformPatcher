"""Lock-screen / console-stolen detection (§5 console-owner row).

A lock screen (RDP stole the console) must NEVER count as worker divergence
("stable + busy" is expected — nobody is home) nor as a pet-state
classification. Detection: template match against registered lock-screen
goldens; on hit the watchdog reports console=locked-or-stolen and suppresses
divergence with an explicit reason instead of a boolean.
"""
from __future__ import annotations

import cv2
import numpy as np


def lock_screen_score(gray: np.ndarray, template_gray: np.ndarray) -> float:
    """Max normalized correlation of the lock template over the frame."""
    if (template_gray.shape[0] > gray.shape[0]
            or template_gray.shape[1] > gray.shape[1]):
        return 0.0
    m = cv2.matchTemplate(gray, template_gray, cv2.TM_CCOEFF_NORMED)
    return float(m.max())


def is_lock_screen(gray: np.ndarray, lock_templates: list[np.ndarray],
                   threshold: float = 0.85) -> bool:
    """True when any registered lock-screen golden matches at threshold."""
    for tmpl in lock_templates:
        t = cv2.cvtColor(tmpl, cv2.COLOR_BGR2GRAY) if tmpl.ndim == 3 else tmpl
        g = cv2.cvtColor(gray, cv2.COLOR_BGR2GRAY) if gray.ndim == 3 else gray
        if lock_screen_score(g, t) >= threshold:
            return True
    return False


def watchdog_observed(claimed_active: bool, fb_stable: bool, log_delta_bytes: int,
                      lock_screen: bool) -> dict:
    """Observed-status writer input: divergence suppressed on lock screen."""
    from harness.watchdog.checks import detect_divergence
    if lock_screen:
        return {"divergence": False, "console": "locked-or-stolen",
                "note": "RDP may have stolen the console; framebuffer shows "
                        "lock screen — not worker divergence, not a pet state"}
    return {"divergence": detect_divergence(claimed_active, fb_stable,
                                            log_delta_bytes),
            "console": "agent"}
