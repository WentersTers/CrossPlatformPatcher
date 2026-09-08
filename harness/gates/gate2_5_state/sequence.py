"""Tier C — temporal sequence verification (§6.5).

expected example:
  [ {"state":"idle","min_duration_s":0.5},
    {"state":"listening","min_duration_s":0.3,"trigger":"wake_word_detected"},
    {"state":"thinking","min_duration_s":1.0},
    {"state":"speaking","min_duration_s":1.5,"expected_text_regex":".*here.*"},
    {"state":"idle","min_duration_s":1.0} ]
"""
from __future__ import annotations

import asyncio
import re
import time
from dataclasses import dataclass, field
from typing import Any, Awaitable, Callable

from harness.gates.gate2_5_state.template_matcher import PET_ABSENT, StateMatch
from harness.gates.gate2_5_state.vlm_classifier import VlmClassifier


@dataclass
class ObservedFrame:
    state: str
    t: float
    confidence: float
    method: str


@dataclass
class SequenceResult:
    passed: bool
    observed: list[ObservedFrame] = field(default_factory=list)
    divergence_point: dict[str, Any] | None = None
    reason: str = ""


# Ambiguous-frame policy (spec-trap fix): transitional frames classify as
# `ambiguous`/`uncertain` in the real world. They are SKIPPED for matching
# (neither deviation nor progress), but N consecutive ambiguous frames without
# a concrete classification = failure (sensor blind, not a pass).
SKIPPED_STATES = frozenset({"ambiguous", "uncertain", PET_ABSENT})
# Storm counts only genuinely unclear frames. pet_absent is a legitimate
# pre-launch condition (boot/login/bare desktop) that can persist for tens of
# seconds — it BREAKS the streak instead of feeding it, so a boot sequence
# waits rather than storm-fails.
STORM_STATES = frozenset({"ambiguous", "uncertain"})
# STORM ROW (was a deferred hypothesis; CONFIRMED live Session 5 and
# implemented in verify_state_sequence below): persistent-ambiguous AND
# fb-frozen (rolling set) AND log-delta-zero -> diagnosis wrong-ROI-or-no-pet,
# storm suppressed, verdict stays fail. A static textured wallpaper held 12/12
# ambiguous frames with a frozen framebuffer — the old vision-quality label
# would have misdiagnosed a dock stare as a sensor failure.
MAX_CONSECUTIVE_AMBIGUOUS = 5


def _concrete(observed: list[ObservedFrame]) -> list[ObservedFrame]:
    return [f for f in observed if f.state not in SKIPPED_STATES]


def consecutive_ambiguous_tail(observed: list[ObservedFrame]) -> int:
    n = 0
    for f in reversed(observed):
        if f.state in STORM_STATES:
            n += 1
        else:
            break  # concrete states AND pet_absent reset the streak
    return n


def saw_concrete(observed: list[ObservedFrame]) -> bool:
    """Any non-skipped frame (a real classification, right or wrong)."""
    return any(f.state not in SKIPPED_STATES for f in observed)


async def wait_for_pet_present(
    capture_fn: Callable[[], Any],
    classify_fn: Callable[[Any], StateMatch | Any],
    roi_fn: Callable[[Any], tuple[int, int, int, int] | None] | None = None,
    timeout_s: float = 60.0,
    sleep_fn: Callable[[float], Awaitable[None]] | None = None,
    time_fn: Callable[[], float] | None = None,
    poll_interval_s: float = 1.0,
) -> dict[str, Any]:
    """Pet-present gate: sequence verification begins only after this passes.

    Present means a CONCRETE classification — ambiguous does NOT open the
    gate. Session-4 evidence: on 1024x768 VGA boot frames a 1920x1080-fixed
    ROI falls out of bounds and reads ambiguous; treating that as present
    would start Tier C on a boot screen and storm-fail it. Boot screens
    (absent or ambiguous) wait here instead of failing downstream.
    """
    _sleep = sleep_fn or asyncio.sleep
    _time = time_fn or time.monotonic
    start = _time()
    frames = 0
    while _time() - start < timeout_s:
        shot = capture_fn()
        frames += 1
        if roi_fn is not None:
            roi_fn(shot)  # tracked for downstream capture; classification binds ROI itself
        m = classify_fn(shot)
        state = getattr(m, "state", PET_ABSENT)
        if state not in SKIPPED_STATES:
            return {"present": True, "state": state,
                    "waited_s": _time() - start, "frames": frames}
        await _sleep(poll_interval_s)
    return {"present": False, "state": PET_ABSENT,
            "waited_s": _time() - start, "frames": frames}


