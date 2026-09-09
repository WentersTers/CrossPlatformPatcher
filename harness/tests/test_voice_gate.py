"""Voice-gate adjudication rules: config, trust checks, SAY buckets.

Encoded live (replication 10/10, loop record/verify, mini-smoke).
"""
import pytest

from harness.live import voice_gate as vg
from harness.live.voice_gateplay import GATEPLAY_SOURCE


def test_gate_config_from_census():
    cfg = vg.gate_config_for(32.6)
    assert cfg == {"onset_budget": 40.0, "hold": 5.0,
                   "max_total": 40.0 + 32.6 + 5.0 + 10.0}


def test_draw_expectation_bounds():
    assert vg.draw_expectation(771876) == "audible"
    assert vg.draw_expectation(3381084) == "audible"
    assert vg.draw_expectation(5557596) == "gap"
    assert vg.draw_expectation(9087636) == "silent"
    assert vg.draw_expectation(12527212) == "silent"


def test_trust_silent_close():
    st = {"closed_why": "no-onset", "onset_s": None, "end_s": 40.06,
          "hot_regions": []}
    assert vg.check_gate_report(st, 2058576, 0.0) == []


def test_trust_held_close():
    st = {"closed_why": "baseline-held", "onset_s": 0.0, "end_s": 6.01,
          "hot_regions": [[0.0, 0.5]]}
    assert vg.check_gate_report(st, 955288, 0.4838) == []


def test_trust_lie_caught():
    st = {"closed_why": "no-onset", "onset_s": None, "end_s": 40.06,
          "hot_regions": []}
    bad = vg.check_gate_report(st, 1500000, 0.48)
    assert any("silence" in v for v in bad)


def test_trust_empty_raw_caught():
    st = {"closed_why": "baseline-held", "onset_s": 1.0, "end_s": 5.0,
          "hot_regions": [[1.0, 2.0]]}
    bad = vg.check_gate_report(st, 0, 0.0)
    assert len(bad) == 2  # empty raw + peak mismatch


def test_member_matched():
    refs = {"i can hear you loud and eager"}
    b, _ = vg.adjudicate_say(771876, 0.48, [(12.0, 14.0, "I can hear you! Loud and eager.")], refs)
    assert b == vg.MEMBER_MATCHED


def test_expected_silence_big_member():
    b, _ = vg.adjudicate_say(12527212, 0.0, [], {"a joke"})
    assert b == vg.EXPECTED_SILENCE


def test_silent_audible_member_is_not_a_verdict():
    assert vg.needs_redraw(3381084, 0.0) is True
    assert vg.needs_redraw(12527212, 0.0) is False
    b, d = vg.adjudicate_say(3381084, 0.0, [], {"you didn't care"})
    assert b == vg.OBSERVE_RECORD and "redraw" in d


def test_silent_gap_draw_is_bracketing_datum():
    b, d = vg.adjudicate_say(5557596, 0.0, [], {"something"})
    assert b == vg.OBSERVE_RECORD and "bracketing" in d


def test_fragment_never_matches():
    b, d = vg.adjudicate_say(771876, 0.48, [(20.0, 21.0, "Can you do that one?")],
                             {"i can hear you loud and eager"})
    assert b == vg.FRAGMENT_INCONCLUSIVE
    assert vg.is_fragment(1.0) and not vg.is_fragment(1.5)


def test_known_bug_entry():
    b, _ = vg.adjudicate_say(12527212, 0.0, [], set(), known_bug=True)
    assert b == vg.KNOWN_BUG


def test_redraw_terminal_rule():
    refs = {"you didn't care"}
    b, _ = vg.adjudicate_redraw(0.0, 0.41, refs, "You didn't care.")
    assert b == vg.MEMBER_MATCHED
    b, d = vg.adjudicate_redraw(0.0, 0.0, refs)
    assert b == vg.OBSERVE_RECORD and "systematic suspicion" in d


def test_gateplay_source_exercised_shape():
    assert "parec" in GATEPLAY_SOURCE and "no-onset" in GATEPLAY_SOURCE
    assert "baseline-held" in GATEPLAY_SOURCE and "max-total" in GATEPLAY_SOURCE
    compile(GATEPLAY_SOURCE, "gateplay", "exec")
