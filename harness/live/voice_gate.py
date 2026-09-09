"""Voice-loop adjudication rules: gated SAY windows, trust-checked instruments.

Encoded from the wake-word phase (replication 10/10, loop v1 record,
v2 verify, mini-smoke): every dispatch lands in exactly one SAY bucket,
decided by evidence the harness produced and cross-checked.

Buckets: member-matched | expected-silence | known-bug |
observe-and-record | fragment-inconclusive. A silent draw of an
audible-expected member is never a verdict — needs_redraw() says so;
re-draw once same-session (GC-lifetime nondeterminism, not failure).

Size bounds below are MEASURED (guest audio tree, Sep 2026), not
universal: biggest file ever heard playing, smallest ever heard silent.
Gap draws are cap-bracketing data, not failures.

No product content lives here: command text, name variants, and
known-bug entries are per-run data (harness/runs/, ignored). Callers
supply reference sets; this module only compares.
"""
from __future__ import annotations

import re

# SAY buckets: the full run's verdict space.
MEMBER_MATCHED = "member-matched"
EXPECTED_SILENCE = "expected-silence"
KNOWN_BUG = "known-bug"
OBSERVE_RECORD = "observe-and-record"
FRAGMENT_INCONCLUSIVE = "fragment-inconclusive"

# Measured audibility bounds (bytes). See module docstring.
AUD_MAX = 3381084
SIL_MIN = 9087636

# Regions shorter than this carry confabulation risk (whisper invents
# completions on fragments): mark inconclusive, never match.
FRAGMENT_MIN_SECS = 1.5

# Gate defaults: onset budget errs long (a short budget manufactures
# false expected-silence); max is computed from the census, not fixed.
ONSET_BUDGET = 40.0
HOLD_SECS = 5.0
MAX_MARGIN = 10.0
ONSET_PEAK_TH = 0.05


def gate_config_for(largest_member_secs: float, onset: float = ONSET_BUDGET,
                    hold: float = HOLD_SECS,
                    margin: float = MAX_MARGIN) -> dict:
    """Census-computed gate window: onset + longest member + hold + margin."""
    return {"onset_budget": onset, "hold": hold,
            "max_total": onset + largest_member_secs + hold + margin}


def draw_expectation(nbytes: int) -> str:
    """Audibility expectation from the measured bounds."""
    if nbytes <= AUD_MAX:
        return "audible"
    if nbytes >= SIL_MIN:
        return "silent"
    return "gap"


def check_gate_report(status: dict, raw_bytes: int, raw_peak: float,
                      thr: float = ONSET_PEAK_TH) -> list[str]:
    """Trust treatment for the gate's self-report: cross-check the status
    JSON against its raw before any verdict is issued from it. Returns
    violations (empty = trusted). The report is an acknowledgment;
    the raw is the effect."""
    bad = []
    why = status.get("closed_why")
    if why not in ("no-onset", "baseline-held", "max-total", "no-trigger"):
        bad.append(f"unknown close reason {why!r}")
    if raw_bytes <= 0:
        bad.append("raw missing or empty")
    if why == "no-onset" and raw_peak >= thr:
        bad.append(f"claims silence but raw peaks at {raw_peak}")
    if why == "baseline-held" and not status.get("hot_regions"):
        bad.append("claims held response but reports no hot regions")
    if why == "baseline-held" and raw_peak < thr:
        bad.append(f"claims held response but raw peaks at {raw_peak}")
    onset, end = status.get("onset_s"), status.get("end_s")
    if why == "baseline-held" and (onset is None or end is None):
        bad.append("held close without onset/end times")
    return bad


def normalize(text: str) -> str:
    return re.sub(r"\s+", " ", re.sub(r"[^a-z0-9 ]", "", text.lower())).strip()


def say_membership(text: str, refs: set[str]) -> bool:
    """Set membership, not string equality: response pools produce
    variants. Normalized containment either direction (whisper adds or
    drops filler around the canonical line)."""
    t = normalize(text)
    if not t:
        return False
    return any(normalize(r) and (normalize(r) in t or t in normalize(r))
               for r in refs)


def is_fragment(span_secs: float) -> bool:
    return span_secs < FRAGMENT_MIN_SECS


def needs_redraw(draw_bytes: int, bus_peak: float,
                 thr: float = ONSET_PEAK_TH) -> bool:
    """A silent draw of an audible-expected member is nondeterminism
    (GC-lifetime race), not a verdict: re-draw once same-session."""
    return draw_expectation(draw_bytes) == "audible" and bus_peak < thr


def adjudicate_redraw(first_peak: float, second_peak: float, refs: set[str],
                      second_text: str = "",
                      thr: float = ONSET_PEAK_TH) -> tuple[str, str]:
    """Terminal rule for the same-session re-draw. First-silent +
    redraw-audible = member-matched-with-redraw (and the redraw event is
    fate-distribution data). Both silent on a small member escalates to
    systematic suspicion: random GC death rarely strikes twice — hand to
    the fresh-session probes, confirmatory, not outlier-hunting."""
    if second_peak >= thr:
        if second_text and say_membership(second_text, refs):
            return MEMBER_MATCHED, "matched on re-draw; redraw is fate data"
        return OBSERVE_RECORD, "audible re-draw outside refs: record it"
    return OBSERVE_RECORD, \
        "silent twice on audible-expected member: systematic suspicion, " \
        "escalate to fresh-session probes"


def adjudicate_say(draw_bytes: int, bus_peak: float,
                   regions: list[tuple[float, float, str]],
                   refs: set[str], known_bug: bool = False,
                   thr: float = ONSET_PEAK_TH) -> tuple[str, str]:
    """One dispatch -> (bucket, detail). regions: (start_s, end_s, text)
    hot spans from the gated window; refs: this draw's pool-member
    reference set (caller-supplied)."""
    if known_bug:
        return KNOWN_BUG, "confirmed misroute entry: holds across sessions"
    if not regions and bus_peak < thr:
        exp = draw_expectation(draw_bytes)
        if exp == "silent":
            return EXPECTED_SILENCE, "log-identified big member, bus zero"
        if exp == "audible":
            return OBSERVE_RECORD, \
                "silent audible-expected draw: redraw once (needs_redraw)"
        return OBSERVE_RECORD, "silent gap draw: cap-bracketing datum"
    solid = [(a, b, t) for a, b, t in regions if not is_fragment(b - a)]
    if regions and not solid:
        return FRAGMENT_INCONCLUSIVE, \
            "all regions under %.1fs: confabulation risk" % FRAGMENT_MIN_SECS
    for _, _, text in solid:
        if say_membership(text, refs):
            return MEMBER_MATCHED, "transcript in reference set"
    if draw_expectation(draw_bytes) == "gap":
        return OBSERVE_RECORD, "audible gap draw: cap-bracketing datum"
    return OBSERVE_RECORD, "audible draw outside reference set: record it"
