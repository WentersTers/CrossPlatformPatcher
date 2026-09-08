"""Adaptive watchdog scheduler: 5s active / 30s idle / on-demand post-action (§5)."""
from __future__ import annotations

from dataclasses import dataclass


ACTIVE_INTERVAL_S = 5.0
IDLE_INTERVAL_S = 30.0


@dataclass
class AdaptiveScheduler:
    def interval(self, worker_claims_running: bool) -> float:
        return ACTIVE_INTERVAL_S if worker_claims_running else IDLE_INTERVAL_S

    def should_check(self, elapsed_s: float, worker_claims_running: bool) -> bool:
        return elapsed_s >= self.interval(worker_claims_running)

    @staticmethod
    def on_demand_post_action() -> bool:
        """Always check on-demand after every click/type."""
        return True
