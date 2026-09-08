"""Scripted ledgered click driver (Session 2 items 3-5, reusable to Session 7).

click_id -> ledger :press/:release claims -> press -> [kill_window_s delay,
test-only: opens the kill-mid-action window] -> release. On resume after a
kill, acked steps are replay-skipped (press never re-sent) and only pending
steps fire — the QMP send log proves the granularity. No agent, no VLM, no
guessing: deterministic input with a ledger.

Resend note: a step left 'sent' by a mid-call kill re-fires on resume
(at-least-once per event). Press-while-held is a QMP no-op, so the only
unsafe direction — skipping a release while press is held — cannot happen:
release fires unless already acked.
"""
from __future__ import annotations

import time

from harness.tools.qmp_ledger import (QmpLedger, audit_stuck_press,
                                      reconcile_click)
from harness.gates.gate2_ssim_ocr import NO_CHANGE_THRESHOLD


def _fire_step(ledger: QmpLedger, action_id: str, kind: str, x: int, y: int,
               send_fn) -> str:
    act, _ = ledger.claim(action_id, kind, {"x": x, "y": y})
    if act.state == "acked":
        return "replayed"
    ledger.mark_sent(action_id)
    send_fn(x, y)
    ledger.mark_acked(action_id)
    return "sent"


def drive_click(ledger: QmpLedger, transport, action_id: str,
                x: int, y: int, kill_window_s: float = 0.0,
                sleep_fn=None, checkpoint_fn=None) -> dict:
    """Idempotent-resumable: rerunning after a kill completes pending steps.

    checkpoint_fn, when given, persists the ledger after press and after
    release — the kill window sits between two durable states, so resume
    never guesses what was sent.
    """
    _sleep = sleep_fn or time.sleep
    press = _fire_step(ledger, f"{action_id}:press", "press", x, y,
                       lambda px, py: transport.press(px, py))
    if checkpoint_fn is not None:
        checkpoint_fn()
    if kill_window_s > 0 and press == "sent":
        _sleep(kill_window_s)
    release = _fire_step(ledger, f"{action_id}:release", "release", x, y,
                         lambda px, py: transport.release(px, py))
    if checkpoint_fn is not None:
        checkpoint_fn()
    return {"action_id": action_id,
            "outcome": {"press": press, "release": release},
            "stuck": audit_stuck_press(ledger)}


def resume_click(ledger: QmpLedger, transport, action_id: str,
                 x: int, y: int, effect_observed: bool) -> dict:
    """Resume adjudication after a kill: reconcile first (the recorded
    decision), then drive to completion. effect_observed comes from
    post-resume observation (screenshot SSIM), never from the ledger."""
    decision = reconcile_click(ledger, action_id, effect_observed)
    if decision == "send-release":
        transport.release(x, y)
        rel = ledger._actions.get(f"{action_id}:release")
        if rel is not None:
            rel.state = "acked"
    elif decision == "resend-press":
        drive_click(ledger, transport, action_id, x, y)
    return {"action_id": action_id, "decision": decision,
            "stuck": audit_stuck_press(ledger)}


def _build_transport(domain: str, uri: str, ssh_hop: bool):
    from harness.live.qmp_transport import QmpTransport
    if ssh_hop:
        from harness.live.virsh_argv import prefix_for
        return QmpTransport(domain=domain, uri=uri,
                            argv_prefix=prefix_for(uri, ssh_hop=True))
    return QmpTransport(domain=domain, uri=uri)


def _load_ledger(path: str) -> QmpLedger:
    import json
    from pathlib import Path
    p = Path(path)
    if p.exists():
        return QmpLedger.from_dict(json.loads(p.read_text(encoding="utf-8")))
    return QmpLedger()


def _save_ledger(ledger: QmpLedger, path: str) -> None:
    import json
    from pathlib import Path
    p = Path(path)
    p.parent.mkdir(parents=True, exist_ok=True)
    p.write_text(json.dumps(ledger.to_dict(), indent=2, sort_keys=True),
                 encoding="utf-8")


def main(argv=None) -> int:
    """CLI: ledgered click with before/after screenshots + SSIM + send log.

    python -m harness.live.click_driver --x 35 --y 165 --out runs/click-01
        --action-id click-files-1 [--kill-window 8] [--ledger-file ...]
    """
    import argparse
    import json
    import subprocess
    from pathlib import Path

    import numpy as np

    from harness.gates.gate2_ssim_ocr import ssim_gray, to_gray
    from harness.live.qmp_transport import QmpTransport

    ap = argparse.ArgumentParser(description="Ledgered click driver (Session 2)")
    ap.add_argument("--x", type=int, required=True)
    ap.add_argument("--y", type=int, required=True)
    ap.add_argument("--out", required=True)
    ap.add_argument("--action-id", required=True)
    ap.add_argument("--domain", default="ubuntu-2204-stage0")
    ap.add_argument("--uri", default="qemu:///system")
    ap.add_argument("--ssh-hop", action="store_true",
                    help="route QMP input through the ssh hop (input only; "
                         "screenshots stay local — pair with watchdog frames)")
    ap.add_argument("--kill-window", type=float, default=0.0)
    ap.add_argument("--ledger-file", default=None)
    ap.add_argument("--settle-s", type=float, default=0.0,
                    help="wait after release before the after-shot: app launch "
                         "latency means immediate SSIM measures nothing")
    args = ap.parse_args(argv)

    out = Path(args.out)
    out.mkdir(parents=True, exist_ok=True)
    ledger_path = args.ledger_file or str(out / "ledger.json")
    transport = _build_transport(args.domain, args.uri, args.ssh_hop)

    def shot(name: str) -> str:
        path = str(out / name)
        subprocess.run(["virsh", "-c", args.uri, "screenshot", args.domain,
                        path], check=True, capture_output=True, timeout=120)
        return path

    def checkpoint() -> None:
        _save_ledger(ledger, ledger_path)

    ledger = _load_ledger(ledger_path)
    # Protocol order: position move FIRST, then before-shot. The cursor lands
    # in both frames and cancels out of verification (Session-4 fix).
    if hasattr(transport, "move"):
        transport.move(args.x, args.y)
    before = shot("before.ppm")
    result = drive_click(ledger, transport, args.action_id, args.x, args.y,
                         kill_window_s=args.kill_window,
                         checkpoint_fn=checkpoint)
    if args.settle_s > 0:
        time.sleep(args.settle_s)
    after = shot("after.ppm")
    checkpoint()

    from PIL import Image
    ssim = ssim_gray(to_gray(np.asarray(Image.open(before).convert("L"))),
                     to_gray(np.asarray(Image.open(after).convert("L"))))
    result["ssim_before_after"] = ssim
    result["no_visual_change"] = ssim >= NO_CHANGE_THRESHOLD
    (out / "result.json").write_text(json.dumps(result, indent=2, sort_keys=True))
    (out / "qmp-log.json").write_text(
        json.dumps(transport.send_log, indent=2, sort_keys=True))
    print(f"outcome={result['outcome']} ssim={ssim:.5f} "
          f"no_change={result['no_visual_change']} stuck={result['stuck']}")
    return 0


if __name__ == "__main__":
    raise SystemExit(main())
