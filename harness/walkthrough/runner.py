"""Walk runner for the harness walkthrough engine.

Orchestrates a full walk: drives steps via StepDriver, captures frames, classifies
them, emits one EvidenceRecord per captured frame to states.jsonl, and records the
guest-subject hash. All IO is injected (capture/motion/click) so the same code
runs against a live guest, a recorded session, or a test fixture.
"""
from __future__ import annotations

import hashlib
import time
from dataclasses import dataclass, field
from pathlib import Path
from typing import Any, Callable, Iterable

from harness.walkthrough.capture import CapturedFrame, capture_frame
from harness.walkthrough.classifier import WalkClassifier, TierResult
from harness.walkthrough.driver import DriveResult, Step, StepDriver
from harness.walkthrough.evidence import EvidenceRecord, EvidenceWriter, frame_sha256
from harness.walkthrough.motion import default_motion
from harness.walkthrough.states import SX


@dataclass
class RunnerConfig:
    steps: list[Step]
    evidence_dir: str | Path
    guest_subject_sha256: str = ""
    model_version: str | None = None
    abstain_on_low_confidence: bool = True
    motion_before_capture: bool = True
    poll_interval_s: float = 0.5


@dataclass
class RunOutcome:
    drive: DriveResult
    evidence_path: str
    frames_captured: int
    abstained: bool
    guest_subject_sha256: str


class WalkRunner:
    """Drives a walk and emits evidence. All IO injected for testability."""

    def __init__(
        self,
        config: RunnerConfig,
        capture_fn: Callable[[], bytes],
        classify_fn: Callable[[bytes], Any],
        motion_fn: Callable[[], None] | None = None,
        click_fn: Callable[[int, int], None] | None = None,
        key_fn: Callable[[str], None] | None = None,
        sleep_fn: Callable[[float], None] | None = None,
        time_fn: Callable[[], float] = time.monotonic,
        poll_interval_s: float | None = None,
    ):
        self.config = config
        if poll_interval_s is not None:
            self.config.poll_interval_s = poll_interval_s
        self.capture_fn = capture_fn
        self.classify_fn = classify_fn
        self.motion_fn = motion_fn
        self.click_fn = click_fn
        self.key_fn = key_fn
        self.sleep_fn = sleep_fn or time.sleep
        self.time_fn = time_fn

    def _motion(self):
        if self.config.motion_before_capture and self.motion_fn is not None:
            self.motion_fn()

    def _capture_and_record(self, writer: EvidenceWriter, subject: str) -> CapturedFrame:
        self._motion()
        cf = capture_frame(self.capture_fn, time_fn=self.time_fn)
        # classify the captured frame
        try:
            m = self.classify_fn(cf.payload)
        except Exception:
            m = None
        if m is None:
            state = "unclassified"
            conf = 0.0
            method = "none"
            abstain = False
        else:
            state = getattr(m, "state", "SX_unknown")
            conf = float(getattr(m, "confidence", getattr(m, "score", 0.0)))
            method = getattr(m, "method", "unknown")
            abstain = bool(getattr(m, "abstain", False)) or state == SX.value
        rec = EvidenceRecord(
            ts=cf.ts,
            state=state,
            confidence=conf,
            method=method,
            frame_sha256=cf.sha256,
            guest_subject_sha256=subject or self.config.guest_subject_sha256,
            abstain=abstain,
            model_version=self.config.model_version,
            extra={"fmt": cf.fmt},
        )
        writer.write(rec)
        return cf

    def run(self) -> RunOutcome:
        evidence_dir = Path(self.config.evidence_dir)
        evidence_dir.mkdir(parents=True, exist_ok=True)
        writer = EvidenceWriter(evidence_dir / "states.jsonl")

        # wrap capture to also record evidence per capture
        captured_count = {"n": 0}

        orig_capture = self.capture_fn

        def recording_capture():
            cf = self._capture_and_record(writer, self.config.guest_subject_sha256)
            captured_count["n"] += 1
            return cf.payload

        # StepDriver expects capture_fn returning bytes
        driver = StepDriver(
            steps=self.config.steps,
            capture_fn=recording_capture,
            classify_fn=self.classify_fn,
            motion_fn=None,  # motion handled inside recording_capture
            sleep_fn=self.sleep_fn,
            time_fn=self.time_fn,
            poll_interval_s=self.config.poll_interval_s,
        )
        drive = driver.run()

        # if abstained, propagate
        abstained = drive.abstained
        return RunOutcome(
            drive=drive,
            evidence_path=str(evidence_dir / "states.jsonl"),
            frames_captured=captured_count["n"],
            abstained=abstained,
            guest_subject_sha256=self.config.guest_subject_sha256,
        )
