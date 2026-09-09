"""Fix loop: the coding-agent iteration contract (6b calibration and beyond).

The loop the harness serves as instrument for:

  cycle -> verdict.json (diagnosis, family, evidence refs)
    -> coding agent reads diagnosis
    -> edits patcher (or golden/runtime config)
    -> dotnet publish -> scp md5-gated -> stage -> snapshot (ceremony)
    -> cycle -> next verdict

Termination is pre-registered (D3 discipline — without it the loop gets
rationalized into running forever or stopping early):
  - pass: the target verdict flips to pass.
  - anti-loop tripwire: the same diagnosis across consecutive iterations
    with no diagnostic movement — the fix isn't touching the cause.
  - budget: max iterations exhausted.
  - Mac gate/policy: any Mac regression signal stops the loop.

Mac-safe preference (audio-fork constraint): runtime/environment fixes
(Wine stack, domain XML, prefix state) over library migrations. A
migration fix (e.g. NAudio 3, which has no macOS CoreAudio backend) must
be platform-conditional or the loop refuses it — "Mac untouched" as code,
not hope. The per-iteration cheap gate diffs Mac-generated artifacts
against the Phase-A baseline without needing a Mac; missing baseline
reports no-baseline (SKIP), never a silent pass.
"""
from __future__ import annotations

import hashlib
import json
from dataclasses import dataclass, field
from pathlib import Path
from typing import Any, Callable

# Fix kinds: runtime/environment preferred; migration gated by policy.
RUNTIME_KINDS = {"runtime", "golden", "config"}
MIGRATION_KINDS = {"migration"}


@dataclass
class FixLoopConfig:
    max_iterations: int = 10
    same_diagnosis_trip: int = 2
    target_verdict: str = "pass"


def read_verdict(run_dir: str | Path) -> dict:
    return json.loads(Path(run_dir, "verdict.json").read_text(encoding="utf-8"))


def check_tripwire(diagnoses: list[str | None], limit: int = 2) -> bool:
    """True when the last `limit` diagnoses are identical and non-null:
    the loop is iterating without moving the diagnosis."""
    if limit < 2 or len(diagnoses) < limit:
        return False
    tail = diagnoses[-limit:]
    return tail[0] is not None and all(d == tail[0] for d in tail)


def mac_policy_check(fix: dict) -> tuple[bool, str]:
    """Mac-safe constraint as code. fix: {"kind", "platform_conditional"}."""
    kind = fix.get("kind", "")
    if kind in RUNTIME_KINDS:
        return True, f"runtime-kind fix ({kind}): Mac path untouched by construction"
    if kind in MIGRATION_KINDS:
        if fix.get("platform_conditional") is True:
            return True, "migration is platform-conditional; Mac verified separately"
        return False, ("migration fix without platform_conditional: NAudio-class "
                       "migrations can break the working Mac path — refusing")
    return False, f"unknown fix kind {kind!r}: declare runtime|golden|config|migration"


def _hash_file(path: str | Path) -> str:
    return hashlib.md5(Path(path).read_bytes()).hexdigest()


def mac_baseline_diff(baseline_dir: str | Path | None,
                      artifacts: dict[str, str | Path]) -> dict:
    """Cheap per-iteration Mac gate: hash-compare Mac-generated artifacts
    against the Phase-A baseline. No Mac required."""
    if baseline_dir is None or not Path(baseline_dir).exists():
        return {"state": "no-baseline", "diffs": [],
                "note": "Phase-A baseline not captured yet: SKIP, not pass"}
    diffs = []
    for name, path in artifacts.items():
        base = Path(baseline_dir, name)
        if not Path(path).exists():
            diffs.append({"artifact": name, "state": "missing"})
        elif not base.exists():
            diffs.append({"artifact": name, "state": "unbaselined"})
        elif _hash_file(path) != _hash_file(base):
            diffs.append({"artifact": name, "state": "changed"})
    if diffs:
        return {"state": "fail", "diffs": diffs}
    return {"state": "pass", "diffs": []}


