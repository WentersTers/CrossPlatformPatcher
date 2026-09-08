"""ROI priority: fixed config -> template-locate -> VLM bbox."""
import numpy as np

from harness.gates.gate2_5_state.pet_roi import locate_pet_roi


def test_fixed_config_wins():
    shot = np.zeros((100, 100, 3), dtype=np.uint8)
    roi = locate_pet_roi(shot, config={"pet_roi": [1, 2, 10, 10]},
                         templates=[np.zeros((5, 5, 3), dtype=np.uint8)],
                         vlm_bbox_fn=lambda s: (9, 9, 9, 9))
    assert roi == (1, 2, 10, 10)


def test_vlm_fallback_last_resort():
    shot = np.zeros((100, 100, 3), dtype=np.uint8)
    roi = locate_pet_roi(shot, config=None, templates=None,
                         vlm_bbox_fn=lambda s: (5, 6, 20, 20))
    assert roi == (5, 6, 20, 20)
    assert locate_pet_roi(shot) is None


def test_template_locate_finds_pet():
    import cv2
    shot = np.zeros((100, 100, 3), dtype=np.uint8)
    cv2.rectangle(shot, (30, 30), (50, 50), (255, 255, 255), -1)
    cv2.circle(shot, (40, 40), 5, (0, 0, 0), -1)  # structure: flat templates match anywhere
    tmpl = np.full((20, 20, 3), 255, dtype=np.uint8)
    cv2.circle(tmpl, (10, 10), 5, (0, 0, 0), -1)
    roi = locate_pet_roi(shot, templates=[tmpl])
    assert roi is not None
    x, y, w, h = roi
    assert abs(x - 30) <= 4 and abs(y - 30) <= 4
