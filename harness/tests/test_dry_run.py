"""Rehearsal shape contract: what a human reviews on day one of real runs."""
import json

from harness.dry_run import TRACE_ID, run
from harness.persistence.checkpointer import Checkpointer
from harness.persistence.traces import stitch_trace


def test_rehearsal_produces_reviewable_shape(tmp_path):
    run_dir = run(tmp_path / "runs", ts="20260908T000000Z", verbose=False)

    verdict = json.loads((run_dir / "verdict.json").read_text())
    assert verdict["verdict"] == "pass"
    assert verdict["state_verification_non_deterministic"] is False
    assert verdict["runtime"] == "native-linux-x64"
    assert "synthetic-rehearsal" in verdict["evidence_refs"]
    # unified schema (Session-7 convergence, real-verdict authoritative):
    # full key set present, rehearsal lanes marked synthetic.
    assert set(verdict) == {"verdict", "gates", "runtime", "synthetic",
                            "diagnosis", "evidence", "failures",
                            "state_method", "state_verification_non_deterministic",
                            "evidence_refs"}
    assert verdict["synthetic"] is True
    assert verdict["diagnosis"] == "synthetic-rehearsal"
    assert verdict["failures"] == []
    assert verdict["evidence"]["exit_code"] == 0
    assert verdict["state_method"] == "template"

    # step PNGs a reviewer can open + sidecars with gate evidence
    for i, st in enumerate(["idle", "listening", "thinking", "speaking", "idle"], 1):
        assert (run_dir / f"step-{i:02d}-{st}.png").stat().st_size > 0
        sc = json.loads((run_dir / f"step-{i:02d}-{st}.json").read_text())
        assert sc["gate_results"]["g1"] is True
        assert sc["state_classification"]["method"] == "template"
        assert sc["synthetic"] is True

    # three-lane timeline: expected / claimed / observed
    tl = json.loads((run_dir / "seq-01-timeline.json").read_text())
    assert tl["expected"] == ["idle", "listening", "thinking", "speaking", "idle"]
    assert [c["state"] for c in tl["claimed"]] == ["listening", "thinking",
                                                  "speaking", "idle"]
    assert tl["observed"][0]["state"] == "idle"
    assert all("method" in f for f in tl["observed"])

    # state.json carries the intent lane; trace stitches end to end
    state = json.loads((run_dir / "state.json").read_text())
    assert state["app_claimed_state"]["state"] == "idle"
    trace = stitch_trace(Checkpointer(run_dir / "checkpoints.db"), TRACE_ID)
    actions = [e.get("action") for e in trace]
    assert "delegate" in actions and "verdict" in actions
