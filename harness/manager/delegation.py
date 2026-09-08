"""Schema-validated delegation (CrewAI-failure-mode prevention, §3)."""
from __future__ import annotations

from dataclasses import dataclass


@dataclass
class Task:
    id: str
    type: str
    params: dict
    success_criteria: str
    timeout_s: float = 300.0


REQUIRED = ("id", "type", "params", "success_criteria")


def validate_task(payload: dict) -> Task:
    """Strict schema; malformed = reject-retry, never interpreted."""
    if not isinstance(payload, dict):
        raise ValueError("task must be a JSON object")
    for k in REQUIRED:
        if k not in payload:
            raise ValueError(f"task missing required field: {k}")
    if not isinstance(payload["params"], dict):
        raise ValueError("task.params must be an object")
    timeout = float(payload.get("timeout_s", 300.0))
    if timeout <= 0:
        raise ValueError("timeout_s must be positive")
    return Task(payload["id"], payload["type"], payload["params"],
                payload["success_criteria"], timeout)


def completion_predicate(task_type: str) -> str:
    """Explicit completion predicates per task type — never 'felt done'."""
    return {
        "wake_word_seq": "assert_state_sequence passed for idle->listening->thinking->speaking->idle",
        "boot": "G1 exit_code==0 and app.running==true in state.json",
        "audio_smoke": "NAUDIO_ALSA_DEVICE=null smoke logged + backend in launcher-runtime.log",
        "state_assert": "assert_state ok with method recorded",
    }.get(task_type, f"{task_type}: gate-authoritative verdict recorded in verdict.json")
