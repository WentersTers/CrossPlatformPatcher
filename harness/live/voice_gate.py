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
# false expected-silence verdicts); max is computed from the census, not fixed.
ONSET_BUDGET = 40.0
HOLD_SECS = 5.0
MAX_MARGIN = 10.0
# Trigger slack: the harness touches the trigger ~15-20s after gate start
# (QGA roundtrips). Without it the onset budget races the max cap and
# silent draws close as max-total by 1s — same outcome, wrong reason.
TRIGGER_SLACK = 25.0
ONSET_PEAK_TH = 0.05

# Structural stopwords for token-overlap matching (command verbs excluded:
# they collide across every command; matching runs on content words).
_STOPWORDS = frozenset(
    "i you he she it we they me him her us them my your his its our their "
    "the a an to do does did is are was were be been and or but on in at "
    "of for with from that this so no yes not can could what how why when "
    "where there here".split())


def content_tokens(text: str) -> set[str]:
    return {w for w in normalize(text).split() if w not in _STOPWORDS}


def say_overlap(text: str, refs: set[str], min_common: int = 3) -> bool:
    """Variant matching: whisper garbles exact wording but preserves
    content words. Quorum of shared content tokens (single-token
    collisions cannot pass)."""
    t = content_tokens(text)
    if not t:
        return False
    return any(len(t & content_tokens(r)) >= min_common for r in refs)


def gate_config_for(largest_member_secs: float, onset: float = ONSET_BUDGET,
                    hold: float = HOLD_SECS,
                    margin: float = MAX_MARGIN,
                    slack: float = TRIGGER_SLACK) -> dict:
    """Census-computed gate window: onset + longest member + hold +
    margin + trigger slack (see TRIGGER_SLACK)."""
    return {"onset_budget": onset, "hold": hold,
            "max_total": onset + largest_member_secs + hold + margin + slack}


def draw_expectation(nbytes: int) -> str:
    """Audibility expectation from the measured bounds."""
    if nbytes <= AUD_MAX:
        return "audible"
    if nbytes >= SIL_MIN:
        return "silent"
    return "gap"


def check_gate_report(status: dict, raw_bytes: int, raw_peak: float,
                      raw_hot: list | None = None,
                      thr: float = ONSET_PEAK_TH) -> list[str]:
    """Trust treatment for the gate's self-report: cross-check the status
    JSON against its raw before any verdict is issued from it. Returns
    violations (empty = trusted). The report is an acknowledgment;
    the raw is the effect.

    With raw_hot (hot spans measured from the pulled raw: pull-then-check),
    consistency is two-sided but lag-tolerant: parec trails wall-clock by
    seconds under load, so positions are never compared, only existence.
    gate-hot + raw-hot = trusted (lag explains any shift); either side
    claiming alone is a violation that fails loud into forensics.
    Without raw_hot, falls back to peak comparison (weaker: misses lag)."""
    bad = []
    why = status.get("closed_why")
    if why not in ("no-onset", "baseline-held", "max-total", "no-trigger",
                   "no-flow"):
        bad.append(f"unknown close reason {why!r}")
    if why == "no-flow":
        # Instrument failure, never a verdict: harness fails loud and
        # retries. The checker accepts the shape; the driver owns the rest.
        if raw_bytes > 32000:
            bad.append("no-flow claim with substantial raw present")
        return bad
    if raw_bytes <= 0:
        bad.append("raw missing or empty")
    gate_hot = bool(status.get("hot_regions"))
    if raw_hot is not None:
        raw_has = len(raw_hot) > 0
        if why == "no-onset" and raw_has:
            bad.append("audio the gate never saw: pull forensics, then judge")
        if why in ("baseline-held", "max-total") and gate_hot and not raw_has:
            bad.append("claims response but raw is silent")
        if why == "baseline-held" and not gate_hot:
            bad.append("held close without hot regions")
        return bad
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
                      second_regions: list[tuple[float, float]] | None = None,
                      thr: float = ONSET_PEAK_TH) -> tuple[str, str]:
    """Terminal rule for the same-session re-draw. First-silent +
    redraw-audible = member-matched-with-redraw (and the redraw event is
    fate-distribution data). Both silent on a small member escalates to
    systematic suspicion: random GC death rarely strikes twice — hand to
    the fresh-session probes, confirmatory, not outlier-hunting.
    Fragment rule applies to redraws too (confabulation risk is
    path-independent): all-fragment redraw regions are inconclusive."""
    if second_regions and all(is_fragment(b - a) for a, b in second_regions):
        return FRAGMENT_INCONCLUSIVE, \
            "re-draw regions all under %.1fs" % FRAGMENT_MIN_SECS
    if second_peak >= thr:
        if second_text and say_membership(second_text, refs):
            return MEMBER_MATCHED, "matched on re-draw; redraw is fate data"
        if second_text and say_overlap(second_text, refs):
            return MEMBER_MATCHED, "variant-matched on re-draw"
        return OBSERVE_RECORD, "audible re-draw outside refs: record it"
    return OBSERVE_RECORD, \
        "silent twice on audible-expected member: systematic suspicion, " \
        "escalate to fresh-session probes"


def adjudicate_say(draw_bytes: int, bus_peak: float,
                   regions: list[tuple[float, float, str]],
                   refs: set[str], known_bug: bool = False,
                   draw_audio: str | None = None,
                   member_audio: str | None = None,
                   thr: float = ONSET_PEAK_TH) -> tuple[str, str]:
    """One dispatch -> (bucket, detail). regions: (start_s, end_s, text)
    hot spans from the gated window; refs: this draw's pool-member
    reference set (caller-supplied). draw_audio/member_audio identify the
    log-named draw vs the script-named member for the draw-prior match:
    the identified file playing audibly IS the primary evidence (the log
    identifies, the bus verifies); a shared content token corroborates
    against whisper variance. Fragments never reach this clause (gated
    above), so confabulations cannot ride it."""
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
    for _, _, text in solid:
        if say_overlap(text, refs):
            return MEMBER_MATCHED, "variant match: content-token quorum"
    if (draw_audio and member_audio and draw_audio == member_audio
            and any(content_tokens(t) & content_tokens(r)
                    for _, _, t in solid for r in refs)):
        return MEMBER_MATCHED, \
            "draw-prior match: identified file audible, shared content"
    if draw_expectation(draw_bytes) == "gap":
        return OBSERVE_RECORD, "audible gap draw: cap-bracketing datum"
    return OBSERVE_RECORD, "audible draw outside reference set: record it"
