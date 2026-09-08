"""D9 disagreement feed: the subset-match blind-spot fixture.

Tier A confidently says 'idle' on a listening frame (circle ⊂ circle+tick —
shared structure matches at high score, never ambiguous), while telemetry
(intent) claims 'listening'. submit() can't see this; submit_disagreement()
is the deterministic pre-VLM feed that closes it.
"""
import cv2
import numpy as np

from harness.gates.gate2_5_state.review_queue import GoldenReviewQueue
from harness.gates.gate2_5_state.template_matcher import StateTemplateMatcher
from harness.telemetry.emitter import SyntheticEmitter
from harness.telemetry.stream import TelemetryStream


def _sprite(kind: str, size: int = 60) -> np.ndarray:
    img = np.zeros((size, size, 3), dtype=np.uint8)
    cv2.circle(img, (size // 2, size // 2), 15, (255, 255, 255), 2)
    if kind == "listening":
        cv2.line(img, (size // 2 + 15, size // 2 - 10),
                 (size // 2 + 22, size // 2 - 18), (255, 255, 255), 3)
    return img


def _canvas(sprite):
    c = np.zeros((120, 120, 3), dtype=np.uint8)
    c[30:90, 30:90] = sprite
    return c


def test_subset_match_is_confident_and_wrong():
    """Pin the blind spot: Tier A above threshold for the WRONG state."""
    matcher = StateTemplateMatcher({"idle": [_sprite("idle")]},
                                   {"idle": {"min_confidence": 0.80}})
    m = matcher.classify(_canvas(_sprite("listening")), (30, 30, 60, 60))
    assert m.state == "idle" and not m.needs_vlm  # confident, wrong


def test_disagreement_feeds_queue_and_grows_library():
    library: dict[str, list[np.ndarray]] = {"idle": [_sprite("idle")]}
    sidecars = {"idle": {"min_confidence": 0.80},
                "listening": {"min_confidence": 0.80}}
    matcher = StateTemplateMatcher(library, sidecars)

    # intent lane: app claims listening
    em = SyntheticEmitter()
    em.state_change("idle", "listening", trigger="wake_word", settle_ms=350)
    stream = TelemetryStream()
    for line in em.lines:
        stream.ingest_line(line)
    claimed, _ = stream.claimed_state()
    assert claimed == "listening"

    # effect lane: Tier A confidently wrong
    frame = _canvas(_sprite("listening"))
    m = matcher.classify(frame, (30, 30, 60, 60))
    assert (m.state, m.needs_vlm) == ("idle", False)

    queue = GoldenReviewQueue()
    assert queue.submit_disagreement("frame-sub-001", _sprite("listening"),
                                     m.state, m.score, claimed) is True
    cand = queue.pending()[0]
    assert cand.source == "telemetry-disagree" and cand.state == "listening"
    # agreement / unknown / ambiguous never queue here
    assert queue.submit_disagreement("f2", _sprite("idle"), "idle", 0.9,
                                     "idle") is False
    assert queue.submit_disagreement("f3", _sprite("idle"), "idle", 0.9,
                                     "unknown") is False
    assert queue.submit_disagreement("f4", _sprite("idle"), "ambiguous", 0.1,
                                     "listening") is False

    assert queue.approve("frame-sub-001", library) is True
    matcher2 = StateTemplateMatcher(library, sidecars)
    r = matcher2.classify(frame, (30, 30, 60, 60))
    assert r.state == "listening" and not r.needs_vlm
