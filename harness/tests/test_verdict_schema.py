"""Unified verdict schema (Session-7 pre-cycle convergence).

The real Session-6 verdict is the authority: the unified writer must emit
natively what Session 6 hand-rolled (fail, family-c diagnosis, Gate-1
evidence, runtime label, synthetic=False), and rehearsals converge toward
that form. Absent lanes are null/empty, never omitted.
"""
import json

from harness.artifacts.layout import write_verdict

UNIFIED_KEYS = {"verdict", "gates", "runtime", "synthetic", "diagnosis",
                "evidence", "failures", "state_method",
                "state_verification_non_deterministic", "evidence_refs"}


def test_real_defaults_carry_full_key_set(tmp_path):
    v = write_verdict(tmp_path, "fail", {"1": False}, runtime="system-wine")
    assert set(v) == UNIFIED_KEYS
    assert v["synthetic"] is False
    assert v["diagnosis"] is None
    assert v["evidence"] == {} and v["failures"] == []
    assert v["state_method"] is None
    assert v["state_verification_non_deterministic"] is False
    assert v["evidence_refs"] == []
    assert json.loads((tmp_path / "verdict.json").read_text()) == v


def test_session6_shape_round_trips_through_unified_writer(tmp_path):
    """Session 6's archived values, re-emitted by the unified writer."""
    v = write_verdict(
        tmp_path, "fail", {"1": False}, runtime="system-wine",
        synthetic=False,
        diagnosis="runtime-startup/wine-mono-missing (family c)",
        evidence={"env": {"PAICOM_MIGRATION_MODE": "full",
                          "PAICOM_RUNTIME_VERIFIED_64BIT": "1"},
                  "exit_code": 255, "libs_checked": 0, "log_bytes": 4265},
        failures=["exit_code=255 != 0"],
        state_method=None,
        evidence_refs=["tier0.provenance.json"],
    )
    assert v["verdict"] == "fail" and v["gates"] == {"1": False}
    assert v["runtime"] == "system-wine" and v["synthetic"] is False
    assert v["diagnosis"] == "runtime-startup/wine-mono-missing (family c)"
    assert v["evidence"]["exit_code"] == 255
    assert v["evidence"]["log_bytes"] == 4265
    assert v["failures"] == ["exit_code=255 != 0"]


def test_vlm_still_flags_non_deterministic(tmp_path):
    v = write_verdict(tmp_path, "pass", {"1": True}, state_method="vlm",
                      synthetic=True)
    assert v["state_verification_non_deterministic"] is True
    assert v["synthetic"] is True
