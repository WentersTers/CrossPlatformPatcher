"""Session-7 real-cycle driver: revert -> launch -> verdict, over the hop.

Wires the proven pieces behind run_session()'s injectables:
  pool      VmPool(SshHopTransport) — revert REALLY happens (via: session)
  launch    GuestRunFactory(QgaBackend) — Tier-0 tee over QGA guest-exec
  capture   two-leg shots.pull_screenshot (host capture + scp + md5)
  classify  template matcher on an empty library (startup-death reads
            pet_absent honestly; positives are Phase-A work)
  logs      QGA cat of the launcher-owned logs into the run dir BEFORE
            the checkpoint (claimed lane, both sources)

Crash drill: --kill-after N raises after cycle N checkpoints; re-run the
same command WITHOUT --kill-after to resume. Exit 2 = killed, resume
pending. Exit 0 = all cycles complete + session7-report.json written.
"""
from __future__ import annotations

import argparse
import subprocess
import sys
import tempfile
import time
from pathlib import Path

HARNESS_ROOT = Path(__file__).resolve().parent.parent
sys.path.insert(0, str(HARNESS_ROOT.parent))

import cv2  # noqa: E402

from harness.gates.gate2_5_state.template_matcher import StateTemplateMatcher  # noqa: E402
from harness.gates.gate2_ssim_ocr import ssim_gray, to_gray  # noqa: E402
from harness.live.cycles import run_session  # noqa: E402
from harness.live.guest_run import GuestRunFactory  # noqa: E402
from harness.live.qga import QgaBackend, QgaError  # noqa: E402
from harness.preflight import SshHopTransport, VirshTransport  # noqa: E402
from harness.live.shots import pull_screenshot  # noqa: E402
from harness.live.virsh_argv import prefix_for  # noqa: E402
from harness.measurements import Samples  # noqa: E402
from harness.vms.pool import VmPool  # noqa: E402

ENV_FULL = {"PAICOM_MIGRATION_MODE": "full",
            "PAICOM_RUNTIME_VERIFIED_64BIT": "1"}


def qga_read(be: QgaBackend, path: str, deadline_s: float = 90.0) -> str:
    pid = be.exec(["cat", path])
    t0 = time.monotonic()
    while time.monotonic() - t0 < deadline_s:
        st = be.status(pid)
        if st["exited"]:
            if st["exitcode"] != 0:
                raise QgaError(f"cat {path}: exit {st['exitcode']}: "
                               f"{st['err'][:200]}")
            return st["out"]
        time.sleep(1.0)
    raise QgaError(f"cat {path}: timed out after {deadline_s}s")


