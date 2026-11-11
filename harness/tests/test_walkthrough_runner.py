"""Tests for the walk runner."""
from __future__ import annotations

import json
from pathlib import Path

from harness.walkthrough import (
    RunnerConfig,
    RunOutcome,
    WalkRunner,
    Step,
    TierResult,
    WalkState,
)


def test_runner_emits_evidence(tmp_path):
    frames = [b"f1", b"f2"]

    def capture():
        f = frames.pop(0) if frames else b"last"
        return f

    def classify(b):
        return TierResult("S6_menu", 0.9, "template")

    steps = [Step("s1", expected_state="S6_menu", settle_frames=1)]
    cfg = RunnerConfig(steps=steps, evidence_dir=tmp_path / "ev",
                       guest_subject_sha256="abc123")
    runner = WalkRunner(cfg, capture, classify, sleep_fn=lambda x: None,
                        poll_interval_s=0.0)
    outcome = runner.run()
    assert isinstance(outcome, RunOutcome)
    assert outcome.drive.completed
    assert outcome.frames_captured >= 1
    assert outcome.guest_subject_sha256 == "abc123"
    # evidence file written
    p = Path(outcome.evidence_path)
    assert p.exists()
    lines = [json.loads(l) for l in p.read_text().splitlines()]
    assert len(lines) >= 1
    assert lines[0]["guest_subject_sha256"] == "abc123"
    assert lines[0]["state"] == "S6_menu"


def test_runner_abstains_propagates(tmp_path):
    def capture():
        return b"f"

    def classify(b):
        return TierResult("SX_unknown", 0.1, "abstain", abstain=True)

    steps = [Step("s1", expected_state="S6_menu", settle_frames=1)]
    cfg = RunnerConfig(steps=steps, evidence_dir=tmp_path / "ev2")
    runner = WalkRunner(cfg, capture, classify, sleep_fn=lambda x: None,
                        poll_interval_s=0.0)
    outcome = runner.run()
    assert outcome.abstained
    assert not outcome.drive.completed


def test_runner_motion_fn_called(tmp_path):
    motion_calls = {"n": 0}

    def motion():
        motion_calls["n"] += 1

    def capture():
        return b"f"

    def classify(b):
        return TierResult("S6_menu", 0.9, "template")

    steps = [Step("s1", expected_state="S6_menu", settle_frames=1)]
    cfg = RunnerConfig(steps=steps, evidence_dir=tmp_path / "ev3",
                       motion_before_capture=True)
    runner = WalkRunner(cfg, capture, classify, motion_fn=motion,
                        sleep_fn=lambda x: None, poll_interval_s=0.0)
    runner.run()
    assert motion_calls["n"] >= 1
