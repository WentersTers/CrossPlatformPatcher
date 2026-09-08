"""Session-7 ten-cycle loop: revert -> launch (Tier-0 tee) -> Gate-1 verdict.

Each cycle reverts to the staged `base-v4-app` snapshot, launches the (c)
payload under the SAME Tier-0 code as Session 6, diagnoses the known-failure
family from the tee (never from assumption), and emits contract-shape
artifacts natively in the unified verdict schema — including tier0.log, the
independent stdout/stderr lane Session 6 was missing.

Crash-resume: per-cycle checkpoints in a Checkpointer db mean a kill mid-run
resumes without re-executing completed cycles (no double launch, mirroring
the QMP no-double-click rule). Nothing here swallows errors: unknown
snapshots, capture failures, and Gate-1 failures are loud (§8).
"""
from __future__ import annotations

import json
from datetime import datetime, timedelta, timezone
from pathlib import Path
from typing import Any, Callable

import cv2

from harness.artifacts.layout import build_run_dir, write_verdict
from harness.gates.gate1_deterministic import evaluate as g1
from harness.live.launch import launch
from harness.measurements import Samples
from harness.persistence.checkpointer import Checkpointer
from harness.tools.state import write_state

FAMILY_C_MONO = "runtime-startup/wine-mono-missing (family c)"
FAMILY_C_HANG = "runtime-startup/hang (family c, hang-shade)"
FAMILY_C_EXIT = "runtime-startup/exit-nonzero (family c)"
FAMILY_C_FATAL = "startup/fatal-unhandled (family c)"
FAMILY_A_DL = "mic-enumeration/dll-not-found (family a)"
FAMILY_E_FONTS = "ui-startup/gdi-font-missing (family e)"
FAMILY_BITNESS = "native-load/bad-image-format (bitness branch)"

TS_FMT = "%Y%m%dT%H%M%SZ"


def _utc_ts_now() -> str:
    return datetime.now(timezone.utc).strftime(TS_FMT)


def cycle_ts(base_ts: str, index: int) -> str:
    base = datetime.strptime(base_ts, TS_FMT).replace(tzinfo=timezone.utc)
    return (base + timedelta(seconds=index)).strftime(TS_FMT)


def classify_family(tier0_text: str, exit_code: int | None,
                    timed_out: bool) -> str | None:
    """Diagnose the known-failure family from tee evidence (§1d oracle).

    Precedence is causal, not textual: a FATAL UNHANDLED block names the
    exit cause, so it outranks every pattern. A bare DllNotFoundException
    is NOT family (a) by itself — the app catches native-load failures
    in degraded lanes (OWW onnx bridge) and continues; only a FATAL
    DllNotFound is mic/startup-load class. (6b calibration: caught onnx
    DllNotFound + fatal FontFamily — the old order misdiagnosed (a).)
    """
    text = tier0_text or ""
    if timed_out:
        return FAMILY_C_HANG
    fatal = "FATAL UNHANDLED" in text
    if fatal and ("FontFamily" in text or "FontFamilyNotFound" in text
                  or "GDI+" in text):
        return FAMILY_E_FONTS
    if "Wine Mono is not installed" in text:
        return FAMILY_C_MONO
    if "BadImageFormat" in text:
        return FAMILY_BITNESS
    if fatal and "DllNotFoundException" in text:
        return FAMILY_A_DL
    if fatal:
        return FAMILY_C_FATAL
    if exit_code not in (0, None):
        return FAMILY_C_EXIT
    return None


def _classification_dict(match: Any) -> dict | None:
    if match is None:
        return None
    if isinstance(match, dict):
        return {"state": match.get("state"),
                "confidence": float(match.get("score", 0.0)),
                "method": match.get("method", "template")}
    return {"state": getattr(match, "state", "ambiguous"),
            "confidence": float(getattr(match, "score", 0.0)),
            "method": getattr(match, "method", "template")}


