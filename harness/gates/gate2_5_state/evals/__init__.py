"""20-query starter eval sets, per state + edge cases (§6.5)."""
from __future__ import annotations

from dataclasses import dataclass


@dataclass
class LabeledFrame:
    frame_id: str
    state: str
    edge_case: str = ""  # e.g. transitional/occlusion/error-state


def starter_set() -> list[LabeledFrame]:
    """Deterministic 20-query set: per-state frames + transitional/occlusion/error."""
    items: list[LabeledFrame] = []
    for i in range(3):
        items.append(LabeledFrame(f"idle-{i}", "idle"))
    for i in range(3):
        items.append(LabeledFrame(f"listening-{i}", "listening"))
    for i in range(3):
        items.append(LabeledFrame(f"thinking-{i}", "thinking"))
    for i in range(4):
        items.append(LabeledFrame(f"speaking-{i}", "speaking"))
    for i in range(2):
        items.append(LabeledFrame(f"acting-{i}", "acting"))
    for i in range(2):
        items.append(LabeledFrame(f"error-{i}", "error"))
    items.append(LabeledFrame("trans-idle-listening", "listening", "transitional"))
    items.append(LabeledFrame("occluded-speaking", "speaking", "partial-occlusion"))
    items.append(LabeledFrame("error-pose-blink", "error", "error-state"))
    assert len(items) == 20
    return items


def run_state_eval(frames: list[LabeledFrame], classify_fn) -> dict:
    """classify_fn(frame_id) -> predicted state. Returns accuracy report."""
    correct = sum(1 for f in frames if classify_fn(f.frame_id) == f.state)
    return {"total": len(frames), "correct": correct,
            "accuracy": correct / len(frames) if frames else 0.0}
