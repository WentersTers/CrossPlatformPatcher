"""Session-7 ten-cycle loop: deterministic (c) payload, kill-resume, hang shade."""
import json
from pathlib import Path

import numpy as np
import pytest

from harness.gates.gate2_5_state.template_matcher import StateMatch
from harness.live.cycles import FAMILY_C_HANG, FAMILY_C_MONO, run_session
from harness.measurements import Samples
from harness.vms.pool import LibvirtConnectionError, VmPool

ENV = {"PAICOM_MIGRATION_MODE": "full", "PAICOM_RUNTIME_VERIFIED_64BIT": "1"}
CMD = ["timeout", "300", "bash", "run.sh"]
MONO_OUT = ("wine: configuration updated\n"
            "00e4:err:mscoree:CLRRuntimeInfo_GetRuntimeHost "
            "Wine Mono is not installed\n")


class FakeProc:
    def __init__(self, out, code, lives=1):
        self._out, self._code, self._lives = out, code, lives
        self.killed = False

    def poll(self):
        if self._lives > 0:
            self._lives -= 1
            return None
        return self._code

    def communicate(self):
        return self._out, ""

    def kill(self):
        self.killed = True
        self._lives = 0

    @property
    def returncode(self):
        return self._code


def _pool():
    pool = VmPool()
    pool.snapshot("vm-ubuntu", "base-v4-app")
    return pool


def _clock():
    clock = [0.0]
    return clock, (lambda s: clock.__setitem__(0, clock[0] + s)), (lambda: clock[0])


def _capture():
    return np.zeros((64, 64, 3), dtype=np.uint8)


def _classify(img):
    assert img.shape == (64, 64, 3)
    return StateMatch("pet_absent", 0.003, "template-edge-floor")


def test_ten_cycles_with_mid_run_kill_and_resume(tmp_path):
    pool, calls = _pool(), []
    clock, sleep, now = _clock()
    samples = Samples()
    kw = dict(pool=pool, vm_id="vm-ubuntu", snapshot="base-v4-app", cmd=CMD,
              observed_env=ENV, timeout_s=300.0, poll_s=5.0,
              popen_factory=lambda c: (calls.append(c), FakeProc(MONO_OUT, 255))[1],
              capture_fn=_capture, classify_fn=_classify,
              sleep_fn=sleep, time_fn=now,
              db_path=tmp_path / "s7.db", trace_id="session-7-test",
              samples=samples, ts_base="20260908T080000Z")
    with pytest.raises(RuntimeError, match="simulated kill"):
        run_session(tmp_path / "runs", n=10, kill_after_cycle=4, **kw)
    assert len(calls) == 5  # killed mid-run after cycle 04 checkpoints

    out = run_session(tmp_path / "runs", n=10, **kw)  # resume
    assert out["cycles_completed"] == 10
    assert len(calls) == 10  # resumed cycles never re-launched
    assert len(out["run_dirs"]) == 10
    assert out["samples"]["cycle.duration_s"]["n"] == 10

    for i, d in enumerate(out["run_dirs"]):
        v = out["verdicts"][i]
        assert v["verdict"] == "fail" and v["gates"] == {"1": False}
        assert v["diagnosis"] == FAMILY_C_MONO
        assert v["runtime"] == "system-wine" and v["synthetic"] is False
        assert v["failures"] == ["exit_code=255 != 0"]
        assert v["state_method"] == "template-edge-floor"
        assert "tier0.log" in v["evidence_refs"]
        assert v["evidence"]["revert_n"] == i + 1
        assert v["evidence"]["timed_out"] is False
        tier0 = (Path(d) / "tier0.log").read_text()
        assert "[tier0]" in tier0 and "Wine Mono is not installed" in tier0
        assert json.loads((Path(d) / "state.json")
                          .read_text())["runtime_selected"] == "system-wine"
        assert (Path(d) / "step-01-launch.png").stat().st_size > 0
    summary = json.loads((tmp_path / "runs" / "session7-summary.json").read_text())
    assert summary["cycles_completed"] == 10
    assert summary["diagnoses"] == [FAMILY_C_MONO]


def test_logs_fn_pulls_join_evidence_refs(tmp_path):
    from harness.live.cycles import run_cycle
    pool = _pool()
    clock, sleep, now = _clock()

    def logs_fn(run_dir):
        (run_dir / "launcher.log").write_text("launcher-line\n")
        return ["launcher.log"]

    res = run_cycle(tmp_path / "runs", 0, "20260908T080000Z", pool=pool,
                    vm_id="vm-ubuntu", snapshot="base-v4-app", cmd=CMD,
                    observed_env=ENV,
                    popen_factory=lambda c: FakeProc(MONO_OUT, 255),
                    capture_fn=_capture, classify_fn=_classify,
                    logs_fn=logs_fn, sleep_fn=sleep, time_fn=now)
    assert "launcher.log" in res["verdict"]["evidence_refs"]
    assert (tmp_path / "runs" / "20260908T080000Z" / "ubuntu-22.04" /
            "session7-cycle-00" / "launcher.log").read_text() == "launcher-line\n"


