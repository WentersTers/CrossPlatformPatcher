"""Three-way reconcile: expected vs claimed (intent) vs observed (effect).

Vision stays authoritative for pass/fail (D4); this turns detection into
diagnosis and localizes faults vision-only testing cannot distinguish.
"""
from __future__ import annotations

from dataclasses import dataclass


@dataclass
class Verdict:
    passed: bool
    diagnosis: str
    detail: str = ""
    latency_ms: float | None = None


def reconcile(expected: str | None,
              claimed: str | None,
              observed: str | None,
              *,
              observed_latency_ms: float | None = None,
              settle_ms: float = 500.0,
              stream_reliable: bool = True,
              heartbeat_alive: bool = True,
              screen_changed: bool = False,
              require_intent_match: bool = False) -> Verdict:
    """One step of expected/claimed/observed. `observed=None` = no change.

    Disagreement inside the settle window is EXPECTED (event fires before the
    render lands) — callers must pass post-settle observations; the timing row
    measures event->capture latency as a per-state, per-OS metric.
    """
    if not heartbeat_alive:
        return Verdict(False, "process-dead",
                       "heartbeat dead; confirm app-vs-VM with QGA + lifecycle")
    if not stream_reliable and claimed is not None:
        # Intent lane MISSING (gapped stream): fall back to vision-only.
        # A present claim on an unreliable stream may be STALE — the true
        # transition may be among the lost events — so it must never flip
        # a verdict or attribute failure to intent contradiction. Absence
        # rows below keep their telemetry-unreliable diagnoses; this branch
        # covers only present-but-untrustworthy claims.
        if require_intent_match:
            return Verdict(False, "telemetry-unreliable",
                           "strict intent match requires a reliable stream: "
                           "intent unprovable, not contradicted")
        if observed == expected:
            return Verdict(True, "pass",
                           "effect correct; intent lane degraded "
                           "(unreliable stream) — no intent attribution",
                           observed_latency_ms)
        if observed is None and not screen_changed:
            return Verdict(False, "effect-absent",
                           "no visual change; intent lane degraded — "
                           "vision-only verdict, no render attribution")
        return Verdict(False, "effect-mismatch",
                       f"observed {observed!r} != expected {expected!r}; "
                       "intent lane degraded — vision-only verdict",
                       observed_latency_ms)
    if require_intent_match and claimed != expected:
        return Verdict(False, "intent-mismatch",
                       f"claimed {claimed!r} != expected {expected!r} (strict)")
    if claimed == expected and observed == expected:
        if (observed_latency_ms is not None
                and observed_latency_ms > settle_ms):
            return Verdict(False, "timing",
                           f"latency {observed_latency_ms:.0f}ms > settle "
                           f"{settle_ms:.0f}ms", observed_latency_ms)
        return Verdict(True, "pass", "intent and effect agree",
                       observed_latency_ms)
    if claimed == expected:  # intent right, effect wrong
        if observed is None and not screen_changed:
            return Verdict(False, "render-no-update",
                           f"claimed {claimed!r}, no visual change: "
                           "render/sprite-update bug, state machine fine")
        return Verdict(False, "render-or-vision",
                       f"claimed {claimed!r}, observed {observed!r}: render "
                       "bug or vision misclassification — golden review "
                       "disambiguates")
    if claimed is None and not screen_changed and observed in (None, expected):
        if not stream_reliable:
            return Verdict(False, "telemetry-unreliable",
                           "no claim on a gapped stream: absence is not "
                           "evidence of stuck")
        if observed == expected:
            return Verdict(False, "unclaimed-expected",
                           "expected state visible with no claim on a "
                           "reliable stream: pre-join transition or missing hook")
        return Verdict(False, "stuck",
                       "heartbeat alive, no transitions, no change: "
                       "state machine stuck")
    if claimed is None and screen_changed:
        if not stream_reliable:
            return Verdict(False, "telemetry-unreliable",
                           "screen changed with no claim on a gapped stream: "
                           "event loss, not evidence")
        return Verdict(False, "unclaimed-change",
                       "screen changed with no claim on a reliable stream: "
                       "non-state change (see command_exec) or lost hook")
    # claimed present but wrong (non-strict mode)
    if observed == expected:
        return Verdict(True, "pass",
                       f"effect {observed!r} correct despite claimed "
                       f"{claimed!r} (intent diverged, non-strict)")
    return Verdict(False, "intent-effect-mismatch",
                   f"claimed {claimed!r}, observed {observed!r}, "
                   f"expected {expected!r}")
