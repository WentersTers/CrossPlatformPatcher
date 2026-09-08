"""Artifact layout (§9): runs/{ts}/{os}-{ver}/{test-id}/ + verdict + retention."""
from __future__ import annotations

import json
from datetime import datetime, timezone
from pathlib import Path
from typing import Any


def build_run_dir(root: str | Path, os_name: str, ver: str, test_id: str,
                  ts: str | None = None) -> Path:
    ts = ts or datetime.now(timezone.utc).strftime("%Y%m%dT%H%M%SZ")
    d = Path(root) / ts / f"{os_name}-{ver}" / test_id
    d.mkdir(parents=True, exist_ok=True)
    return d


def write_verdict(run_dir: str | Path, verdict: str, gates: dict,
                  state_method: str | None = None, runtime: str = "native-linux-x64",
                  evidence_refs: list[str] | None = None,
                  synthetic: bool = False,
                  diagnosis: str | None = None,
                  evidence: dict | None = None,
                  failures: list[str] | None = None) -> dict:
    """Unified verdict schema (Session-7 pre-cycle convergence).

    The real Session-6 verdict is the authority on what this schema IS:
    every verdict carries the full key set — verdict, gates, runtime,
    synthetic, diagnosis, evidence, failures, state_method,
    state_verification_non_deterministic, evidence_refs — with null/empty
    where a lane didn't run (absence recorded, never omitted). Rehearsal
    verdicts converge toward this form, never the reverse.

    - state_method None: no state verification ran (e.g. startup-death
      before first frame); callers with a real method pass it explicitly.
    - A Tier B (VLM) test is flagged non-deterministic for human filtering.
    """
    payload = {
        "verdict": verdict,
        "gates": dict(gates),
        "runtime": runtime,
        "synthetic": synthetic,
        "diagnosis": diagnosis,
        "evidence": dict(evidence) if evidence is not None else {},
        "failures": list(failures) if failures is not None else [],
        "state_method": state_method,
        "state_verification_non_deterministic": state_method == "vlm",
        "evidence_refs": list(evidence_refs) if evidence_refs is not None else [],
    }
    Path(run_dir, "verdict.json").write_text(json.dumps(payload, indent=2, sort_keys=True))
    return payload


def retention_days(passed: bool) -> int | None:
    """30 days passing, indefinite failures (§9). None = indefinite."""
    return 30 if passed else None
