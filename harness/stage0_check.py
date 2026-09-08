"""Stage-0 exit-criteria smoke check (§14).

Runs the harness pytest suite and reports the quantitative gates:
- silent tool failures (NO_VISUAL_CHANGE + STALE_SCREENSHOT counters exercised)
- Tier A calibration signal (template-matcher tests)
- gate_disagree baseline hook (D3/D9 trigger unit tests)
Usage: python harness/stage0_check.py
"""
from __future__ import annotations

import subprocess
import sys
from pathlib import Path

ROOT = Path(__file__).resolve().parent.parent


def main() -> int:
    print("== harness stage-0 smoke check ==")
    proc = subprocess.run([sys.executable, "-m", "pytest", "harness/tests", "-q"],
                          cwd=ROOT, capture_output=True, text=True)
    print(proc.stdout[-3000:])
    if proc.returncode != 0:
        print(proc.stderr[-2000:])
        print("STAGE0 SMOKE: FAIL")
        return proc.returncode
    print("STAGE0 SMOKE: PASS (see harness/tests for per-gate counters)")
    print("- full stub loop + kill-resume, no double QMP: test_full_loop.py")
    print("- QMP sent-but-unacked reconcile-by-observation: test_qmp_ledger.py")
    print("- transitional allow-list counter-case: test_sequence.py")
    print("- remote-libvirt contract (Q1): test_remote_libvirt.py")
    print("- lock-screen suppresses divergence: test_lockscreen.py")
    print("- D3 loop integration (verdict->trigger): test_d3_integration.py")
    print("- D9 storm->review->library growth: test_d9_review_queue.py")
    print("- D9 telemetry-disagree feed (subset-match): test_d9_disagreement_feed.py")
    print("- telemetry schema/gaps/heartbeat/event-sync: test_telemetry.py")
    print("- fault-localization matrix (13 rows + vision-only fallback): test_localization_matrix.py")
    print("- rehearsal shape contract: test_dry_run.py "
          "(run: python harness/dry_run.py --root harness/runs)")
    print("- unified verdict schema (real-authoritative): test_verdict_schema.py")
    print("- Session-7 ten-cycle loop + kill-resume + hang shade: test_session7.py")
    print("- pet_absent band + present gate (no storm on boot): test_pet_absent.py")
    print("  + dock-icon mirror pin (ambiguous burn, ceiling margin ~0.05 synthetic)")
    print("- Session-0 preflight (real virsh path + ssh-hop): test_preflight.py")
    print("- Session-7 seams (hop argv, pool revert, qmp prefix, 2-leg shots): "
          "test_seams.py")
    print("- QGA-over-hop launch channel: test_qga.py")
    print("- fix-loop contract (tripwires, Mac policy/gate): test_fixloop.py")
    print("  + kvm probe, latency/Samples, host-key hint, URI query passthrough")
    print("- evidence helper p50/p95 + --cycles runner: test_measurements_cycles.py")
    print("- repo hygiene meta-test: test_meta.py")
    print("- md5-gated put-file (Session-1 ack-without-effect): test_putfile.py")
    print("- per-event press/release + stuck-press audit: test_qmp_ledger.py")
    print("- guest IP lookup-not-config: test_guestinfo.py")
    print("- OCR backend + watchdog loop + scoped recipe: test_ocr_watchdog.py")
    print("- no-change band pin (0.95) + aim/effect localize: test_gate2.py")
    print("- rolling-set stability + concrete-only present gate: "
          "test_watchdog.py, test_pet_absent.py")
    print("- move-only transport + visual pinning order: "
          "test_live.py, test_packer_template.py")
    print("- storm row live shape + Tier-0 launch + log patterns: "
          "test_sequence.py, test_session56.py")
    print("- 22.04 golden template pins (HCL semantics: Session 1): "
          "test_packer_template.py")
    print("  + secrets/rehearsal gitignore hygiene")
    print("- NO_VISUAL_CHANGE / STALE_SCREENSHOT guards: test_tool_guards.py")
    print("- Tier A calibration + ambiguous-storm: test_template_matcher.py, test_sequence.py")
    print("- D3 rolling-50/consecutive + D9: test_trigger_tracker.py, test_manager.py")
    print("- checkpointer resume + idempotency: test_persistence_state.py, test_full_loop.py")
    return 0


if __name__ == "__main__":
    raise SystemExit(main())
