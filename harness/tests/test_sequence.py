"""Tier C: sequence order/duration/triggers/OCR; divergence; adaptive Hz."""
import asyncio

from harness.gates.gate2_5_state.sequence import (SequenceResult, sequence_matches,
                                                  verify_state_sequence)
from harness.gates.gate2_5_state.template_matcher import StateMatch


def _frames(states_with_t):
    from harness.gates.gate2_5_state.sequence import ObservedFrame
    return [ObservedFrame(s, t, 0.9, "template") for s, t in states_with_t]


def test_sequence_matches_order_and_duration():
    exp = [{"state": "idle", "min_duration_s": 0.5},
           {"state": "listening", "min_duration_s": 0.3}]
    obs = _frames([("idle", 0.0), ("idle", 0.6), ("listening", 1.0), ("listening", 1.5)])
    assert sequence_matches(obs, exp) is True


def test_flicker_through_fails_duration():
    exp = [{"state": "idle", "min_duration_s": 0.5}]
    obs = _frames([("idle", 0.0), ("idle", 0.1), ("listening", 0.2)])
    assert sequence_matches(obs, exp) is False


def test_skipped_state_fails():
    exp = [{"state": "idle", "min_duration_s": 0.0},
           {"state": "thinking", "min_duration_s": 0.0},
           {"state": "idle", "min_duration_s": 0.0}]
    obs = _frames([("idle", 0.0), ("idle", 0.1)])
    assert sequence_matches(obs, exp) is False


async def _run(script, expected, event_log=None, ocr=None, sleeps=None):
    it = iter(script)
    clock = [0.0]
    seen_sleeps: list = [] if sleeps is None else sleeps

    def capture():
        return next(it, script[-1])

    def classify(shot):
        return StateMatch(shot, 0.95, "template")

    async def sleep(d):
        seen_sleeps.append(d)
        clock[0] += d

    return await verify_state_sequence(
        expected, timeout_s=30.0, capture_fn=capture, classify_fn=classify,
        sleep_fn=sleep, time_fn=lambda: clock[0],
        event_log=event_log, ocr_text_fn=(lambda s: ocr) if ocr is not None else None), seen_sleeps


def test_verify_full_wake_word_sequence():
    expected = [{"state": "idle", "min_duration_s": 0.5},
                {"state": "listening", "min_duration_s": 0.3, "trigger": "wake_word_detected"},
                {"state": "thinking", "min_duration_s": 1.0},
                {"state": "speaking", "min_duration_s": 1.5, "expected_text_regex": ".*here.*"},
                {"state": "idle", "min_duration_s": 1.0}]
    # each capture returns next state; need enough repeats for durations with 0.5/2.0 sleeps
    script = (["idle"] * 3 + ["listening"] * 3 + ["thinking"] * 3
              + ["speaking"] * 5 + ["idle"] * 4)
    res, sleeps = asyncio.run(_run(script, expected,
                                   event_log=["wake_word_detected at 1.2s"],
                                   ocr="the browser is here"))
    assert res.passed, res.divergence_point
    assert [f.state for f in res.observed][:1] == ["idle"]


def test_missing_trigger_fails():
    expected = [{"state": "idle", "min_duration_s": 0.0},
                {"state": "listening", "min_duration_s": 0.0, "trigger": "wake_word_detected"}]
    res, _ = asyncio.run(_run(["idle", "listening", "listening"], expected, event_log=[]))
    assert not res.passed and res.reason == "trigger-missing"


def test_timeout_reports_divergence():
    expected = [{"state": "idle", "min_duration_s": 0.0},
                {"state": "speaking", "min_duration_s": 0.0}]
    res, _ = asyncio.run(_run(["idle"] * 3, expected))
    assert not res.passed and res.reason == "timeout"
    assert res.divergence_point is not None


def test_ambiguous_frames_skipped_not_deviation():
    from harness.gates.gate2_5_state.sequence import MAX_CONSECUTIVE_AMBIGUOUS
    assert MAX_CONSECUTIVE_AMBIGUOUS >= 4
    exp = [{"state": "idle", "min_duration_s": 0.0},
           {"state": "listening", "min_duration_s": 0.0}]
    # transitional ambiguous between real states must not fail the sequence
    script = ["idle", "ambiguous", "ambiguous", "listening", "listening"]
    res, _ = asyncio.run(_run(script, exp))
    assert res.passed, res.divergence_point


def test_ambiguous_storm_fails():
    exp = [{"state": "idle", "min_duration_s": 0.0},
           {"state": "speaking", "min_duration_s": 0.0}]
    res, _ = asyncio.run(_run(["ambiguous"] * 10, exp))
    assert not res.passed and res.reason == "ambiguous-storm"


def _frozen_run(script, expected, frozen, log_delta):
    from harness.gates.gate2_5_state.sequence import verify_state_sequence
    it = iter(script)
    clock = [0.0]

    async def sleep(d):
        clock[0] += d

    async def main():
        return await verify_state_sequence(
            expected, timeout_s=60.0, capture_fn=lambda: next(it, "idle"),
            classify_fn=lambda s: StateMatch(s, 0.3, "template"),
            sleep_fn=sleep, time_fn=lambda: clock[0],
            fb_frozen_fn=lambda: frozen, log_delta_fn=lambda: log_delta)

    return asyncio.run(main())


def test_storm_row_confirmed_live_shape():
    """Session-5 live shape, pinned: frozen fb + silent logs + persistent
    ambiguous -> wrong-ROI-or-no-pet (fail verdict, right diagnosis, storm
    suppressed). Changing fb or live logs keep the legacy storm label."""
    exp = [{"state": "idle", "min_duration_s": 0.0}]
    r = _frozen_run(["ambiguous"] * 10, exp, True, 0)
    assert not r.passed and r.reason == "wrong-ROI-or-no-pet"
    assert r.divergence_point["storm_suppressed"] is True
    r2 = _frozen_run(["ambiguous"] * 10, exp, False, 0)
    assert r2.reason == "ambiguous-storm"  # fb changing: genuinely unclear
    r3 = _frozen_run(["ambiguous"] * 10, exp, True, 40)
    assert r3.reason == "ambiguous-storm"  # logs alive: something happening


def test_listed_transitional_state_passes():
    """Counter-case to strict interleave: a *listed* transitional state
    (e.g. settling between thinking and speaking) passes; an *unlisted*
    interleave still fails. This policy is the calibration point between
    D9 firing constantly (too strict) and deviations slipping through."""
    from harness.gates.gate2_5_state.sequence import sequence_matches
    # allow_transitional on expected[j] permits those states while WAITING
    # for expected[j] (i.e. list the transitional on the FOLLOWING state).
    exp = [{"state": "thinking", "min_duration_s": 0.0},
           {"state": "speaking", "min_duration_s": 0.0,
            "allow_transitional": ["settling"]}]
    obs = _frames([("thinking", 0.0), ("settling", 1.0), ("speaking", 2.0)])
    assert sequence_matches(obs, exp) is True
    obs_bad = _frames([("thinking", 0.0), ("acting", 1.0), ("speaking", 2.0)])
    assert sequence_matches(obs_bad, exp) is False
