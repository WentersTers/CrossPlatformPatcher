"""pet_absent: the legitimate pre-launch condition (UEFI/GRUB/login/desktop).

The classifier's first real screenshots will have no pet. Absent is a third
band — not ambiguous (no VLM burn on a boot screen), not a failure (no storm,
sequence waits). Pet-present gate starts verification only when present.
"""
import asyncio

import cv2
import numpy as np

from harness.gates.gate2_5_state.review_queue import GoldenReviewQueue
from harness.gates.gate2_5_state.sequence import (verify_state_sequence,
                                                  wait_for_pet_present)
from harness.gates.gate2_5_state.template_matcher import (PET_ABSENT,
                                                           StateTemplateMatcher)


def _pet_circle(size: int = 60) -> np.ndarray:
    img = np.zeros((size, size, 3), dtype=np.uint8)
    cv2.circle(img, (size // 2, size // 2), 15, (255, 255, 255), 2)
    return img


def _blank(h=120, w=160) -> np.ndarray:
    return np.zeros((h, w, 3), dtype=np.uint8)


def _grub(h=120, w=160) -> np.ndarray:
    img = np.zeros((h, w, 3), dtype=np.uint8)
    cv2.putText(img, "GNU GRUB  v2.06", (8, 20),
                cv2.FONT_HERSHEY_SIMPLEX, 0.4, (200, 200, 200), 1)
    cv2.putText(img, "*Ubuntu", (8, 45),
                cv2.FONT_HERSHEY_SIMPLEX, 0.4, (200, 200, 200), 1)
    cv2.putText(img, " UEFI Firmware", (8, 62),
                cv2.FONT_HERSHEY_SIMPLEX, 0.4, (200, 200, 200), 1)
    return img


def _login(h=120, w=160) -> np.ndarray:
    img = np.zeros((h, w, 3), dtype=np.uint8)
    for y in range(h):  # smooth vertical gradient: almost no edges
        img[y, :] = (90 + y // 4, 60 + y // 6, 40)
    cv2.rectangle(img, (w // 2 - 14, h // 2 - 18), (w // 2 + 14, h // 2 + 18),
                  (180, 180, 180), -1)
    return img


def _desktop(h=120, w=160) -> np.ndarray:
    img = np.full((h, w, 3), 45, dtype=np.uint8)
    cv2.rectangle(img, (0, h - 12), (w, h), (25, 25, 25), -1)  # taskbar
    cv2.rectangle(img, (4, h - 10), (12, h - 3), (70, 70, 70), -1)  # start btn
    return img


def _matcher():
    return StateTemplateMatcher({"idle": [_pet_circle()]},
                                {"idle": {"min_confidence": 0.80}})


def test_boot_frames_are_absent_not_ambiguous():
    m = _matcher()
    for name, frame in (("blank", _blank()), ("grub", _grub()),
                        ("login", _login()), ("desktop", _desktop())):
        r = m.classify(frame, None)
        assert r.state == PET_ABSENT, f"{name} -> {r.state} {r.score:.3f}"
        assert r.needs_vlm is False, f"{name} must not burn VLM tokens"


def test_absent_distinct_from_ambiguous_band():
    m = _matcher()
    # empty ROI via low locate confidence -> absent regardless of pixels
    r = m.classify(_blank(), None, roi_confidence=0.1)
    assert r.state == PET_ABSENT and not r.needs_vlm
    # confident ROI on empty pixels -> still absent (edge floor)
    r2 = m.classify(_blank(), (10, 10, 40, 40), roi_confidence=0.9)
    assert r2.state == PET_ABSENT


def test_absent_breaks_storm_and_never_fires_it():
    from harness.gates.gate2_5_state.sequence import consecutive_ambiguous_tail
    from harness.gates.gate2_5_state.sequence import ObservedFrame
    obs = [ObservedFrame("ambiguous", 0.0, 0.3, "template"),
           ObservedFrame("ambiguous", 0.5, 0.3, "template"),
           ObservedFrame(PET_ABSENT, 1.0, 0.0, "template"),
           ObservedFrame("ambiguous", 1.5, 0.3, "template")]
    assert consecutive_ambiguous_tail(obs) == 1  # absent reset the streak


def test_long_absence_waits_then_sequence_passes():
    expected = [{"state": "idle", "min_duration_s": 0.0}]
    script = [PET_ABSENT] * 8 + ["idle", "idle"]  # boot, then pet appears
    it, clock = iter(script), [0.0]

    async def sleep(d):
        clock[0] += d

    from harness.gates.gate2_5_state.template_matcher import StateMatch
    res = asyncio.run(verify_state_sequence(
        expected, timeout_s=60.0, capture_fn=lambda: next(it, "idle"),
        classify_fn=lambda s: StateMatch(s, 0.9 if s == "idle" else 0.0,
                                         "template"),
        sleep_fn=sleep, time_fn=lambda: clock[0]))
    assert res.passed, res.reason  # waited through absence, no storm


def test_pet_never_present_reason():
    from harness.gates.gate2_5_state.template_matcher import StateMatch
    it, clock = iter([PET_ABSENT] * 40), [0.0]

    async def sleep(d):
        clock[0] += d

    res = asyncio.run(verify_state_sequence(
        [{"state": "idle", "min_duration_s": 0.0}], timeout_s=30.0,
        capture_fn=lambda: next(it, PET_ABSENT),
        classify_fn=lambda s: StateMatch(s, 0.0, "template"),
        sleep_fn=sleep, time_fn=lambda: clock[0]))
    assert not res.passed and res.reason == "pet-never-present"


def test_present_gate_waits_then_opens():
    from harness.gates.gate2_5_state.template_matcher import StateMatch
    script = [PET_ABSENT, PET_ABSENT, "idle"]
    it, clock = iter(script), [0.0]

    async def sleep(d):
        clock[0] += d

    out = asyncio.run(wait_for_pet_present(
        capture_fn=lambda: next(it, "idle"),
        classify_fn=lambda s: StateMatch(s, 0.9, "template"),
        timeout_s=30.0, sleep_fn=sleep, time_fn=lambda: clock[0],
        poll_interval_s=1.0))
    assert out["present"] is True and out["state"] == "idle"
    assert out["frames"] == 3 and out["waited_s"] == 2.0

    it2, clock2 = iter([PET_ABSENT] * 100), [0.0]

    async def sleep2(d):
        clock2[0] += d

    out2 = asyncio.run(wait_for_pet_present(
        capture_fn=lambda: next(it2, PET_ABSENT),
        classify_fn=lambda s: StateMatch(s, 0.0, "template"),
        timeout_s=5.0, sleep_fn=sleep2, time_fn=lambda: clock2[0]))
    assert out2["present"] is False


def test_ambiguous_does_not_open_present_gate():
    """Session-4: out-of-bounds ROI on VGA boot reads ambiguous — that must
    wait, not open. Only concrete states open the gate."""
    from harness.gates.gate2_5_state.template_matcher import StateMatch
    script = ["ambiguous"] * 6 + ["idle"]
    it, clock = iter(script), [0.0]

    async def sleep(d):
        clock[0] += d

    out = asyncio.run(wait_for_pet_present(
        capture_fn=lambda: next(it, "idle"),
        classify_fn=lambda s: StateMatch(s, 0.4 if s == "ambiguous" else 0.9,
                                         "template"),
        timeout_s=30.0, sleep_fn=sleep, time_fn=lambda: clock[0],
        poll_interval_s=1.0))
    assert out["present"] is True and out["state"] == "idle"
    assert out["frames"] == 7  # six ambiguous waited through, idle opened


def test_absent_never_queues_review():
    q = GoldenReviewQueue()
    assert q.submit("b1", np.zeros((4, 4, 3), dtype=np.uint8),
                    PET_ABSENT, 0.0, "idle", 0.95) is False
    assert q.submit_disagreement("b2", np.zeros((4, 4, 3), dtype=np.uint8),
                                 PET_ABSENT, 0.0, "idle") is False
    assert q.pending() == []


def _dock_icon_roi() -> np.ndarray:
    """Worst-case false presence: pet-sized fixed ROI over a size-matched
    round dock icon with bar edge + clutter (Ubuntu dock look)."""
    img = np.full((60, 60, 3), 45, dtype=np.uint8)
    cv2.rectangle(img, (0, 0), (6, 60), (30, 30, 30), -1)
    cv2.circle(img, (32, 24), 15, (200, 200, 200), 2)
    cv2.circle(img, (32, 24), 5, (200, 200, 200), -1)
    cv2.rectangle(img, (24, 44), (40, 58), (180, 180, 180), 2)
    return img


def test_false_presence_is_ambiguous_burn():
    """The mirror blind spot (pinned, not fixed): a textured ROI with a
    sprite-sized round icon scores ABOVE the absent ceiling but BELOW the
    state threshold — ambiguous, not absent, not confident-wrong.

    Measured on synthetic: 0.25 <= score < 0.80 (observed ~0.30). Consequences:
    (1) every pre-launch poll burns VLM tokens instead of short-circuiting;
    (2) a static textured ROI climbs the ambiguous-storm counter — the storm
    was designed for unclear PET frames, but a dock icon feeds it identically.
    The 0.25 ceiling sits ~0.05 below a textured negative: a thin measured
    margin, and the quantity Phase A negatives must re-measure on real
    desktops (band separation, not just positives).
    Closers: Phase A negative-frame margins + telemetry intent lane (a confident
    'idle' with no supporting state_change is intent-effect mismatch)."""
    from harness.gates.gate2_5_state.template_matcher import edge_map, to_gray
    m = StateTemplateMatcher({"idle": [_pet_circle()]},
                             {"idle": {"min_confidence": 0.80}})
    roi = _dock_icon_roi()
    assert float((edge_map(to_gray(roi)) > 0).mean()) > 0.01  # past the floor
    r = m.classify(roi, None)
    assert r.state == "ambiguous" and r.needs_vlm is True
    assert 0.25 <= r.score < 0.80, f"band drift: {r.score:.3f}"
    # storm counter DOES climb here — pinned as the known exposure, not as OK
    from harness.gates.gate2_5_state.sequence import consecutive_ambiguous_tail
    from harness.gates.gate2_5_state.sequence import ObservedFrame
    obs = [ObservedFrame("ambiguous", t, 0.3, "template") for t in (0.0, 0.5, 1.0)]
    assert consecutive_ambiguous_tail(obs) == 3
