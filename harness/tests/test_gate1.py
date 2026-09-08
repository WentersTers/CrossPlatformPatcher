"""Gate 1: exit/env/libs/transcript (injection-immune)."""
from harness.gates.gate1_deterministic import (NativeLibInfo, evaluate,
                                               transcript_match)


def _base_step():
    return {"exit_code": 0,
            "env": {"PAICOM_MIGRATION_MODE": "full",
                    "PAICOM_RUNTIME_VERIFIED_64BIT": "1"},
            "libs": [NativeLibInfo("libvosk.so", True, True, True)],
            "ref_transcript": "Hey PAIcom, open the browser",
            "hyp_transcript": "hey paicom open the browser"}


def test_gate1_pass():
    r = evaluate(_base_step(), first_boot=True)
    assert r.passed, r.failures


def test_gate1_exit_code_authoritative():
    s = _base_step(); s["exit_code"] = 1
    r = evaluate(s)
    assert not r.passed and any("exit_code" in f for f in r.failures)


def test_gate1_env_gating():
    s = _base_step(); s["env"] = {"PAICOM_MIGRATION_MODE": "stable"}
    r = evaluate(s)
    assert not r.passed and any("PAICOM_RUNTIME_VERIFIED_64BIT" in f for f in r.failures)


def test_gate1_first_boot_ldd_and_warm_cache_pair():
    bad = _base_step()
    bad["libs"] = [NativeLibInfo("libvosk.so", True, True, False)]
    assert not evaluate(bad, first_boot=True).passed
    # warm cache: ldd not re-required
    assert evaluate(bad, first_boot=False).passed


def test_gate1_transcript_tolerance():
    ok, wer = transcript_match("Hello, World!", "hello world")
    assert ok and wer == 0.0
    ok2, wer2 = transcript_match("open the browser please", "close the window now")
    assert not ok2 and wer2 > 0.2