def main(argv: list[str] | None = None) -> int:
    ap = argparse.ArgumentParser(description="Session-7 real cycles")
    ap.add_argument("--uri", required=True)
    ap.add_argument("--domain", default="ubuntu-2204-stage")
    ap.add_argument("--snapshot", default="base-v4-app")
    ap.add_argument("--app-dir", default="/home/sage/paicom/patched")
    ap.add_argument("--cycles", type=int, default=10)
    ap.add_argument("--kill-after", type=int, default=None)
    ap.add_argument("--root", default=str(HARNESS_ROOT / "runs" / "session7-real"))
    ap.add_argument("--timeout", type=float, default=300.0)
    ap.add_argument("--poll", type=float, default=5.0)
    ap.add_argument("--ssh-hop", action="store_true")
    ap.add_argument("--user", default=None,
                    help="run the payload as this guest user via runuser "
                         "(QGA executes as root; Wine prefixes are "
                         "user-owned — launch as the delivery owner)")
    args = ap.parse_args(argv)

    transport = SshHopTransport() if args.ssh_hop else VirshTransport()
    pool = VmPool(transport=transport)
    pool.connect(args.uri)
    prefix = prefix_for(args.uri, ssh_hop=args.ssh_hop)
    info_target = args.uri  # only used for messages below

    be = QgaBackend(args.domain, prefix,
                    env={} if args.user else {"DISPLAY": ":0"})
    fac = GuestRunFactory(be.exec, be.status, be.kill)
    run_sh = f"{args.app_dir}/run.sh"
    if args.user:
        cmd = ["timeout", str(int(args.timeout)), "runuser",
               "-u", args.user, "--", "env", "DISPLAY=:0",
               "bash", run_sh]
    else:
        cmd = ["timeout", str(int(args.timeout)), "bash", run_sh]
    matcher = StateTemplateMatcher({})
    pull_s: list[float] = []
    counter = [0]

    def scp_fetch(tgt, remote, local):
        subprocess.run(["scp", "-o", "BatchMode=yes", f"{tgt}:{remote}", local],
                       check=True, capture_output=True, timeout=180)

    try:
        from harness.vms.pool import parse_libvirt_uri
        info = parse_libvirt_uri(args.uri)
        ssh_target = f"{info['user']}@{info['host']}" if info["user"] else info["host"]
    except ValueError:
        ssh_target = args.uri
    ssh_prefix = ["ssh", "-o", "BatchMode=yes", "-o", "ConnectTimeout=10",
                  "-o", "StrictHostKeyChecking=accept-new", ssh_target]

    def capture():
        i = counter[0]
        counter[0] += 1
        remote = f"/tmp/s7-c{i:02d}.ppm"
        t0 = time.monotonic()
        with tempfile.TemporaryDirectory() as d:
            tmp = str(Path(d) / "frame.ppm")
            pull_screenshot(virsh_prefix=prefix, ssh_prefix=ssh_prefix,
                            ssh_target=ssh_target, domain=args.domain,
                            remote_path=remote, local_path=tmp,
                            run=lambda c: (lambda p: (p.returncode, p.stdout, p.stderr))(
                                subprocess.run(c, capture_output=True, text=True, timeout=180)),
                            fetch=scp_fetch)
            img = cv2.imread(tmp)
        pull_s.append(time.monotonic() - t0)
        if img is None:
            raise QgaError("captured frame unreadable")
        return img

    def logs_fn(run_dir):
        names = []
        for name in ("launcher.log", "launcher-runtime.log"):
            text = qga_read(be, f"{args.app_dir}/{name}")
            (Path(run_dir) / name).write_text(text, encoding="utf-8")
            names.append(name)
        return names

    def guest_epoch() -> float:
        pid = be.exec(["date", "+%s"])
        t0 = time.monotonic()
        while time.monotonic() - t0 < 60.0:
            st = be.status(pid)
            if st["exited"]:
                if st["exitcode"] != 0:
                    raise QgaError(f"date failed: {st['err'][:200]}")
                return float(st["out"].strip())
            time.sleep(1.0)
        raise QgaError("date timed out")

    samples = Samples()
    try:
        out = run_session(args.root, args.cycles, pool=pool, vm_id=args.domain,
                          snapshot=args.snapshot,
                          cmd=cmd,
                          observed_env=ENV_FULL, timeout_s=args.timeout,
                          poll_s=args.poll, popen_factory=fac,
                          capture_fn=capture,
                          classify_fn=lambda img: matcher.classify(img, None),
                          logs_fn=logs_fn, clock_fn=guest_epoch,
                          db_path=str(Path(args.root) / "session7.db"),
                          trace_id="session-7",
                          kill_after_cycle=args.kill_after,
                          samples=samples)
    except RuntimeError as e:
        print(f"KILLED: {e} — resume with the same command minus --kill-after")
        return 2

    # driver-level report: pull timings + cross-cycle frame stability
    frames = []
    for d in out["run_dirs"]:
        img = cv2.imread(str(Path(d) / "step-01-launch.png"), cv2.IMREAD_GRAYSCALE)
        frames.append(img)
    ssims = [ssim_gray(to_gray(frames[i]), to_gray(frames[i + 1]))
             for i in range(len(frames) - 1)] if len(frames) > 1 else []
    report = {"cycles_completed": out["cycles_completed"],
              "verdicts": [v["verdict"] for v in out["verdicts"]],
              "diagnoses": sorted({v["diagnosis"] or "none" for v in out["verdicts"]}),
              "revert_via": sorted({v["evidence"].get("revert_via", "?") for v in out["verdicts"]}),
              "pull_s": pull_s,
              "cross_frame_ssim": ssims,
              "samples": out["samples"],
              "run_dirs": out["run_dirs"]}
    import json
    (Path(args.root) / "session7-report.json").write_text(
        json.dumps(report, indent=2, sort_keys=True))
    print(f"cycles={out['cycles_completed']} verdicts={report['verdicts']} "
          f"diag={report['diagnoses']} via={report['revert_via']}")
    print(f"pull_s n={len(pull_s)} "
          f"ssim_pairs={len(ssims)} min={[round(s, 5) for s in ssims][:10]}")
    print(f"target was {info_target}")
    return 0


if __name__ == "__main__":
    raise SystemExit(main())
