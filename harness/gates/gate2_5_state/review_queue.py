"""D9 golden-review queue: library growth path end to end (§7 Phase C).

Tier A returns `ambiguous` + Tier B classifies confidently as a known state →
frame becomes a CANDIDATE golden, queued for human review, then merged.
Separate remediation path from D3.
"""
from __future__ import annotations

from dataclasses import dataclass, field

import numpy as np

from harness.gates.gate2_5_state.template_matcher import PET_ABSENT

MIN_TIER_B_CONFIDENCE = 0.70


@dataclass
class CandidateGolden:
    frame_id: str
    state: str
    confidence: float
    tier_a_score: float
    frame: np.ndarray = field(repr=False)
    source: str = "tier_b"  # or "telemetry-disagree" (deterministic, pre-VLM)


class GoldenReviewQueue:
    def __init__(self):
        self._pending: dict[str, CandidateGolden] = {}
        self.merged: list[str] = []
        self.rejected: list[str] = []

    def submit(self, frame_id: str, frame: np.ndarray, tier_a_state: str,
               tier_a_score: float, tier_b_state: str,
               tier_b_confidence: float) -> bool:
        """Queue only genuine discoveries: Tier A ambiguous + Tier B confident
        known-state. Returns True when queued."""
        if tier_a_state != "ambiguous":
            return False
        if tier_b_state in ("unknown", "uncertain", "ambiguous"):
            return False
        if tier_b_confidence < MIN_TIER_B_CONFIDENCE:
            return False
        self._pending[frame_id] = CandidateGolden(
            frame_id, tier_b_state, tier_b_confidence, tier_a_score, np.asarray(frame))
        return True

    def submit_disagreement(self, frame_id: str, frame: np.ndarray,
                            tier_a_state: str, tier_a_confidence: float,
                            claimed_state: str | None) -> bool:
        """Deterministic D9 feed: Tier A CONFIDENT but telemetry (intent)
        disagrees. Closes the subset-match blind spot — a novel variant that
        scores high for the wrong-but-similar state never goes ambiguous, so
        submit() can never see it. Live before any VLM exists."""
        if tier_a_state in ("ambiguous", "uncertain", "unknown"):
            return False  # ambiguous path owns this; use submit()
        if tier_a_state == PET_ABSENT:
            return False  # no pet in frame: nothing to learn, never queue
        if claimed_state in (None, "unknown", "uncertain", "ambiguous"):
            return False
        if tier_a_state == claimed_state:
            return False
        self._pending[frame_id] = CandidateGolden(
            frame_id, claimed_state, tier_a_confidence, tier_a_confidence,
            np.asarray(frame), source="telemetry-disagree")
        return True

    def pending(self) -> list[CandidateGolden]:
        return list(self._pending.values())

    def approve(self, frame_id: str, library: dict[str, list[np.ndarray]]) -> bool:
        """Human review: merge the frame into the golden library (§7 Phase C)."""
        cand = self._pending.pop(frame_id, None)
        if cand is None:
            return False
        library.setdefault(cand.state, []).append(cand.frame)
        self.merged.append(frame_id)
        return True

    def reject(self, frame_id: str) -> bool:
        if frame_id not in self._pending:
            return False
        del self._pending[frame_id]
        self.rejected.append(frame_id)
        return True
