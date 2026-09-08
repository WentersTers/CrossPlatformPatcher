"""Watchdog §5: adaptive cadence, hash stability, divergence cross-reference."""
from harness.watchdog.adaptive_scheduler import AdaptiveScheduler
from harness.watchdog.checks import (detect_divergence, downscale_grayscale_hash,
                                     framebuffer_hash, log_delta, qga_check)


def test_adaptive_intervals():
    s = AdaptiveScheduler()
    assert s.interval(True) == 5.0
    assert s.interval(False) == 30.0
    assert s.should_check(5.0, True) and not s.should_check(4.9, True)
    assert s.should_check(30.0, False) and not s.should_check(29.0, False)
    assert s.on_demand_post_action() is True


def test_qga_unknown_never_healthy():
    assert qga_check(True) == "ok"
    assert qga_check(False) == "unknown"


def test_fb_hash_stable_for_solid_color():
    w, h = 64, 36
    solid = bytes([10, 20, 30] * w * h)
    assert downscale_grayscale_hash(solid, w, h) == downscale_grayscale_hash(solid, w, h)
    # single-pixel change in a 64x36 image vanishes after 160x90 downscale? use big block
    changed = bytearray(solid)
    for i in range(0, len(changed), 3):
        changed[i] = 200
    assert downscale_grayscale_hash(bytes(changed), w, h) != downscale_grayscale_hash(solid, w, h)


def test_divergence_only_on_cross_reference():
    assert detect_divergence(True, True, 0) is True
    assert detect_divergence(True, True, 10) is False  # log growing -> working
    assert detect_divergence(True, False, 0) is False  # screen changing -> working
    assert detect_divergence(False, True, 0) is False  # idle claim -> not divergence
    assert log_delta(100, 150) == 50
    assert log_delta(150, 100) == 0
    assert len(framebuffer_hash(b"abc")) == 32


def test_rolling_set_blink_reads_stable_novelty_reads_not():
    from harness.watchdog.checks import HashWindow
    w = HashWindow(k=4)
    assert w.observe("a") is False  # cold: no history, never stable
    assert w.observe("a") is True
    # terminal cursor blink: two-state alternation stays inside the set
    assert w.observe("b") is False  # first sighting of b: novel (correct)
    assert w.observe("a") is True
    assert w.observe("b") is True  # now both known: blink reads stable
    assert w.observe("c") is False  # genuine change: novel hash
    assert len(w) == 4  # bounded window
    w2 = HashWindow(k=2)
    assert w2.observe("a") is False
    assert w2.observe("b") is False
    assert w2.observe("c") is False  # evicted a
    assert w2.observe("a") is False  # aged out of k=2: novel again
    assert w2.observe("a") is True  # now known
