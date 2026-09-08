"""Budgets & anti-loop enforcement (§3, §13)."""
from __future__ import annotations

from dataclasses import dataclass, field

PER_TEST_TOKEN_CAP = 200_000
PER_WORKER_RETRIES = 2
VLM_CALLS_PER_ASSERTION = 5
GATE_DISAGREE_WINDOW = 50
GATE_DISAGREE_RATE_TRIGGER = 0.10


@dataclass
class BudgetTracker:
    text_tokens: int = 0
    image_tokens: int = 0
    attempts: dict[tuple[str, str], int] = field(default_factory=dict)

    def record(self, text_tokens: int = 0, image_tokens: int = 0) -> None:
        self.text_tokens += text_tokens
        self.image_tokens += image_tokens

    @property
    def total(self) -> int:
        return self.text_tokens + self.image_tokens

    def over_test_cap(self) -> bool:
        return self.total > PER_TEST_TOKEN_CAP

    def record_attempt(self, worker: str, task: str) -> int:
        key = (worker, task)
        self.attempts[key] = self.attempts.get(key, 0) + 1
        return self.attempts[key]

    def anti_loop_tripped(self, worker: str, task: str) -> bool:
        """Same worker + same task >2 attempts = abort + rollback + escalate."""
        return self.attempts.get((worker, task), 0) > 2

    def retry_allowed(self, worker: str, task: str) -> bool:
        return self.attempts.get((worker, task), 0) <= PER_WORKER_RETRIES

    def split(self) -> dict[str, int]:
        """Image/text split measured and reported separately per stage (D6)."""
        return {"text_tokens": self.text_tokens, "image_tokens": self.image_tokens,
                "total": self.total}
