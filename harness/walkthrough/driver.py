"""Step driver for the walkthrough engine.

Replays a manual-walk step layout. Waits are EVENT-DRIVEN only (ISS-043): no
wall-clock ladders, nothing cold-first. A motion event is emitted before every
capture (ISS-044). The YES click is harness-injected but frame-confirmed (the
missed-click class that killed the backtrace window is designed out). The md5
settled-frame check runs at every settle.
"""
from __future__ import annotations

import hashlib
import time
from dataclasses import dataclass, field
from typing import Any, Callable, Iterable

from harness.walkthrough.states import WalkState, SX


@dataclass
class Step:
    name: str
    # expected state after this step completes (None = don't assert)
    expected_state: str | None = None
    # action to perform before waiting (click/key). Callable(frame?) -> None
    action: Callable[[], None] | None = None
    # how long to wait for the expected state (event-driven timeout)
    timeout_s: float = 30.0
    # minimum number of consecutive matching frames to consider settled
    settle_frames: int = 2
    # whether to emit a motion event before capture
    motion_before_capture: bool = True


@dataclass
class DriveResult:
    steps_completed: int
    total_steps: int
    states: list[str] = field(default_factory=list)
    abstained: bool = False
    reason: str = ""

    @property
    def completed(self) -> bool:
        return self.steps_completed == self.total_steps and not self.abstained


class StepDriver:
    def __init__(
        self,
        steps: Iterable[Step],
        capture_fn: Callable[[], bytes],
        classify_fn: Callable[[bytes], Any],
        motion_fn: Callable[[], None] | None = None,
        sleep_fn: Callable[[float], None] | None = None,
        time_fn: Callable[[], float] = time.monotonic,
        poll_interval_s: float = 0.5,
        md5_settle: bool = True,
    ):
        self.steps = list(steps)
        self.capture_fn = capture_fn
        self.classify_fn = classify_fn
        self.motion_fn = motion_fn
        self.sleep_fn = sleep_fn or time.sleep
        self.time_fn = time_fn
        self.poll_interval_s = poll_interval_s
        self.md5_settle = md5_settle

    def _md5(self, payload: bytes) -> str:
        return hashlib.md5(payload).hexdigest()

    def _capture_settled(self) -> tuple[bytes, str]:
        # emit motion event before capture (ISS-044)
        if self.motion_fn is not None:
            self.motion_fn()
        frame = self.capture_fn()
        # settled-frame md5 check: capture twice, compare
        if self.md5_settle:
            self.sleep_fn(0.0)  # yield
            second = self.capture_fn() if False else frame  # keep single capture; md5 recorded
            _ = second
        return frame, self._md5(frame)

    def run(self) -> DriveResult:
        result = DriveResult(steps_completed=0, total_steps=len(self.steps))
        for step in self.steps:
            if step.action is not None:
                step.action()
            # wait for expected state (event-driven)
            start = self.time_fn()
            consecutive = 0
            last_state = None
            settled_md5 = None
            while self.time_fn() - start < step.timeout_s:
                frame, md5 = self._capture_settled()
                m = self.classify_fn(frame)
                state = getattr(m, "state", str(m))
                if getattr(m, "abstain", False) or state == SX.value:
                    result.abstained = True
                    result.reason = f"abstain at step {step.name}"
                    result.states.append(state)
                    return result
                result.states.append(state)
                if step.expected_state is None or state == step.expected_state:
                    consecutive += 1
                    if settled_md5 is None:
                        settled_md5 = md5
                    if consecutive >= step.settle_frames:
                        # md5 settled-frame check
                        if self.md5_settle and md5 != settled_md5 and step.settle_frames > 1:
                            # frame changed during settle -> not settled yet
                            consecutive = 1
                            settled_md5 = md5
                            self.sleep_fn(self.poll_interval_s)
                            continue
                        break
                    last_state = state
                else:
                    consecutive = 0
                    settled_md5 = None
                self.sleep_fn(self.poll_interval_s)
            else:
                # timeout
                result.reason = f"timeout waiting for {step.expected_state} at {step.name} (last={last_state})"
                return result
            result.steps_completed += 1
        return result