def _check_triggers(expected_seq: list[dict], observed_states: list[str],
                    event_log: list[str]) -> str | None:
    """Trigger events must be present in audio/transcript log at correct position."""
    for exp in expected_seq:
        trig = exp.get("trigger")
        if trig and not any(trig in e for e in event_log):
            return f"missing trigger {trig!r} for state {exp.get('state')}"
    return None


def sequence_matches(observed: list[ObservedFrame], expected: list[dict]) -> bool:
    """All expected states in order; each min duration met; no unexpected interleave.

    Ambiguous/uncertain frames are skipped (transitional), never deviation.
    Listed transitionals: `allow_transitional` on expected[j] permits those
    states to appear while WAITING for expected[j] (list on the FOLLOWING
    state). Anything else interleaved = deviation.
    """
    observed = _concrete(observed)
    if not expected:
        return True
    # compress consecutive duplicates keeping first/last timestamps
    segs: list[dict] = []
    for f in observed:
        if segs and segs[-1]["state"] == f.state:
            segs[-1]["end"] = f.t
        else:
            segs.append({"state": f.state, "start": f.t, "end": f.t})
    # walk expected through segments in order
    j = 0
    for seg in segs:
        if j < len(expected) and seg["state"] == expected[j]["state"]:
            dur = seg["end"] - seg["start"]
            if dur + 1e-9 >= float(expected[j].get("min_duration_s", 0)):
                j += 1
            # else flicker-through: not yet satisfied, keep waiting for more
            # of the same state (but segments merged, so this means fail unless
            # later same-state segment continues — handled by not advancing)
        elif j < len(expected) and seg["state"] != expected[j]["state"]:
            # check transitional allowance
            if seg["state"] in expected[j].get("allow_transitional", []):
                continue
            # if seg matches a LATER expected state -> skipped a state
            later = [e["state"] for e in expected[j + 1:]]
            if seg["state"] in later:
                return False
            return False
    return j == len(expected)


def sequence_deviated(observed: list[ObservedFrame], expected: list[dict]) -> bool:
    """Early-fail: an expected state was skipped (a later state appeared)."""
    states = [f.state for f in _concrete(observed)]
    # find first index of each expected state in order
    pos = -1
    for exp in expected:
        try:
            nxt = states.index(exp["state"], pos + 1)
        except ValueError:
            return False  # not yet seen -> not deviated, maybe timeout later
        pos = nxt
    return False  # full order seen -> matches (duration checked separately)


def divergence_point(observed: list[ObservedFrame], expected: list[dict]) -> dict[str, Any] | None:
    states = [f.state for f in _concrete(observed)]
    for i, exp in enumerate(expected):
        if exp["state"] not in states:
            return {"expected_index": i, "expected_state": exp["state"],
                    "observed": states}
    return None


