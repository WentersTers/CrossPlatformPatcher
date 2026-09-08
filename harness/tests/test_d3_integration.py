"""D3 integration: full-loop false-pass verdicts flow into the tracker and fire.

Closes gate output -> verdict -> D3, the exact path disarmed before
TriggerTracker owned the stream.
"""
from harness.gates.gate1_deterministic import evaluate as g1
from harness.manager.manager_agent import ManagerAgent


def _gate_says_fail():
    return g1({"exit_code": 1, "env": {}, "libs": []})  # deterministic FAIL


def test_false_pass_verdicts_fire_d3_through_manager():
    mgr = ManagerAgent()
    # loop 1: worker says pass, deterministic gate says fail -> false-pass #1
    assert _gate_says_fail().passed is False
    d1 = mgr.record_test_outcome(worker_said_pass=True, gate_said_pass=False,
                                 gate_disagreed=True)
    assert d1.escalate is False  # only 1 consecutive
    # loop 2: same again -> >=2 consecutive false-pass fires mechanically
    d2 = mgr.record_test_outcome(worker_said_pass=True, gate_said_pass=False,
                                 gate_disagreed=True)
    assert d2.escalate is True and "D3" in d2.reason
    assert any(e.get("action") == "force-option-b" for e in mgr.escalations)


def test_agreement_resets_streak_through_manager():
    mgr = ManagerAgent()
    mgr.record_test_outcome(True, False, True)
    mgr.record_test_outcome(True, True, False)  # agreement breaks streak
    d = mgr.record_test_outcome(True, False, True)
    assert d.escalate is False