def run_cycle(root: str | Path, index: int, ts: str, *,
              pool, vm_id: str, snapshot: str, cmd: list[str],
              runtime_label: str = "system-wine",
              observed_env: dict | None = None,
              timeout_s: float = 300.0, poll_s: float = 5.0,
              popen_factory: Callable | None = None,
              capture_fn: Callable[[], Any] | None = None,
              classify_fn: Callable[[Any], Any] | None = None,
              logs_fn: Callable[[Path], list[str]] | None = None,
              clock_fn: Callable[[], float] | None = None,
              sleep_fn=None, time_fn=None,
              checkpointer: Checkpointer | None = None,
              trace_id: str = "session-7",
              samples: Samples | None = None) -> dict:
    """One revert-launch-verdict cycle. Returns run_dir, verdict, skipped.

    logs_fn(run_dir): pull extra claimed-lane artifacts (launcher-owned
      logs) into run_dir BEFORE the checkpoint; returns pulled filenames
      for evidence_refs. A crash between verdict and checkpoint must never
      leave a "complete" cycle without its logs, so pulls are part of the
      cycle's durable completion, not a post-step.

    clock_fn(): guest epoch seconds read AFTER the revert, at cycle start.
      Evidence records clock_offset_s (guest minus console — snapshot
      restore resets the guest RTC to snapshot time, so Phase-0 telemetry
      reconcile must correct by this anchor, never assume synced clocks).
      A failed read records None + a sidecar note and lets the cycle
      proceed: the anchor is evidence, not the cycle's subject.
    """
    root = Path(root)
    run_dir = build_run_dir(root, "ubuntu", "22.04",
                            f"session7-cycle-{index:02d}", ts=ts)
    action_id = f"cycle-{index:02d}"

    if checkpointer is not None:
        state = checkpointer.load_checkpoint(trace_id) or {}
        done = state.get("done", {})
        if action_id in done:
            return {"run_dir": str(run_dir), "index": index,
                    "verdict": done[action_id], "skipped": True,
                    "replayed": True}

    clock_note = ""
    guest_epoch: float | None = None

    revert = pool.revert(vm_id, snapshot)

    # AFTER the revert: snapshot restore resets the guest RTC to snapshot
    # time, so the cycle's own clock is the post-revert one. A pre-revert
    # read would carry the previous cycle's guest-elapsed as systematic
    # error into Phase-0 settle reconciliation.
    if clock_fn is not None:
        try:
            guest_epoch = float(clock_fn())
        except Exception as e:
            clock_note = f"clock read failed: {type(e).__name__}: {e}"[:200]

    t = launch(cmd, run_dir / "tier0.log", timeout_s, poll_s,
               popen_factory=popen_factory, sleep_fn=sleep_fn, time_fn=time_fn)
    tier0_text = (run_dir / "tier0.log").read_text(encoding="utf-8")

    env = observed_env or {}
    r1 = g1({"exit_code": t["exit_code"], "env": env, "libs": [],
             "log_text": tier0_text})
    passed = r1.passed
    diagnosis = classify_family(tier0_text, t["exit_code"], t["timed_out"])
    if not passed and diagnosis is None:
        diagnosis = "undiagnosed-failure"

    classification = None
    if capture_fn is not None:
        img = capture_fn()
        cv2.imwrite(str(run_dir / "step-01-launch.png"), img)
        if classify_fn is not None:
            classification = _classification_dict(classify_fn(img))
    sidecar = {"step": 1, "cmd": cmd, "exit_code": t["exit_code"],
               "timed_out": t["timed_out"], "synthetic": False,
               "runtime": runtime_label,
               "log_tail": tier0_text.strip().splitlines()[-1]
               if tier0_text.strip() else "",
               "gate_results": {"g1": passed},
               "state_classification": classification}
    if clock_note:
        sidecar["clock_note"] = clock_note
    (run_dir / "step-01-launch.json").write_text(
        json.dumps(sidecar, indent=2, sort_keys=True), encoding="utf-8")

    state = write_state(
        run_dir / "state.json",
        {"vm": "ubuntu-22.04",
         "app": {"running": bool(passed), "runtime": runtime_label,
                 "launched_at": datetime.now(timezone.utc).isoformat()},
         "app_claimed_state": None,
         "runtime_selected": runtime_label,
         "last_task": {"id": f"{trace_id}-{action_id}",
                       "verdict": "pass" if passed else "fail",
                       "gates_passed": [1] if passed else []}})

    evidence = dict(r1.evidence)
    evidence.update({"timed_out": t["timed_out"],
                     "duration_s": t["duration_s"],
                     "alive_samples": t["alive_samples"],
                     "revert_n": revert["revert_n"],
                     "revert_via": revert.get("via", "stub"),
                     "guest_epoch_s": guest_epoch,
                     "clock_offset_s": (guest_epoch - datetime.now(timezone.utc).timestamp())
                     if guest_epoch is not None else None})
    refs = ["tier0.log", "step-01-launch.png", "step-01-launch.json"]
    if logs_fn is not None:
        refs += [str(n) for n in (logs_fn(run_dir) or [])]
    verdict = write_verdict(
        run_dir, "pass" if passed else "fail", {"1": passed},
        state_method=(classification or {}).get("method"),
        runtime=runtime_label,
        evidence_refs=refs,
        synthetic=False, diagnosis=diagnosis, evidence=evidence,
        failures=list(r1.failures))

    if samples is not None:
        samples.add("cycle.duration_s", t["duration_s"], unit="s")
        samples.add("cycle.alive_samples", float(t["alive_samples"]))
        samples.add("cycle.timed_out", 1.0 if t["timed_out"] else 0.0)

    if checkpointer is not None:
        checkpointer.append_event_idempotent(
            trace_id, action_id,
            {"actor": "session", "action": action_id,
             "verdict": verdict["verdict"], "run_dir": str(run_dir)})
        st = checkpointer.load_checkpoint(trace_id) or {}
        done = dict(st.get("done", {}))
        done[action_id] = verdict  # full verdict cached: resume reuses it
        st["done"] = done
        checkpointer.save_checkpoint(trace_id, st)

    return {"run_dir": str(run_dir), "index": index, "verdict": verdict,
            "state_version": state["version"], "skipped": False,
            "replayed": False}


