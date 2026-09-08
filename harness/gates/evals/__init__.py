"""Shared prompt-drift eval harness (§6 prompt-drift control)."""
from __future__ import annotations

from dataclasses import dataclass


@dataclass
class EvalOutcome:
    accuracy: float
    total: int
    correct: int
    blocked: bool = False


def check_regression(baseline_acc: float, new_acc: float, threshold: float = 0.05) -> bool:
    """Regression >5% blocks the edit. Returns True when blocked."""
    return (baseline_acc - new_acc) > threshold


def run_eval(items: list[dict], predict_fn) -> EvalOutcome:
    """items: [{input, expected}]. predict_fn(item) -> predicted label/score."""
    correct = 0
    for it in items:
        if predict_fn(it) == it.get("expected"):
            correct += 1
    total = len(items)
    acc = correct / total if total else 0.0
    return EvalOutcome(acc, total, correct)
