"""Fix-loop contract: pass flip, anti-loop tripwire, budget, Mac policy/gate."""
import json
from pathlib import Path

from harness.artifacts.layout import write_verdict
from harness.live.fixloop import (FixError, FixLoopConfig, check_tripwire,
                                  mac_baseline_diff, mac_policy_check,
                                  run_loop)

C_MONO = "runtime-startup/wine-mono-missing (family c)"


def _vdir(root: Path, name: str, verdict: str, diagnosis) -> Path:
    d = root / name
    d.mkdir(parents=True, exist_ok=True)
    write_verdict(d, verdict, {"1": verdict == "pass"}, runtime="system-wine",
                  synthetic=False, diagnosis=diagnosis,
                  evidence={"exit_code": 0 if verdict == "pass" else 255},
                  failures=[] if verdict == "pass" else ["exit_code=255 != 0"])
    return d


def _loop_dirs(pre: Path):
    """diagnose_fn: iteration 0 reads the seeded baseline, later iterations
    read the previous iteration's post-fix dir (the driver's wiring)."""
    state = {"post": None}

    def diagnose(i, ledger):
        if i == 0 or state["post"] is None:
            return pre
        return state["post"]

    def cycle(fix, i):
        return state["post"]

    def set_post(d):
        state["post"] = d
    return diagnose, cycle, set_post


def test_pass_flip_stops(tmp_path):
    pre = _vdir(tmp_path, "base", "fail", C_MONO)
    post = _vdir(tmp_path, "fixed", "pass", None)
    diagnose, cycle, set_post = _loop_dirs(pre)
    set_post(post)
    fixes = []
    out = run_loop(tmp_path / "loop", FixLoopConfig(max_iterations=5),
                   diagnose,
                   fix_fn=lambda v, i: (fixes.append(i), {"fix_id": "dotnet48", "kind": "runtime"})[1],
                   cycle_fn=lambda f, i: cycle(f, i))
    assert out.stop == "pass" and out.iterations == 2
    assert fixes == [0]
    assert json.loads((tmp_path / "loop" / "fixloop-ledger.json").read_text())[0]["diagnosis"] == C_MONO


def test_same_diagnosis_twice_trips(tmp_path):
    pre = _vdir(tmp_path, "base", "fail", C_MONO)
    again = _vdir(tmp_path, "again", "fail", C_MONO)
    diagnose, cycle, set_post = _loop_dirs(pre)
    set_post(again)
    cycles = []
    out = run_loop(tmp_path / "loop", FixLoopConfig(max_iterations=5),
                   diagnose,
                   fix_fn=lambda v, i: {"fix_id": f"try-{i}", "kind": "runtime"},
                   cycle_fn=lambda f, i: (cycles.append(f["fix_id"]), cycle(f, i))[1])
    assert out.stop == "tripwire" and out.iterations == 2
    assert cycles == ["try-0"]  # second fix never attempted
    assert "escalate" in out.ledger[-1]["note"]


def test_alternating_diagnoses_run_to_budget(tmp_path):
    a = _vdir(tmp_path, "a", "fail", "family (c)")
    b = _vdir(tmp_path, "b", "fail", "family (a)")

    def diagnose(i, ledger):
        return a if i % 2 == 0 else b

    out = run_loop(tmp_path / "loop", FixLoopConfig(max_iterations=4),
                   diagnose,
                   fix_fn=lambda v, i: {"fix_id": f"f{i}", "kind": "runtime"},
                   cycle_fn=lambda f, i: a)
    assert out.stop == "budget" and out.iterations == 4


def test_migration_fix_blocked_without_conditional(tmp_path):
    pre = _vdir(tmp_path, "base", "fail", C_MONO)
    ran = []
    out = run_loop(tmp_path / "loop", FixLoopConfig(),
                   lambda i, l: pre,
                   fix_fn=lambda v, i: {"fix_id": "naudio3", "kind": "migration"},
                   cycle_fn=lambda f, i: ran.append(f) or pre)
    assert out.stop == "mac-policy" and ran == []
    ok, _ = mac_policy_check({"kind": "migration", "platform_conditional": True})
    assert ok is True
    ok2, _ = mac_policy_check({"kind": "weird"})
    assert ok2 is False


def test_mac_gate_fail_stops_and_no_baseline_skips(tmp_path):
    base = tmp_path / "baseline"
    base.mkdir()
    (base / "launch.command").write_text("mac-baseline")
    pre = _vdir(tmp_path, "run", "fail", C_MONO)
    (tmp_path / "run" / "launch.command").write_text("mac-changed")

    arts = lambda d: {"launch.command": str(Path(d) / "launch.command")}
    out = run_loop(tmp_path / "loop", FixLoopConfig(),
                   lambda i, l: pre,
                   fix_fn=lambda v, i: {"fix_id": "f", "kind": "runtime"},
                   cycle_fn=lambda f, i: pre,
                   mac_artifacts_fn=arts, baseline_dir=base)
    assert out.stop == "mac-gate"

    g = mac_baseline_diff(tmp_path / "missing", arts(pre))
    assert g["state"] == "no-baseline"
    out2 = run_loop(tmp_path / "loop2", FixLoopConfig(max_iterations=1),
                    lambda i, l: pre,
                    fix_fn=lambda v, i: {"fix_id": "f", "kind": "runtime"},
                    cycle_fn=lambda f, i: pre,
                    mac_artifacts_fn=arts, baseline_dir=tmp_path / "missing")
    assert out2.stop == "budget"  # SKIP is not a stop


def test_fix_failed_is_recorded_stop_not_crash(tmp_path):
    pre = _vdir(tmp_path, "base", "fail", C_MONO)

    def bad_fix(verdict, i):
        raise FixError("effect-verify: fatal persists after fix")

    out = run_loop(tmp_path / "loop", FixLoopConfig(),
                   lambda i, l: pre, bad_fix,
                   cycle_fn=lambda f, i: (_ for _ in ()).throw(
                       AssertionError("cycle must not run after failed fix")))
    assert out.stop == "fix-failed" and out.iterations == 1
    saved = json.loads((tmp_path / "loop" / "fixloop-ledger.json").read_text())
    assert saved[0]["stop"] == "fix-failed" and "FixError" in saved[0]["error"]


def test_diagnose_error_is_typed_stop(tmp_path):
    def boom(i, ledger):
        raise OSError("verdict unreadable")

    out = run_loop(tmp_path / "loop", FixLoopConfig(), boom,
                   fix_fn=lambda v, i: {}, cycle_fn=lambda f, i: "")
    assert out.stop == "error" and "OSError" in out.ledger[0]["error"]


def test_tripwire_unit():
    assert check_tripwire([]) is False
    assert check_tripwire(["a"]) is False
    assert check_tripwire(["a", "a"]) is True
    assert check_tripwire(["a", "b"]) is False
    assert check_tripwire([None, None]) is False
    assert check_tripwire(["a", "a", "b"], limit=3) is False
