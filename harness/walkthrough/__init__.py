"""Harness walkthrough engine (D-HARNESS-WALK / REV-035).

Design-first; no VM runs are triggered by importing this module. The engine
drives manual-walk paths and emits a state track + evidence; it renders no
verdicts. See plans/addendum-2026-10-08-harness-walkthrough.md.
"""
from __future__ import annotations

from harness.walkthrough.states import (  # noqa: F401
    WalkState,
    WALK_STATES,
    FAILURE_STATES,
    SX,
    is_abstain,
    is_failure,
)
from harness.walkthrough.evidence import EvidenceRecord, EvidenceWriter, frame_sha256, guest_subject_sha256  # noqa: F401
from harness.walkthrough.classifier import (  # noqa: F401
    WalkClassifier,
    TierResult,
)
from harness.walkthrough.capture import (  # noqa: F401
    CapturedFrame,
    capture_frame,
    decode_base64_frame,
    make_capture_from_bytes,
)
from harness.walkthrough.motion import (  # noqa: F401
    MotionEvent,
    default_motion,
    make_sweep_motion,
)
from harness.walkthrough.roi import (  # noqa: F401
    fixed_roi,
    template_locate,
    locate_pet_roi,
)
from harness.walkthrough.frames import (  # noqa: F401
    load_reference_frames,
    sha256_file,
    inventory,
)
from harness.walkthrough.driver import (  # noqa: F401
    Step,
    StepDriver,
    DriveResult,
)
from harness.walkthrough.runner import (  # noqa: F401
    RunnerConfig,
    RunOutcome,
    WalkRunner,
)
from harness.walkthrough.rig import (  # noqa: F401
    GuestBridge,
    GuestBridgeConfig,
    build_default_steps,
)

__all__ = [
    "WalkState",
    "WALK_STATES",
    "FAILURE_STATES",
    "SX",
    "is_abstain",
    "is_failure",
    "EvidenceRecord",
    "EvidenceWriter",
    "frame_sha256",
    "guest_subject_sha256",
    "WalkClassifier",
    "TierResult",
    "CapturedFrame",
    "capture_frame",
    "decode_base64_frame",
    "make_capture_from_bytes",
    "MotionEvent",
    "default_motion",
    "make_sweep_motion",
    "fixed_roi",
    "template_locate",
    "locate_pet_roi",
    "load_reference_frames",
    "sha256_file",
    "inventory",
    "Step",
    "StepDriver",
    "DriveResult",
    "RunnerConfig",
    "RunOutcome",
    "WalkRunner",
    "GuestBridge",
    "GuestBridgeConfig",
    "build_default_steps",
]
