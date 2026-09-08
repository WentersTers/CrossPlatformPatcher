"""Mechanical trigger tracker with exact window semantics (D3, D9).

D3 trap fixed: "gate_disagree_rate > 10% over any 50 completed tests" is a
ROLLING window over the last 50 completed tests (not cumulative since boot),
and the false-pass counter is CONSECUTIVE (resets on any non-false-pass).
Previously the pure function took precomputed aggregates, so any caller could
silently disarm the trigger with an off-by-one. This tracker owns the stream.
"""
from __future__ import annotations

from collections import deque
from dataclasses import dataclass, field

from harness.manager.escalation import (EscalationDecision,
                                        should_force_option_b,
                                        should_review_golden_library)

WINDOW = 50


@dataclass
class TriggerTracker:
    """Ingest one outcome per completed test; query mechanical triggers."""
    disagreements: deque[bool] = field(default_factory=lambda: deque(maxlen=WINDOW))
    consecutive_false_pass: int = 0
    total_completed: int = 0
    # D9 accumulators
    tier_b_fallbacks: int = 0
    tier_b_disagreements: int = 0
    tier_a_attempts: int = 0
    tier_a_ambiguous: int = 0

    def record_test(self, gate_disagreed: bool, worker_said_pass: bool,
                    gate_said_pass: bool) -> None:
        """One call per completed test, in completion order."""
        false_pass = bool(worker_said_pass and not gate_said_pass)
        # consecutive counter: reset on ANY non-false-pass (spec: consecutive)
        self.consecutive_false_pass = self.consecutive_false_pass + 1 if false_pass else 0
        self.disagreements.append(bool(gate_disagreed))
        self.total_completed += 1

    def record_state_frame(self, tier_a_result: str,
                           tier_b_result: str | None = None) -> None:
        """tier_a_result: classified state or 'ambiguous'. tier_b_result: Tier B
        state when fallback ran, else None."""
        self.tier_a_attempts += 1
        if tier_a_result == "ambiguous":
            self.tier_a_ambiguous += 1
        if tier_b_result is not None:
            self.tier_b_fallbacks += 1
            if tier_b_result != tier_a_result and tier_a_result != "ambiguous":
                self.tier_b_disagreements += 1
            elif tier_a_result == "ambiguous" and tier_b_result in ("unknown", "uncertain"):
                pass  # agreement on uncertainty, not disagreement

    @property
    def rolling_disagree_rate(self) -> float:
        if not self.disagreements:
            return 0.0
        return sum(self.disagreements) / len(self.disagreements)

    def d3(self) -> EscalationDecision:
        # "any 50 completed tests" = rolling window is full (50) — before that,
        # rate is not yet defined over 50; consecutive false-pass still fires.
        n = len(self.disagreements)
        rate = self.rolling_disagree_rate if n == WINDOW else 0.0
        return should_force_option_b(rate, n, self.consecutive_false_pass)

    def d9(self) -> EscalationDecision:
        tb_rate = (self.tier_b_disagreements / self.tier_b_fallbacks
                   if self.tier_b_fallbacks else 0.0)
        amb_rate = (self.tier_a_ambiguous / self.tier_a_attempts
                    if self.tier_a_attempts else 0.0)
        return should_review_golden_library(tb_rate, amb_rate)
