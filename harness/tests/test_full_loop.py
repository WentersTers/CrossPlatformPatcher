"""Full simulated loop with every stub in place (last cheap test before reality).

manager delegates -> worker executes stub tools (idempotent claims) ->
G1/G2/G2.5 verdict -> artifacts written -> KILL mid-loop -> resume from
checkpointer -> driver skips completed action_ids (no double QMP click) ->
verdict + stitched trace complete.
"""
import asyncio

import numpy as np

from harness.artifacts.layout import build_run_dir, write_verdict
from harness.gates.gate1_deterministic import evaluate as g1
from harness.gates.gate2_ssim_ocr import evaluate as g2
from harness.gates.gate2_5_state.sequence import verify_state_sequence
from harness.gates.gate2_5_state.template_matcher import StateMatch
from harness.manager.manager_agent import ManagerAgent
from harness.persistence.checkpointer import Checkpointer
from harness.workers.worker_agent import WorkerAgent, WorkerReport


def _img(v=128):
    return np.full((32, 32), v, dtype=np.uint8)


def _run_loop(cp, qmp_log, run_root, fail_after=None):
    """One loop pass. fail_after=N simulates kill after N tool calls."""
    trace_id = "loop-1"
    mgr = ManagerAgent(checkpointer=cp)
    worker = WorkerAgent("vm-ubuntu", "ubuntu")
    if cp.load_checkpoint(trace_id) is None:
        cp.save_checkpoint(trace_id, {"step": "start"})
        task = mgr.delegate_task("vm-ubuntu", {"id": trace_id, "type": "wake_word_seq",
                                               "params": {"device": "pulse"},
                                               "success_criteria": "seq pass"})
        _ = task
    done = cp.completed_action_ids(trace_id)

    actions = ["click-1", "type-1", "assert-seq-1"]
    script = ["idle", "idle", "listening", "thinking", "speaking", "idle", "idle"]
    for aid in actions:
        if aid in done:
            continue  # resume-after: never re-execute the in-flight tool call
        if aid.startswith("click"):
            seq, replayed = cp.append_event_idempotent(
                trace_id, aid, {"actor": "worker", "action": "click", "x": 100, "y": 200})
            assert replayed is False
            qmp_log.append(aid)  # real QMP would fire here — exactly once per aid
        elif aid.startswith("type"):
            seq, replayed = cp.append_event_idempotent(
                trace_id, aid, {"actor": "worker", "action": "type", "text": "hello"})
            assert replayed is False
            qmp_log.append(aid)
        else:
            it = iter(script)
            clock = [0.0]

            async def sleep(d):
                clock[0] += d

            res = asyncio.run(verify_state_sequence(
                [{"state": "idle", "min_duration_s": 0.0},
                 {"state": "listening", "min_duration_s": 0.0},
                 {"state": "thinking", "min_duration_s": 0.0},
                 {"state": "speaking", "min_duration_s": 0.0},
                 {"state": "idle", "min_duration_s": 0.0}],
                timeout_s=30.0, capture_fn=lambda: next(it, "idle"),
                classify_fn=lambda s: StateMatch(s, 0.95, "template"),
                sleep_fn=sleep, time_fn=lambda: clock[0]))
            assert res.passed
            cp.append_event_idempotent(trace_id, aid,
                                       {"actor": "gate", "action": "2.5-pass"})
            qmp_log.append(aid)
        done = cp.completed_action_ids(trace_id)
        cp.save_checkpoint(trace_id, {"step": aid})
        if fail_after is not None and len(qmp_log) >= fail_after:
            raise RuntimeError("simulated kill")

    # gates over the loop result (deterministic, authoritative)
    r1 = g1({"exit_code": 0,
             "env": {"PAICOM_MIGRATION_MODE": "full",
                     "PAICOM_RUNTIME_VERIFIED_64BIT": "1"},
             "libs": []})
    r2 = g2(_img(), _img(), {"ssim_threshold": 0.9})
    assert r1.passed and r2.passed
    d = build_run_dir(run_root, "ubuntu", "22.04", "loop-1", ts="20260101T000000Z")
    v = write_verdict(d, "pass", {"1": True, "2": True, "2.5": True},
                      state_method="template", runtime="native-linux-x64")
    cp.append_event_idempotent(trace_id, "verdict",
                               {"actor": "manager", "action": "verdict", "verdict": "pass"})
    cp.save_checkpoint(trace_id, {"step": "done", "verdict": "pass"})
    rep = WorkerReport("vm-ubuntu", trace_id, "pass", gates_passed=[1, 2, "2.5-A"])
    worker.validate_handoff({"task_id": rep.task_id, "status": rep.status})
    return v


def test_full_loop_with_kill_and_resume(tmp_path):
    import pytest
    db = tmp_path / "harness.db"
    qmp_log: list[str] = []
    cp1 = Checkpointer(db)
    with pytest.raises(RuntimeError, match="simulated kill"):
        _run_loop(cp1, qmp_log, tmp_path / "runs", fail_after=2)
    assert qmp_log == ["click-1", "type-1"]  # killed mid-loop
    del cp1  # process dies

    # resume: new instance, same db file
    cp2 = Checkpointer(db)
    assert cp2.load_checkpoint("loop-1")["step"] == "type-1"
    v = _run_loop(cp2, qmp_log, tmp_path / "runs")  # completes
    assert v["verdict"] == "pass"
    # idempotency: resumed run skipped completed actions — no double QMP
    assert qmp_log == ["click-1", "type-1", "assert-seq-1"]
    assert cp2.completed_action_ids("loop-1") == {"click-1", "type-1",
                                                 "assert-seq-1", "verdict"}
    # stitched trace queryable by trace_id across manager+worker+gates
    kinds = [e.get("action") for e in cp2.get_events("loop-1")]
    assert "delegate" in kinds and "verdict" in kinds


def test_idempotent_claim_never_duplicates(tmp_path):
    cp = Checkpointer(tmp_path / "h.db")
    s1, r1 = cp.append_event_idempotent("t", "click-1", {"actor": "w"})
    s2, r2 = cp.append_event_idempotent("t", "click-1", {"actor": "w"})
    assert s1 == s2 and r1 is False and r2 is True
    assert len(cp.get_events("t")) == 1
