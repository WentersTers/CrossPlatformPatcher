"""Stage-0 dry-run rehearsal: full synthetic loop producing a REAL runs/ tree.

Not a test — a rehearsal of artifact naming, retention, verdict flags, and
trace stitching for human review. Every artifact is labeled synthetic; when
the Linux host lands, the first real run has this shape to compare against.

Usage: python harness/dry_run.py [--root harness/runs]
"""
from __future__ import annotations

import argparse
import asyncio
import json
import sys
from datetime import datetime, timedelta, timezone
from pathlib import Path

import cv2
import numpy as np

HARNESS_ROOT = Path(__file__).resolve().parent

sys.path.insert(0, str(HARNESS_ROOT.parent))

from harness.artifacts.layout import (build_run_dir, retention_days,  # noqa: E402
                                      write_verdict)
from harness.gates.gate1_deterministic import evaluate as g1  # noqa: E402
from harness.gates.gate2_ssim_ocr import evaluate as g2  # noqa: E402
from harness.gates.gate2_5_state.sequence import verify_state_sequence  # noqa: E402
from harness.gates.gate2_5_state.template_matcher import StateMatch  # noqa: E402
from harness.manager.manager_agent import ManagerAgent  # noqa: E402
from harness.persistence.checkpointer import Checkpointer  # noqa: E402
from harness.persistence.traces import stitch_trace  # noqa: E402
from harness.telemetry.emitter import SyntheticEmitter  # noqa: E402
from harness.telemetry.reconcile import reconcile  # noqa: E402
from harness.telemetry.stream import TelemetryStream  # noqa: E402
from harness.tools.state import write_state  # noqa: E402
from harness.workers.worker_agent import WorkerAgent  # noqa: E402

STATES = ["idle", "listening", "thinking", "speaking", "idle"]
TRACE_ID = "dryrun-wake-word"


def sprite(state: str, size: int = 60) -> np.ndarray:
    img = np.zeros((size, size, 3), dtype=np.uint8)
    c, col = size // 2, (255, 255, 255)
    if state == "speaking":
        cv2.rectangle(img, (15, 15), (size - 15, size - 15), col, 2)
    else:
        cv2.circle(img, (c, c), 15, col, 2)
        if state == "listening":
            cv2.line(img, (c + 15, c - 10), (c + 22, c - 18), col, 3)
        elif state == "thinking":
            cv2.line(img, (c, c - 15), (c, c - 24), col, 3)
    canvas = np.zeros((120, 120, 3), dtype=np.uint8)
    canvas[30:90, 30:90] = img
    return canvas


