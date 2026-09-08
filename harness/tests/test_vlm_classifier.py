"""Tier B: enum-only, unknown<0.5, 500ms cache, hard cap 5 (cost guards)."""
import numpy as np

from harness.gates.gate2_5_state.vlm_classifier import (StubVlmClassifier,
                                                        validate_result)


def test_enum_only_and_unknown_threshold():
    assert validate_result("idle", 0.9) == "idle"
    assert validate_result("idle", 0.4) == "unknown"
    assert validate_result("hallucinated", 0.99) == "unknown"


def test_hard_cap_5_returns_uncertain():
    img = np.zeros((8, 8, 3), dtype=np.uint8)
    v = StubVlmClassifier([("idle", 0.9)], max_calls_per_assertion=5, cache_ms=0)
    results = [v.classify(img, None) for _ in range(6)]
    assert all(r is not None for r in results[:5])
    assert results[5] is None  # fail gracefully, no token burn
    assert v.calls == 5


def test_cache_500ms():
    img = np.zeros((8, 8, 3), dtype=np.uint8)
    t = [0.0]
    v = StubVlmClassifier([("idle", 0.9), ("speaking", 0.9)],
                          cache_ms=500, time_fn=lambda: t[0])
    r1 = v.classify(img, None)
    t[0] = 0.1  # within 500ms, SAME frame -> cached
    r2 = v.classify(img, None)
    assert r1.state == r2.state == "idle"
    assert v.calls == 1
    t[0] = 1.0
    r3 = v.classify(img, None)
    assert r3.state == "speaking" and v.calls == 2


def test_cache_keyed_on_content_not_time():
    """Different frame within 500ms must NOT return the stale state."""
    img_a = np.zeros((8, 8, 3), dtype=np.uint8)
    img_b = np.full((8, 8, 3), 255, dtype=np.uint8)
    t = [0.0]
    v = StubVlmClassifier([("idle", 0.9), ("speaking", 0.9)],
                          cache_ms=500, time_fn=lambda: t[0])
    r1 = v.classify(img_a, None)
    t[0] = 0.1
    r2 = v.classify(img_b, None)  # changed frame -> fresh call
    assert r1.state == "idle" and r2.state == "speaking"
    assert v.calls == 2