def run_session(root: str | Path, n: int = 10, *, ts_base: str | None = None,
                pool, vm_id: str, snapshot: str, cmd: list[str],
                runtime_label: str = "system-wine",
                observed_env: dict | None = None,
                timeout_s: float = 300.0, poll_s: float = 5.0,
                popen_factory: Callable | None = None,
                capture_fn: Callable[[], Any] | None = None,
                classify_fn: Callable[[Any], Any] | None = None,
                logs_fn: Callable[[Path], list[str]] | None = None,
                clock_fn: Callable[[], float] | None = None,
                sleep_fn=None, time_fn=None,
                db_path: str | Path | None = None,
                trace_id: str = "session-7",
                kill_after_cycle: int | None = None,
                samples: Samples | None = None) -> dict:
    """Run n cycles with checkpoint resume. Raises RuntimeError (simulated
    kill) after kill_after_cycle checkpoints; re-invoke without it to resume.
    Per-cycle ts derives from the checkpointed base so resume reuses dirs."""
    root = Path(root)
    cp = Checkpointer(db_path or (root / "session7.db"))
    st = cp.load_checkpoint(trace_id) or {}
    if "base_ts" not in st:
        st = {"base_ts": ts_base or _utc_ts_now(), "done": {}}
        cp.save_checkpoint(trace_id, st)
    base_ts = st["base_ts"]
    own_samples = samples if samples is not None else Samples()

    verdicts: list[dict] = []
    run_dirs: list[str] = []
    for i in range(n):
        res = run_cycle(root, i, cycle_ts(base_ts, i), pool=pool, vm_id=vm_id,
                        snapshot=snapshot, cmd=cmd,
                        runtime_label=runtime_label,
                        observed_env=observed_env, timeout_s=timeout_s,
                        poll_s=poll_s, popen_factory=popen_factory,
                        capture_fn=capture_fn, classify_fn=classify_fn,
                        logs_fn=logs_fn, clock_fn=clock_fn,
                        sleep_fn=sleep_fn, time_fn=time_fn,
                        checkpointer=cp, trace_id=trace_id,
                        samples=own_samples)
        verdicts.append(res["verdict"])
        run_dirs.append(res["run_dir"])
        if kill_after_cycle is not None and i == kill_after_cycle \
                and not res.get("skipped"):
            raise RuntimeError(f"simulated kill after cycle {i:02d}")

    summary = {"trace_id": trace_id, "cycles_completed": n,
               "verdicts": [v["verdict"] for v in verdicts],
               "diagnoses": sorted({v["diagnosis"] or "none"
                                    for v in verdicts}),
               "samples": own_samples.summary()}
    (root / "session7-summary.json").write_text(
        json.dumps(summary, indent=2, sort_keys=True), encoding="utf-8")
    return {"cycles_completed": n, "run_dirs": run_dirs,
            "verdicts": verdicts, "samples": own_samples.summary(),
            "summary_path": str(root / "session7-summary.json")}
