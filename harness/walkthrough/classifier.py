"""Walk-frame classifier: Tier A (template, deterministic) + Tier B (learned, optional).

Tier A is authoritative for the clear cases. Tier B is an injectable learned
classifier (a small model or nearest-neighbor over embeddings) used only when
Tier A is ambiguous/absent-band. Abstain (SX) is mandatory when neither tier
is confident. The learned tier is optional: pass None and the classifier
degrades to Tier A + abstain.

Training-data note (see plan): the learned tier trains on GUEST SCREENSHOTS
only, never on phone photos (different image domain). Until enough screenshots
exist for a state, that state stays on Tier A / abstain.
"""
from __future__ import annotations

from dataclasses import dataclass
from typing import Any, Callable, Protocol

# NOTE: we deliberately do NOT import template_matcher here. That module pulls
# in opencv (cv2), which is not available in every test environment. The
# classifier treats the template matcher as an opaque dependency and reads
# attributes off its result (state/score/needs_vlm) duck-typed.
from harness.walkthrough.states import WalkState, SX


class LearnedClassifier(Protocol):
    """Injectable learned tier. Returns (state, confidence) or None."""

    def predict(self, frame: Any) -> tuple[str, float] | None: ...


ABSTAIN_CEILING = 0.5  # below this, SX (never guess)


@dataclass
class TierResult:
    state: str
    confidence: float
    method: str  # "template" | "learned" | "abstain"
    needs_vlm: bool = False
    abstain: bool = False


class WalkClassifier:
    def __init__(
        self,
        template_matcher: Any,
        learned: LearnedClassifier | None = None,
        roi_fn: Callable[[Any], tuple[int, int, int, int] | None] | None = None,
        abstain_ceiling: float = ABSTAIN_CEILING,
    ):
        self.template_matcher = template_matcher
        self.learned = learned
        self.roi_fn = roi_fn
        self.abstain_ceiling = abstain_ceiling

    def classify(self, frame: Any) -> TierResult:
        roi = self.roi_fn(frame) if self.roi_fn else None
        match: StateMatch = self.template_matcher.classify(frame, roi)
        # pet_absent / ambiguous handling: ambiguous with low score -> try learned
        if match.state not in ("ambiguous",) and not match.needs_vlm:
            # classified by template (including pet_absent as a concrete state)
            if match.score >= self.abstain_ceiling or match.state == "pet_absent":
                return TierResult(match.state, float(match.score), "template",
                                 needs_vlm=match.needs_vlm, abstain=False)
        # ambiguous or low confidence: try learned tier
        if self.learned is not None:
            pred = self.learned.predict(frame)
            if pred is not None:
                state, conf = pred
                if conf >= self.abstain_ceiling:
                    return TierResult(state, float(conf), "learned", abstain=False)
        # abstain
        score = float(getattr(match, "score", 0.0))
        return TierResult(SX.value, score, "abstain", abstain=True,
                          needs_vlm=getattr(match, "needs_vlm", False))
