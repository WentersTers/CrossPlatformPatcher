"""D9 flow end to end: ambiguous storm -> candidate -> review -> library grows."""
import cv2
import numpy as np

from harness.gates.gate2_5_state.review_queue import GoldenReviewQueue
from harness.gates.gate2_5_state.template_matcher import StateTemplateMatcher
from harness.gates.gate2_5_state.vlm_classifier import StubVlmClassifier


def _sprite(kind: str, size: int = 60, tint: int = 0) -> np.ndarray:
    img = np.full((size, size, 3), tint, dtype=np.uint8)
    if kind == "idle":
        cv2.circle(img, (size // 2, size // 2), 15, (255, 255, 255), 2)
    elif kind == "listening":  # circle + ear tick
        cv2.circle(img, (size // 2, size // 2), 15, (255, 255, 255), 2)
        cv2.line(img, (size // 2 + 15, size // 2 - 10),
                 (size // 2 + 22, size // 2 - 18), (255, 255, 255), 3)
    else:  # speaking: rectangle (structurally distinct from circle)
        cv2.rectangle(img, (15, 15), (size - 15, size - 15), (255, 255, 255), 2)
    return img


def _canvas(sprite):
    c = np.zeros((120, 120, 3), dtype=np.uint8)
    c[30:90, 30:90] = sprite
    return c


def test_storm_to_candidate_to_merged_library():
    library: dict[str, list[np.ndarray]] = {"idle": [_sprite("idle")]}
    sidecars = {"idle": {"min_confidence": 0.85},
                "speaking": {"min_confidence": 0.80}}
    matcher = StateTemplateMatcher(library, sidecars)
    vlm = StubVlmClassifier([("speaking", 0.92)], max_calls_per_assertion=5,
                            cache_ms=0)
    queue = GoldenReviewQueue()

    # novel speaking frame on dark-tinted platform: Tier A ambiguous...
    # (rectangle shares no structure with the circle golden at 0.85)
    frame = _canvas(_sprite("speaking", tint=60))
    m = matcher.classify(frame, (30, 30, 60, 60))
    assert m.state == "ambiguous" and m.needs_vlm is True
    # ...Tier B confident -> candidate queued (NOT auto-merged)
    v = vlm.classify(frame, (30, 30, 60, 60))
    assert queue.submit("frame-001", _sprite("speaking", tint=60),
                        m.state, m.score, v.state, v.confidence) is True
    assert len(queue.pending()) == 1
    assert "speaking" not in library  # library unchanged before review

    # weak Tier B does NOT queue (below 0.70)
    assert queue.submit("frame-002", _sprite("speaking"), "ambiguous", 0.1,
                        "speaking", 0.5) is False
    # non-ambiguous Tier A never queues
    assert queue.submit("frame-003", _sprite("idle"), "idle", 0.9,
                        "idle", 0.9) is False

    # (stub) human review approves -> library grows -> Tier A now authoritative
    assert queue.approve("frame-001", library) is True
    matcher2 = StateTemplateMatcher(library, sidecars)
    r = matcher2.classify(_canvas(_sprite("speaking", tint=60)), (30, 30, 60, 60))
    assert r.state == "speaking" and not r.needs_vlm
    assert queue.merged == ["frame-001"]


def test_reject_path():
    q = GoldenReviewQueue()
    q.submit("f1", np.zeros((4, 4, 3), dtype=np.uint8),
             "ambiguous", 0.1, "acting", 0.8)
    assert q.reject("f1") is True and q.pending() == []
    assert q.approve("f1", {}) is False
