"""D5: checkpointed graph from day one; trace_id on every event; resume."""
import json

import pytest

from harness.persistence.checkpointer import Checkpointer
from harness.persistence.traces import stitch_trace
from harness.tools.state import (StaleVersionError, load_state,
                                 new_initial_state, write_state)


def test_checkpoint_save_load_resume(tmp_path):
    cp = Checkpointer(tmp_path / "harness.db")
    assert cp.load_checkpoint("nope") is None
    cp.save_checkpoint("t1", {"step": 1, "vm": "ubuntu-22.04"})
    # kill-and-resume: new instance on same db file
    cp2 = Checkpointer(tmp_path / "harness.db")
    assert cp2.load_checkpoint("t1") == {"step": 1, "vm": "ubuntu-22.04"}
    cp2.save_checkpoint("t1", {"step": 2})
    assert cp.load_checkpoint("t1") == {"step": 2}


def test_events_carry_trace_id_and_stitch(tmp_path):
    cp = Checkpointer(tmp_path / "harness.db")
    cp.save_checkpoint("trace-1", {})
    cp.append_event("trace-1", {"actor": "manager", "action": "delegate"})
    cp.append_event("trace-1", {"actor": "worker", "action": "click"})
    cp.append_event("trace-1", {"actor": "gate", "action": "g1-pass"})
    events = stitch_trace(cp, "trace-1")
    assert [e["action"] for e in events] == ["delegate", "click", "g1-pass"]
    assert all(e["trace_id"] == "trace-1" for e in events)
    with pytest.raises(ValueError):
        cp.append_event("", {"action": "x"})
    with pytest.raises(ValueError):
        cp.append_event("trace-1", {"trace_id": "other", "action": "x"})


def test_state_versioned_lww(tmp_path):
    p = tmp_path / "state.json"
    s0 = new_initial_state("ubuntu-22.04")
    write_state(p, s0)
    assert load_state(p)["version"] == 1
    s1 = write_state(p, {"app": {"running": True}}, expected_version=1)
    assert s1["version"] == 2
    # concurrent writer ahead: last-writer-wins-on-version, no exception
    s2 = write_state(p, {"pet": {"last_observed_state": "idle"}}, expected_version=1)
    assert s2["version"] == 3
    assert s2["app"] == {"running": True}
    # version is managed: caller-supplied version in patch is ignored, not honored
    s3 = write_state(tmp_path / "fresh.json", {"version": 99, "vm": "x"})
    assert s3["version"] == 1
    s4 = write_state(p, {"version": 99, "vm": "y"}, expected_version=3)
    assert s4["version"] == 4 and s4["vm"] == "y"
    # reject mode: stale write raises and writes nothing
    with pytest.raises(StaleVersionError):
        write_state(p, {"vm": "stale"}, expected_version=1, on_conflict="reject")
    assert load_state(p)["version"] == 4
    with pytest.raises(ValueError):
        write_state(p, {"vm": "z"}, on_conflict="bogus")
