"""Tests for the walkthrough engine package."""
from __future__ import annotations

import hashlib
import time

from types import SimpleNamespace

from harness.walkthrough import (
    WalkState,
    WALK_STATES,
    FAILURE_STATES,
    SX,
    is_abstain,
    is_failure,
    EvidenceRecord,
    EvidenceWriter,
    frame_sha256,
    WalkClassifier,
    TierResult,
    Step,
    StepDriver,
    DriveResult,
)


class FakeMatcher:
    def __init__(self, state="S6_menu", score=0.9, needs_vlm=False):
        self.state = state
        self.score = score
        self.needs_vlm = needs_vlm

    def classify(self, frame, roi=None):
        return SimpleNamespace(state=self.state, score=self.score, needs_vlm=self.needs_vlm)


class FakeLearned:
    def __init__(self, state="S7_pet", conf=0.95):
        self.state = state
        self.conf = conf

    def predict(self, frame):
        return (self.state, self.conf)


def test_states_enum_complete():
    assert len(WALK_STATES) == 13
    assert SX.value == "SX_unknown"
    assert is_abstain(SX)
    assert is_abstain("SX_unknown")
    assert not is_abstain(WalkState.S6_MENU)
    assert is_failure(WalkState.S6_BLANK)
    assert not is_failure(WalkState.S6_MENU)


def test_evidence_roundtrip(tmp_path):
    w = EvidenceWriter(tmp_path / "states.jsonl")
    rec = EvidenceRecord(ts=1.0, state="S6_menu", confidence=0.9, method="template",
                         frame_sha256=frame_sha256(b"abc"), guest_subject_sha256="deadbeef")
    w.write(rec)
    rows = w.read_all()
    assert len(rows) == 1
    assert rows[0]["state"] == "S6_menu"
    assert rows[0]["frame_sha256"] == hashlib.sha256(b"abc").hexdigest()


def test_classifier_template_classifies():
    clf = WalkClassifier(FakeMatcher("S6_menu", 0.9))
    r = clf.classify(b"frame")
    assert r.state == "S6_menu"
    assert r.method == "template"
    assert not r.abstain


def test_classifier_abstains_on_low_score():
    clf = WalkClassifier(FakeMatcher("ambiguous", 0.1))
    r = clf.classify(b"frame")
    assert r.abstain
    assert r.state == "SX_unknown"


def test_classifier_learned_tier_used_when_ambiguous():
    clf = WalkClassifier(FakeMatcher("ambiguous", 0.1), learned=FakeLearned("S7_pet", 0.95))
    r = clf.classify(b"frame")
    assert r.state == "S7_pet"
    assert r.method == "learned"


def test_classifier_learned_low_conf_abstains():
    clf = WalkClassifier(FakeMatcher("ambiguous", 0.1), learned=FakeLearned("S7_pet", 0.2))
    r = clf.classify(b"frame")
    assert r.abstain


def test_driver_completes_happy_path():
    frames = [b"f1", b"f2", b"f3"]
    idx = {"i": 0}

    def capture():
        f = frames[min(idx["i"], len(frames) - 1)]
        idx["i"] += 1
        return f

    def classify(b):
        return TierResult("S6_menu", 0.9, "template")

    steps = [Step("s1", expected_state="S6_menu", settle_frames=1)]
    drv = StepDriver(steps, capture, classify, sleep_fn=lambda x: None, poll_interval_s=0.0)
    res = drv.run()
    assert res.completed
    assert res.steps_completed == 1


def test_driver_abstains():
    def capture():
        return b"f"

    def classify(b):
        return TierResult("SX_unknown", 0.1, "abstain", abstain=True)

    steps = [Step("s1", expected_state="S6_menu", settle_frames=1)]
    drv = StepDriver(steps, capture, classify, sleep_fn=lambda x: None, poll_interval_s=0.0)
    res = drv.run()
    assert res.abstained


def test_driver_timeout():
    def capture():
        return b"f"

    def classify(b):
        return TierResult("S3_credits", 0.9, "template")

    steps = [Step("s1", expected_state="S6_menu", timeout_s=0.01, settle_frames=1)]
    drv = StepDriver(steps, capture, classify, sleep_fn=lambda x: None, poll_interval_s=0.0, time_fn=lambda: time.monotonic())
    # force timeout by making time advance
    t0 = time.monotonic()
    drv.time_fn = lambda: time.monotonic()
    res = drv.run()
    assert not res.completed
    assert "timeout" in res.reason or res.steps_completed == 0
