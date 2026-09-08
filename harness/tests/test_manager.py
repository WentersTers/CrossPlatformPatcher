"""Manager: schema validation, budgets/anti-loop, D3/D9 mechanical triggers."""
import pytest

from harness.manager.budgets import BudgetTracker
from harness.manager.delegation import completion_predicate, validate_task
from harness.manager.escalation import (should_force_option_b,
                                        should_review_golden_library)
from harness.manager.manager_agent import ManagerAgent
from harness.workers.worker_agent import WorkerAgent, WorkerReport


def test_task_schema_rejects_malformed():
    with pytest.raises(ValueError):
        validate_task({"id": "x"})  # missing fields
    with pytest.raises(ValueError):
        validate_task({"id": "x", "type": "boot", "params": [],
                       "success_criteria": "y"})
    t = validate_task({"id": "wake1", "type": "wake_word_seq",
                       "params": {"device": "pulse"}, "success_criteria": "seq pass"})
    assert t.id == "wake1"
    assert "assert_state_sequence" in completion_predicate("wake_word_seq")


def test_budgets_anti_loop_and_caps():
    b = BudgetTracker()
    b.record(text_tokens=1000, image_tokens=9000)
    assert b.split() == {"text_tokens": 1000, "image_tokens": 9000, "total": 10000}
    for _ in range(3):
        b.record_attempt("w1", "t1")
    assert b.anti_loop_tripped("w1", "t1") is True
    assert b.retry_allowed("w1", "t1") is False
    b2 = BudgetTracker()
    b2.record(text_tokens=200_001)
    assert b2.over_test_cap() is True


def test_d3_mechanical_trigger():
    assert should_force_option_b(0.11, 50, 0).escalate is True
    assert should_force_option_b(0.05, 50, 0).escalate is False
    assert should_force_option_b(0.0, 10, 2).escalate is True  # consecutive false-pass
    assert should_force_option_b(0.11, 49, 0).escalate is False  # need 50 tests


def test_d9_separate_from_d3():
    assert should_review_golden_library(0.16, 0.0).escalate is True
    assert should_review_golden_library(0.0, 0.31).escalate is True
    assert should_review_golden_library(0.05, 0.10).escalate is False


def test_manager_never_sees_pixels_and_rollback_gating():
    m = ManagerAgent()
    t = m.delegate_task("vm1", {"id": "t1", "type": "boot", "params": {},
                                "success_criteria": "booted"})
    assert m.get_vm_status("vm1").task == "t1"
    assert m.rollback_vm("vm1", "flaky")["gated"] is False
    assert m.rollback_vm("vm1", "flaky")["gated"] is False
    gated = m.rollback_vm("vm1", "flaky")  # 3rd needs human
    assert gated["gated"] is True
    assert m.escalate_to_human("Windows key entry required")["escalated"] is True


def test_worker_strict_handoff():
    w = WorkerAgent("vm1", "ubuntu")
    rep = WorkerReport("vm1", "t1", "pass", gates_passed=[1, 2])
    out = w.run_task({"id": "t1", "type": "boot", "params": {},
                      "success_criteria": "x"}, driver=lambda task: rep)
    assert out.status == "pass"
    with pytest.raises(ValueError):
        w.validate_handoff("not-a-dict")
    with pytest.raises(ValueError):
        w.validate_handoff({"nope": 1})