def test_clock_anchor_recorded_and_failure_noted(tmp_path):
    import time as _t

    from harness.live.cycles import run_cycle
    pool = _pool()
    clock, sleep, now = _clock()
    res = run_cycle(tmp_path / "runs", 0, "20260908T080000Z", pool=pool,
                    vm_id="vm-ubuntu", snapshot="base-v4-app", cmd=CMD,
                    observed_env=ENV,
                    popen_factory=lambda c: FakeProc(MONO_OUT, 255),
                    clock_fn=lambda: 1000.0,
                    sleep_fn=sleep, time_fn=now)
    ev = res["verdict"]["evidence"]
    assert ev["guest_epoch_s"] == 1000.0
    assert abs(ev["clock_offset_s"] - (1000.0 - _t.time())) < 120

    def bad():
        raise OSError("no clock")

    res2 = run_cycle(tmp_path / "runs", 1, "20260908T080001Z", pool=pool,
                     vm_id="vm-ubuntu", snapshot="base-v4-app", cmd=CMD,
                     observed_env=ENV,
                     popen_factory=lambda c: FakeProc(MONO_OUT, 255),
                     clock_fn=bad, sleep_fn=sleep, time_fn=now)
    assert res2["verdict"]["evidence"]["clock_offset_s"] is None
    import json as j
    sc = j.loads((tmp_path / "runs" / "20260908T080001Z" / "ubuntu-22.04" /
                  "session7-cycle-01" / "step-01-launch.json").read_text())
    assert "no clock" in sc["clock_note"]


def test_classify_family_fatal_outranks_caught_dll():
    from harness.live.cycles import (FAMILY_A_DL, FAMILY_BITNESS,
                                     FAMILY_C_EXIT, FAMILY_C_FATAL,
                                     FAMILY_C_MONO, FAMILY_E_FONTS,
                                     classify_family)
    assert classify_family("x Wine Mono is not installed", 255, False) == FAMILY_C_MONO
    # 6b shape: caught onnx DllNotFound (app continues) + fatal fonts.
    # The fatal cause wins; the caught string alone is generic exit-nonzero.
    both = ("[oww] OpenWakeWord initialization failed: System.DllNotFoundException: onnxruntime\n"
            "FATAL UNHANDLED EXCEPTION: System.ArgumentException: FontFamilyNotFound [GDI+]")
    assert classify_family(both, 1, False) == FAMILY_E_FONTS
    assert classify_family("only System.DllNotFoundException: onnxruntime (caught)", 1, False) == FAMILY_C_EXIT
    assert classify_family("FATAL UNHANDLED EXCEPTION: System.DllNotFoundException: foo", 1, False) == FAMILY_A_DL
    assert classify_family("FATAL UNHANDLED EXCEPTION: something novel", 3, False) == FAMILY_C_FATAL
    assert classify_family("System.BadImageFormatException: 0x8007000B", 1, False) == FAMILY_BITNESS
    assert classify_family("clean boot", 0, False) is None


def test_hang_is_timeout_kill_verdict_not_cycle_failure(tmp_path):
    pool, calls = _pool(), []
    clock, sleep, now = _clock()
    out = run_session(
        tmp_path / "runs", n=1, pool=pool, vm_id="vm-ubuntu",
        snapshot="base-v4-app", cmd=CMD, observed_env=ENV,
        timeout_s=12.0, poll_s=5.0,
        popen_factory=lambda c: (calls.append(c), FakeProc("partial", 0, 999))[1],
        capture_fn=_capture, classify_fn=_classify,
        sleep_fn=sleep, time_fn=now,
        db_path=tmp_path / "s7.db", trace_id="hang-1",
        ts_base="20260908T090000Z")
    v = out["verdicts"][0]
    assert v["verdict"] == "fail" and v["diagnosis"] == FAMILY_C_HANG
    assert v["evidence"]["timed_out"] is True
    assert v["evidence"]["exit_code"] is None


def test_revert_refuses_unknown_snapshot():
    pool = VmPool()
    with pytest.raises(LibvirtConnectionError, match="unknown snapshot"):
        pool.revert("vm-ubuntu", "base-v4-app")
    pool.snapshot("vm-ubuntu", "base-v4-app")
    assert pool.revert("vm-ubuntu", "base-v4-app")["revert_n"] == 1
    assert pool.revert("vm-ubuntu", "base-v4-app")["revert_n"] == 2