def run(root: str | Path, ts: str | None = None, verbose: bool = True,
        trace_id: str = TRACE_ID) -> Path:
    def log(msg: str):
        if verbose:
            print(msg)

    log("== DRY RUN (SYNTHETIC REHEARSAL — not a real run) ==")
    run_dir = build_run_dir(root, "ubuntu", "22.04", "test_wake_word_seq", ts=ts)
    cp = Checkpointer(run_dir / "checkpoints.db")
    mgr = ManagerAgent(checkpointer=cp)
    worker = WorkerAgent("vm-ubuntu", "ubuntu")

    # 1. delegate
    cp.save_checkpoint(trace_id, {"step": "start"})
    task = mgr.delegate_task("vm-ubuntu", {"id": trace_id, "type": "wake_word_seq",
                                           "params": {"device": "pulse"},
                                           "success_criteria": "seq pass"})
    log(f"delegated {task.id} (predicate recorded in trace)")

    # 2. intent lane (synthetic emitter) + stub tool claims
    stream = TelemetryStream()
    em = SyntheticEmitter()
    em.heartbeat()
    prev = STATES[0]
    for st in STATES[1:]:
        em.state_change(prev, st, trigger="wake_word" if st == "listening" else "",
                        settle_ms=350)
        prev = st
    em.speak_start("the browser is here")
    em.speak_end(2100)
    em.vosk_result("hey pie com open the browser", 0.83)
    for line in em.lines:
        stream.ingest_line(line)
    for aid, kind in (("click-1", "click"), ("type-1", "type")):
        cp.append_event_idempotent(trace_id, aid, {"actor": "worker", "action": kind})
    log(f"intent: claimed={stream.claimed_state()[0]} reliable={stream.reliable}")

    # 3. Tier C over scripted captures (fake clock: instant)
    script = list(STATES)
    it, clock = iter(script), [0.0]

    async def sleep(d):
        clock[0] += d

    expected = [{"state": s, "min_duration_s": 0.0} for s in STATES]
    res = asyncio.run(verify_state_sequence(
        expected, timeout_s=30.0, capture_fn=lambda: next(it, "idle"),
        classify_fn=lambda s: StateMatch(s, 0.95, "template"),
        sleep_fn=sleep, time_fn=lambda: clock[0]))
    assert res.passed, res.divergence_point
    cp.append_event_idempotent(trace_id, "gate-2.5",
                               {"actor": "gate", "action": "2.5-pass",
                                "frames": len(res.observed)})

    # 4. G1 + G2 (deterministic, authoritative)
    r1 = g1({"exit_code": 0,
             "env": {"PAICOM_MIGRATION_MODE": "full",
                     "PAICOM_RUNTIME_VERIFIED_64BIT": "1"},
             "libs": [],
             "ref_transcript": "hey pie com open the browser",
             "hyp_transcript": "hey pie com open the browser"})
    shot = sprite("speaking")
    r2 = g2(shot, shot, {"ssim_threshold": 0.90},
            ocr_func=lambda roi: "the browser is here",
            active_window_roi=(30, 30, 60, 60), expected_text="browser",
            step_name="speaking")
    assert r1.passed and r2.passed
    final = reconcile("idle", stream.claimed_state()[0], res.observed[-1].state,
                      stream_reliable=stream.reliable, heartbeat_alive=True)
    log(f"reconcile: {final.diagnosis} (vision authoritative)")

    # 5. step artifacts: real PNGs + sidecars a reviewer can open
    for i, st in enumerate(STATES, 1):
        png = run_dir / f"step-{i:02d}-{st}.png"
        cv2.imwrite(str(png), sprite(st))
        sidecar = {"step": i, "state": st, "synthetic": True,
                   "cmd": "assert_state_sequence", "exit_code": 0,
                   "log_tail": "[launcher] arch.selected_runtime=native",
                   "transcript": "hey pie com open the browser",
                   "expected_text": "browser" if st == "speaking" else "",
                   "gate_results": {"g1": r1.passed, "g2": r2.evidence["ssim"]},
                   "state_classification": {"state": st, "confidence": 0.95,
                                            "method": "template"}}
        (run_dir / f"step-{i:02d}-{st}.json").write_text(json.dumps(sidecar, indent=2))
    (run_dir / "seq-01-timeline.json").write_text(json.dumps({
        "synthetic": True,
        "expected": [e["state"] for e in expected],
        "claimed": [{"state": e["to"], "seq": e["seq"]} for e in stream.events
                    if e["event"] == "state_change"],
        "observed": [{"state": f.state, "t": round(f.t, 2), "method": f.method}
                     for f in res.observed]}, indent=2))

    # 6. state.json + verdict.json + checkpoint
    state = write_state(run_dir / "state.json",
                        {"vm": "ubuntu-22.04",
                         "app": {"running": True, "runtime": "native-linux-x64",
                                 "launched_at": datetime.now(timezone.utc).isoformat()},
                         "app_claimed_state": {"state": stream.claimed_state()[0],
                                               "seq": stream.claimed_state()[1]},
                         "last_task": {"id": trace_id, "verdict": "pass",
                                       "gates_passed": [1, 2, "2.5-A"]}})
    verdict = write_verdict(run_dir, "pass", {"1": True, "2": True, "2.5": True},
                            state_method="template", runtime="native-linux-x64",
                            evidence_refs=["synthetic-rehearsal"],
                            synthetic=True,
                            diagnosis="synthetic-rehearsal",
                            evidence=dict(r1.evidence),
                            failures=[])
    cp.append_event_idempotent(trace_id, "verdict",
                               {"actor": "manager", "action": "verdict",
                                "verdict": "pass"})
    cp.save_checkpoint(trace_id, {"step": "done", "verdict": "pass"})

    # 7. reviewer summary
    trace = stitch_trace(cp, trace_id)
    log(f"run dir: {run_dir} (state v{state['version']})")
    log(f"verdict: {verdict['verdict']} "
        f"nondeterministic={verdict['state_verification_non_deterministic']}")
    log(f"retention: passing={retention_days(True)}d "
        f"failing={'indefinite' if retention_days(False) is None else ''}")
    log(f"trace events ({len(trace)}): {[e.get('action') for e in trace]}")
    log("review: open step-*.png, verdict.json, seq-01-timeline.json")
    return run_dir


def main(argv: list[str] | None = None) -> int:
    ap = argparse.ArgumentParser(description="Stage-0 dry-run rehearsal (synthetic)")
    ap.add_argument("--root", default=str(HARNESS_ROOT / "runs"))
    ap.add_argument("--ts", default=None)
    ap.add_argument("--cycles", type=int, default=1,
                    help="consecutive scripted cycles (stage-0 evidence "
                         "is 10 against a real VM)")
    ap.add_argument("--ts-base", default=None,
                    help="base timestamp for cycle dirs (adds i seconds per cycle)")
    args = ap.parse_args(argv)
    base = None
    if args.ts_base is not None:
        base = datetime.strptime(args.ts_base, "%Y%m%dT%H%M%SZ").replace(
            tzinfo=timezone.utc)
    passed = 0
    for i in range(max(1, args.cycles)):
        if base is not None:
            ts = (base + timedelta(seconds=i)).strftime("%Y%m%dT%H%M%SZ")
        else:
            ts = args.ts
        run(args.root, ts=ts, trace_id=f"{TRACE_ID}-{i:02d}" if args.cycles > 1
            else TRACE_ID)
        passed += 1
    print(f"cycles passed: {passed}/{max(1, args.cycles)}")
    return 0 if passed == max(1, args.cycles) else 1


if __name__ == "__main__":
    raise SystemExit(main())
