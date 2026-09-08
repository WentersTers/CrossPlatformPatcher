"""Gate 3 — small VLM triage, only if G1/G2/G2.5 ambiguous (§6).

'Is the expected UI state visible? Y/N + evidence.' Triage/provisional only;
can never upgrade a deterministic failure (D4) — enforced by returning
provisional=True always.
"""
from __future__ import annotations

from dataclasses import dataclass, field
from typing import Callable


@dataclass
class TriageResult:
    visible: bool
    score: float  # 0.0-1.0 numeric rubric, no free-text verdicts
    evidence: str = ""
    provisional: bool = True


def evaluate(judge_fn: Callable[[], dict], context: dict | None = None) -> TriageResult:
    """judge_fn returns {score 0..1, evidence}. Single-call judge."""
    raw = judge_fn()
    score = float(max(0.0, min(1.0, raw.get("score", 0.0))))
    return TriageResult(visible=score >= 0.5, score=score,
                        evidence=str(raw.get("evidence", ""))[:500], provisional=True)
