"""Escalation + pre-registered mechanical triggers (D3, D9, §3, §13)."""
from __future__ import annotations

from dataclasses import dataclass


@dataclass
class EscalationDecision:
    escalate: bool
    reason: str = ""


def should_force_option_b(gate_disagree_rate: float, n_tests: int,
                          consecutive_false_pass: int) -> EscalationDecision:
    """D3: gate_disagree_rate > 10% over any 50 completed tests OR >=2 consecutive
    false-pass (worker says pass; G1/G2/G2.5-TierA says fail) -> force Option B."""
    if consecutive_false_pass >= 2:
        return EscalationDecision(True, "D3: >=2 consecutive false-pass")
    if n_tests >= 50 and gate_disagree_rate > 0.10:
        return EscalationDecision(True, f"D3: gate_disagree_rate {gate_disagree_rate:.3f} > 10% over {n_tests}")
    return EscalationDecision(False)


def should_review_golden_library(template_vlm_disagree_rate: float,
                                 ambiguous_rate: float) -> EscalationDecision:
    """D9 (separate remediation from D3): >15% of Tier B fallbacks disagree, or
    Tier A ambiguous-rate >30% on a platform -> golden-library review task."""
    if template_vlm_disagree_rate > 0.15:
        return EscalationDecision(True, f"D9: template-vs-VLM {template_vlm_disagree_rate:.3f} > 15%")
    if ambiguous_rate > 0.30:
        return EscalationDecision(True, f"D9: ambiguous-rate {ambiguous_rate:.3f} > 30%")
    return EscalationDecision(False)


AUTO_ESCALATE = ("budget exhausted", "repeated anti-loop trips",
                 "Windows key entry", "snapshot/network actions")


def auto_escalation_reason(event: str) -> EscalationDecision:
    for key in AUTO_ESCALATE:
        if key.lower() in event.lower():
            return EscalationDecision(True, f"auto: {key}")
    return EscalationDecision(False)
