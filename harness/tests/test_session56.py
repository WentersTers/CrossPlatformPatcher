"""Storm probe verdicts, Tier-0 launch paths, Gate-1 log patterns."""
import pytest

from harness.gates.gate1_deterministic import evaluate as g1
from harness.live.launch import launch
from harness.live.storm_probe import ProbeFrame, assess, main as storm_main


def _frames(states):
    return [ProbeFrame(s, 0.3 if s == "ambiguous" else 0.9, f"h{i % 2}")
            for i, s in enumerate(states)]


def test_assess_current_vs_deferred():
    # sustained ambiguous on frozen fb, silent logs: current storms with the
    # vision-quality mislabel; the deferred row names wrong-ROI-or-no-pet.
    r = assess(_frames(["ambiguous"] * 8), log_delta=0)
    assert r["max_consecutive_ambiguous"] == 8
    assert r["fb_frozen"] is True  # 2-hash alternation sits in the window
    assert (r["current_verdict"], r["current_label"]) == ("storm", "vision-quality")
    assert r["deferred_diagnosis"] == "wrong-ROI-or-no-pet"
    assert r["deferred_suppresses_storm"] is True
    # concrete states break the run; changing hashes unfreeze the fb
    r2 = assess([ProbeFrame("idle", 0.9, f"h{i}") for i in range(8)])
    assert r2["max_consecutive_ambiguous"] == 0
    assert r2["current_verdict"] == "none" and r2["deferred_diagnosis"] == ""
    # log activity disqualifies the deferred row (something IS happening)
    r3 = assess(_frames(["ambiguous"] * 8), log_delta=40)
    assert r3["deferred_suppresses_storm"] is False


def test_storm_main_cli_shape(tmp_path):
    import numpy as np
    from PIL import Image
    sleeps = []

    def classify(img, roi):
        return ("ambiguous", 0.3)

    def capture(i, path):
        Image.fromarray(np.full((16, 16, 3), 128, dtype=np.uint8)).save(path)

    rc = storm_main(["--n", "6", "--interval", "0.1", "--out", str(tmp_path)],
                    classify_fn=classify, capture_fn=capture,
                    sleep_fn=lambda s: sleeps.append(s))
    assert rc == 0
    import json as j
    rep = j.loads((tmp_path / "storm-report.json").read_text())
    assert rep["frames"] == 6 and rep["max_consecutive_ambiguous"] == 6
    assert rep["current_verdict"] == "storm"
    assert sleeps == [0.1] * 5


class FakeProc:
    def __init__(self, out="(out)", err="", code=0, lives=1):
        self._out, self._err, self._code = out, err, code
        self._lives = lives
        self.killed = False

    def poll(self):
        if self._lives > 0:
            self._lives -= 1
            return None
        return self._code

    def communicate(self):
        return self._out, self._err

    def kill(self):
        self.killed = True
        self._lives = 0

    @property
    def returncode(self):
        return self._code


def test_launch_exit_path(tmp_path):
    clock = [0.0]
    r = launch(["app"], tmp_path / "run.log", timeout_s=60.0, poll_s=5.0,
               popen_factory=lambda c: FakeProc("(line1\nline2)", code=3),
               sleep_fn=lambda s: clock.__setitem__(0, clock[0] + s),
               time_fn=lambda: clock[0])
    assert r == {"exit_code": 3, "timed_out": False,
                 "log_path": str(tmp_path / "run.log"),
                 "alive_samples": 1, "duration_s": 5.0}
    text = (tmp_path / "run.log").read_text()
    assert "tier0" in text and "exit=3" in text and "line1" in text


def test_launch_timeout_kills(tmp_path):
    clock = [0.0]
    procs = []

    def factory(cmd):
        p = FakeProc("partial", code=0, lives=999)
        procs.append(p)
        return p

    r = launch(["game"], tmp_path / "run.log", timeout_s=12.0, poll_s=5.0,
               popen_factory=factory,
               sleep_fn=lambda s: clock.__setitem__(0, clock[0] + s),
               time_fn=lambda: clock[0])
    assert r["exit_code"] is None and r["timed_out"] is True
    assert procs[0].killed is True and r["alive_samples"] == 3


def test_gate1_log_patterns():
    ok = g1({"exit_code": 1, "env": {}, "libs": [],
             "log_text": "speech: DllNotFoundException libvosk boom",
             "log_must_match": [r"DllNotFound"],
             "log_must_not_match": [r"ALL GREEN"]})
    assert not ok.passed  # exit/env fail; but the must_match held (no log failure for it)
    assert not any("required pattern" in f for f in ok.failures)
    bad = g1({"exit_code": 0,
              "env": {"PAICOM_MIGRATION_MODE": "full",
                      "PAICOM_RUNTIME_VERIFIED_64BIT": "1"},
              "libs": [], "log_text": "clean boot",
              "log_must_match": [r"DllNotFound"]})
    assert not bad.passed and any("required pattern" in f for f in bad.failures)
    dirty = g1({"exit_code": 0,
                "env": {"PAICOM_MIGRATION_MODE": "full",
                        "PAICOM_RUNTIME_VERIFIED_64BIT": "1"},
                "libs": [], "log_text": "ALL GREEN",
                "log_must_not_match": [r"ALL GREEN"]})
    assert not dirty.passed and any("forbidden pattern" in f for f in dirty.failures)


def _guest_factory(script, kills=None):
    """Scripted QGA backend: script = list of status dicts consumed per poll."""
    from harness.live.guest_run import GuestRunFactory
    calls = {"exec": [], "kill": []}
    states = list(script)

    def exec_fn(argv):
        calls["exec"].append(argv)
        return 4242

    def status_fn(pid):
        assert pid == 4242
        if states:
            return states.pop(0)
        return {"exited": True, "exitcode": 0, "out": "", "err": ""}

    def kill_fn(pid):
        calls["kill"].append(pid)

    if kills is None:
        return GuestRunFactory(exec_fn, status_fn), calls
    return GuestRunFactory(exec_fn, status_fn, kill_fn), calls


def test_guest_run_crash_path(tmp_path):
    from harness.live.launch import launch
    factory, calls = _guest_factory([
        {"exited": False, "exitcode": None, "out": "", "err": ""},
        {"exited": True, "exitcode": 1,
         "out": "speech: DllNotFoundException libvosk\n", "err": ""},
    ])
    clock = [0.0]
    r = launch(["timeout", "300", "bash", "run.sh"], tmp_path / "t0.log",
               timeout_s=300.0, poll_s=5.0, popen_factory=factory,
               sleep_fn=lambda s: clock.__setitem__(0, clock[0] + s),
               time_fn=lambda: clock[0])
    assert r["exit_code"] == 1 and not r["timed_out"]
    assert calls["exec"] == [["timeout", "300", "bash", "run.sh"]]
    text = (tmp_path / "t0.log").read_text()
    assert "DllNotFoundException" in text and "exit=1" in text


def test_guest_run_timeout_path(tmp_path):
    from harness.live.launch import launch
    running = {"exited": False, "exitcode": None, "out": "", "err": ""}
    factory, calls = _guest_factory([running] * 10, kills=[])
    clock = [0.0]
    r = launch(["timeout", "300", "bash", "run.sh"], tmp_path / "t0.log",
               timeout_s=12.0, poll_s=5.0, popen_factory=factory,
               sleep_fn=lambda s: clock.__setitem__(0, clock[0] + s),
               time_fn=lambda: clock[0])
    assert r["exit_code"] is None and r["timed_out"] is True
    assert calls["kill"] == [4242]  # best-effort kill attempted