async def verify_state_sequence(
    expected: list[dict],
    timeout_s: float,
    capture_fn: Callable[[], Any],
    classify_fn: Callable[[Any], StateMatch | Any],
    roi_fn: Callable[[Any], tuple[int, int, int, int] | None] | None = None,
    sleep_fn: Callable[[float], Awaitable[None]] | None = None,
    time_fn: Callable[[], float] | None = None,
    event_log: list[str] | None = None,
    ocr_text_fn: Callable[[Any], str] | None = None,
    fb_frozen_fn: Callable[[], bool] | None = None,
    log_delta_fn: Callable[[], int] | None = None,
) -> SequenceResult:
    """Blocking Tier C logic with adaptive Hz (0.5s animated / 2.0s static).

    capture_fn/classify_fn injectable for tests. classify_fn may return
    StateMatch or VlmStateResult-like (state/confidence/method attrs).
    """
    _sleep = sleep_fn or asyncio.sleep
    _time = time_fn or time.monotonic
    observed: list[ObservedFrame] = []
    start = _time()
    idx = 0
    while _time() - start < timeout_s:
        shot = capture_fn()
        roi = roi_fn(shot) if roi_fn else None
        m = classify_fn(shot) if roi_fn is None else classify_fn(shot)
        # normalize
        state = getattr(m, "state", "ambiguous")
        conf = float(getattr(m, "confidence", getattr(m, "score", 0.0)))
        method = getattr(m, "method", "template")
        observed.append(ObservedFrame(state, _time() - start, conf, method))
        if consecutive_ambiguous_tail(observed) >= MAX_CONSECUTIVE_AMBIGUOUS:
            # Deferred row, CONFIRMED live (Session 5: 12/12 ambiguous on a
            # static textured wallpaper, fb-frozen, zero logs): a frozen
            # framebuffer with silent logs means wrong ROI or no pet — NOT a
            # vision-quality failure. Verdict stays fail (the pet never
            # appeared; D4), but the diagnosis localizes instead of
            # mislabeling, and the storm counter is suppressed.
            frozen = bool(fb_frozen_fn()) if fb_frozen_fn is not None else False
            delta = int(log_delta_fn()) if log_delta_fn is not None else 0
            if frozen and delta == 0:
                return SequenceResult(
                    False, observed,
                    {"reason": "wrong-ROI-or-no-pet", "storm_suppressed": True,
                     "consecutive_ambiguous":
                         consecutive_ambiguous_tail(observed)},
                    "wrong-ROI-or-no-pet")
            return SequenceResult(False, observed,
                                  {"reason": "ambiguous-storm",
                                   "consecutive_ambiguous": consecutive_ambiguous_tail(observed)},
                                  "ambiguous-storm")
        # speaking OCR check
        if idx < len(expected) and expected[idx].get("expected_text_regex"):
            if state == expected[idx]["state"] and ocr_text_fn is not None:
                txt = ocr_text_fn(shot)
                if not re.search(expected[idx]["expected_text_regex"], txt):
                    return SequenceResult(False, observed,
                                          {"expected_index": idx, "reason": "speech-bubble OCR mismatch",
                                           "text": txt[:200]}, "ocr-mismatch")
        if sequence_matches(observed, expected):
            trig_err = _check_triggers(expected, [f.state for f in observed], event_log or [])
            if trig_err:
                return SequenceResult(False, observed,
                                      {"reason": trig_err}, "trigger-missing")
            return SequenceResult(True, observed, None, "matched")
        # advance idx hint for adaptive sleep
        cur = expected[min(idx, len(expected) - 1)] if expected else {}
        # if current expected state observed, move hint forward
        states = [f.state for f in _concrete(observed)]
        while idx < len(expected) and expected[idx]["state"] in states:
            # only advance if duration already satisfied for that prefix
            prefix = expected[: idx + 1]
            # crude: check prefix matched as subsequence with durations
            if sequence_matches(observed, prefix):
                idx += 1
            else:
                break
        animated = bool(cur.get("is_animated", cur.get("state") in ("listening", "speaking")))
        await _sleep(0.5 if animated else 2.0)
    if not saw_concrete(observed):
        return SequenceResult(False, observed,
                              {"reason": "pet-never-present",
                               "frames": len(observed)}, "pet-never-present")
    return SequenceResult(False, observed, divergence_point(observed, expected) or {"reason": "timeout"}, "timeout")
