"""Watchdog telemetry checks (§5): freshness, seq reliability, process-death triple.

New distinction the framebuffer watchdog can't make: heartbeat-dead +
QGA-alive + fb-static = in-guest PROCESS death (app crash), not VM death
(QGA dead or lifecycle event present) and not a stuck worker.
"""
from __future__ import annotations

HEARTBEAT_STALE_S = 15.0


def telemetry_freshness(heartbeat_age_s: float | None,
                        threshold_s: float = HEARTBEAT_STALE_S) -> dict:
    """None age = no heartbeat ever seen (also stale)."""
    if heartbeat_age_s is None:
        return {"stale": True, "age_s": None, "reason": "no-heartbeat-ever"}
    stale = heartbeat_age_s > threshold_s
    return {"stale": stale, "age_s": heartbeat_age_s,
            "reason": "age-exceeded" if stale else "fresh"}


def seq_reliability(gap_count: int, invalid_lines: int) -> dict:
    reliable = gap_count == 0 and invalid_lines == 0
    return {"reliable": reliable, "gaps": gap_count, "invalid": invalid_lines}


def process_death_triple(heartbeat_dead: bool, qga_state: str,
                         fb_static: bool) -> dict:
    """heartbeat-dead + QGA-alive + fb-static = app died in-guest."""
    if heartbeat_dead and qga_state == "ok" and fb_static:
        return {"process_dead": True,
                "diagnosis": "in-guest process death: agent alive, guest "
                             "alive, app silent, screen frozen"}
    return {"process_dead": False, "diagnosis": ""}