@dataclass
class FixLoopResult:
    stop: str  # pass | tripwire | budget | mac-policy | mac-gate | error | fix-failed
    iterations: int
    ledger: list[dict] = field(default_factory=list)


class FixError(Exception):
    """A fix step that executed and failed its own effect-verify (retry
    policy lives in the fix step; reaching here means exhausted). Distinct
    from tripwire (verdicts repeating) and error (unreadable inputs): the
    fix is known-bad, the diagnosis is unchanged, and the loop must stop
    with the failure attributed — never crash with the ledger in memory."""


def run_loop(loop_dir: str | Path, config: FixLoopConfig,
             diagnose_fn: Callable[[int, list[dict]], str | Path],
             fix_fn: Callable[[dict, int], dict],
             cycle_fn: Callable[[dict, int], str | Path],
             mac_artifacts_fn: Callable[[str | Path], dict] | None = None,
             baseline_dir: str | Path | None = None) -> FixLoopResult:
    """Drive iterations to a pre-registered stop. All step functions are
    injectable (fakes in suite; publish/scp/stage/snapshot/cycle live).

    diagnose_fn(iteration, ledger) -> run_dir holding the new verdict.
      Iteration 0's diagnose seeds from the pre-loop baseline run.
    fix_fn(verdict, iteration) -> fix record {"fix_id","kind",...}.
    cycle_fn(fix, iteration) -> run_dir of the post-fix verification cycle.
    Ledger appends per iteration (views must survive the process).
    """
    loop = Path(loop_dir)
    loop.mkdir(parents=True, exist_ok=True)
    ledger: list[dict] = []
    diagnoses: list[str | None] = []

    def _save():
        (loop / "fixloop-ledger.json").write_text(
            json.dumps(ledger, indent=2, sort_keys=True), encoding="utf-8")

    for i in range(config.max_iterations):
        try:
            pre_dir = diagnose_fn(i, ledger)
            verdict = read_verdict(pre_dir)
        except Exception as e:
            ledger.append({"iteration": i, "stop": "error",
                           "error": f"{type(e).__name__}: {e}"})
            _save()
            return FixLoopResult("error", i + 1, ledger)
        diagnosis = verdict.get("diagnosis")
        diagnoses.append(diagnosis)
        entry: dict[str, Any] = {"iteration": i, "diagnosis": diagnosis,
                                 "verdict": verdict.get("verdict"),
                                 "run_dir": str(pre_dir)}
        if verdict.get("verdict") == config.target_verdict:
            entry["stop"] = "pass"
            ledger.append(entry)
            _save()
            return FixLoopResult("pass", i + 1, ledger)
        if check_tripwire(diagnoses, config.same_diagnosis_trip):
            entry["stop"] = "tripwire"
            entry["note"] = (f"same diagnosis {diagnosis!r} x{config.same_diagnosis_trip}: "
                             "fix not touching the cause — escalate to human")
            ledger.append(entry)
            _save()
            return FixLoopResult("tripwire", i + 1, ledger)
        try:
            fix = fix_fn(verdict, i)
        except FixError as e:
            entry["stop"] = "fix-failed"
            entry["error"] = f"{type(e).__name__}: {e}"
            ledger.append(entry)
            _save()
            return FixLoopResult("fix-failed", i + 1, ledger)
        entry["fix"] = fix
        ok, reason = mac_policy_check(fix)
        entry["mac_policy"] = {"ok": ok, "reason": reason}
        if not ok:
            entry["stop"] = "mac-policy"
            ledger.append(entry)
            _save()
            return FixLoopResult("mac-policy", i + 1, ledger)
        post_dir = cycle_fn(fix, i)
        entry["post_run_dir"] = str(post_dir)
        if mac_artifacts_fn is not None:
            gate = mac_baseline_diff(baseline_dir, mac_artifacts_fn(post_dir))
            entry["mac_gate"] = gate
            if gate["state"] == "fail":
                entry["stop"] = "mac-gate"
                ledger.append(entry)
                _save()
                return FixLoopResult("mac-gate", i + 1, ledger)
        ledger.append(entry)
        _save()
    return FixLoopResult("budget", config.max_iterations, ledger)
