"""Gate 2: per-OS sidecar thresholds, active-window OCR scoping."""
import numpy as np
import pytest

from harness.gates.gate2_ssim_ocr import evaluate, ssim_gray


def _img(val=128, h=64, w=64):
    return np.full((h, w), val, dtype=np.uint8)


def test_ssim_identical_is_one():
    a = _img()
    assert ssim_gray(a, a) == 1.0


def test_ssim_detects_change():
    assert ssim_gray(_img(128), _img(0)) < 0.5


def test_gate2_pass_with_sidecar_threshold():
    golden = _img(128)
    actual = _img(130)  # tiny rendering difference (Win font smoothing style)
    r = evaluate(actual, golden, {"ssim_threshold": 0.90},
                 ocr_func=lambda roi: "Opening the browser",
                 active_window_roi=None, expected_text="Opening")
    assert r.passed, r.failures
    assert r.evidence["ssim"] >= 0.90


def test_gate2_per_os_threshold_enforced():
    golden = _img(128)
    actual = _img(150)
    strict = evaluate(actual, golden, {"ssim_threshold": 0.9999})
    loose = evaluate(actual, golden, {"ssim_threshold": 0.10})
    assert not strict.passed
    assert loose.passed


def test_gate2_ocr_forbidden_only_in_active_window():
    golden = _img(128)
    # forbidden text present but OCR is scoped to roi crop: fake ocr ignores background
    def ocr_scoped(roi):
        return "Opening the browser"  # background log viewer errors excluded by scoping
    r = evaluate(golden, golden, {}, ocr_func=ocr_scoped,
                 active_window_roi=(0, 0, 32, 32), expected_text="Opening")
    assert r.passed, r.failures

    def ocr_dirty(roi):
        return "DllNotFoundException: libvosk in active window"
    r2 = evaluate(golden, golden, {}, ocr_func=ocr_dirty,
                  active_window_roi=(0, 0, 32, 32))
    assert not r2.passed and any("forbidden" in f for f in r2.failures)

    def ocr_missing(roi):
        return "Something else"
    r3 = evaluate(golden, golden, {}, ocr_func=ocr_missing, expected_text="Opening")
    assert not r3.passed and any("expected_text" in f for f in r3.failures)


def test_localize_change_separates_aim_from_effect():
    from harness.gates.gate2_ssim_ocr import localize_change
    before = np.zeros((100, 100), dtype=np.uint8)
    after = before.copy()
    after[70:80, 70:80] = 200  # change far from the aim point
    loc = localize_change(before, after, at_xy=(10, 10), radius=50)
    assert loc["changed_px"] == 100
    assert loc["bbox"] == [70, 70, 79, 79]
    assert loc["mean_near"] == 0.0 and loc["mean_far"] > 0.0
    same = localize_change(before, before.copy(), at_xy=(10, 10))
    assert same["changed_px"] == 0 and same["bbox"] is None
    with pytest.raises(ValueError):
        localize_change(before, np.zeros((50, 50), dtype=np.uint8))


def test_no_change_threshold_sits_between_measured_bands():
    """Session-2/3 recorded bands (n=6 no-change, n=1 change). The constant
    must sit below the entire no-change band and far above gross change.
    Move only on band distributions, never single samples."""
    from harness.gates.gate2_ssim_ocr import NO_CHANGE_THRESHOLD
    no_change_band = [1.0, 1.0, 0.99986, 0.99954, 0.99805, 0.97752]
    change_band = [0.21908]
    assert NO_CHANGE_THRESHOLD == 0.95
    assert all(s >= NO_CHANGE_THRESHOLD for s in no_change_band)
    assert all(s < NO_CHANGE_THRESHOLD - 0.5 for s in change_band)
