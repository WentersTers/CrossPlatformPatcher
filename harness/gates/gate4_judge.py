"""Gate 4 — adversarial second-pass judge, only if G3 uncertain or critical (§6).

Framing: 'prove this test FAILED.' Reduces confirmation bias; does NOT
decorrelate same-model errors — triage-only; cannot upgrade deterministic
failure (D4).
"""
from __future__ import annotations

from dataclasses import dataclass
from typing import Callable


@dataclass
class JudgeResult:
    failed_proven: bool
    score: float  # 0.0-1.0 confidence the test FAILED
    evidence: str = ""
    provisional: bool = True


ADVERSARIAL_PROMPT = (
    "You are an adversarial judge. Your job is to prove this test FAILED. "
    "Find any flaw, mismatch, or missing evidence. Respond with a 0.0-1.0 score "
    "(1.0 = certainly failed) plus concrete evidence. Do not confirm success."
)


def evaluate(judge_fn: Callable[[str], dict]) -> JudgeResult:
    raw = judge_fn(ADVERSARIAL_PROMPT)
    score = float(max(0.0, min(1.0, raw.get("score", 0.0))))
    return JudgeResult(failed_proven=score >= 0.5, score=score,
                       evidence=str(raw.get("evidence", ""))[:500], provisional=True)
