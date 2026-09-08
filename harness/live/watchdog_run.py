"""Watchdog live loop (Session 3): production cadence, overhead measured.

Each iteration: qga_ping (timed) -> screenshot + fb-hash (timed) -> log
check (missing file reports missing, NEVER stalled) -> sleep the cadence
remainder. Overhead % = poll time / cadence interval, recorded per iteration
into Samples — the <5% exit number comes out of this loop, not a model.
All backends injectable; the live run wires virsh + QGA on the host.
"""
from __future__ import annotations

import json
import subprocess
import time
from dataclasses import dataclass, field
from pathlib import Path
from typing import Any, Callable

from harness.measurements import Samples

ACTIVE_CADENCE_S = 5.0
IDLE_CADENCE_S = 30.0


@dataclass
class WatchdogReading:
    seq: int
    qga_state: str  # ok | unknown
    fb_hash: str
    log_present: bool
    log_delta: int
    poll_s: float
    overhead_pct: float


def _default_qga_ping(domain: str, uri: str,
                      virsh_prefix: list[str] | None = None) -> str:
    prefix = list(virsh_prefix) if virsh_prefix is not None else ["virsh", "-c", uri]
    payload = '{"execute":"guest-ping"}'
    if prefix[0] == "ssh":
        import shlex
        payload = shlex.quote(payload)
    p = subprocess.run(prefix + ["qemu-agent-command", domain, payload],
                       capture_output=True, text=True, timeout=30)
    return "ok" if p.returncode == 0 else "unknown"


def run_watchdog(iterations: int, cadence_s: float, out_dir: str | Path,
                 domain: str = "ubuntu-2204-stage0",
                 uri: str = "qemu:///system",
                 ping_fn: Callable[[], str] | None = None,
                 shot_fn: Callable[[int, str], None] | None = None,
                 log_fn: Callable[[], dict] | None = None,
                 sleep_fn=None, time_fn=None,
                 virsh_prefix: list[str] | None = None) -> dict:
    """Run N watchdog iterations. Returns paths + summary (also metrics.json).

    ping_fn: () -> qga state (raise -> recorded unknown).
    shot_fn: (seq, path) -> None (framebuffer capture to path).
    log_fn: () -> {"present": bool, "bytes": int} (byte counter source).
    virsh_prefix: live/virsh_argv prefix for the default backends. A hop
      prefix with no shot_fn is a loud ValueError: hop screenshots are two
      legs (shots.pull_screenshot), and a single-leg default would strand
      the frame on the host while reporting success.
    """
    import hashlib

    prefix = list(virsh_prefix) if virsh_prefix is not None else ["virsh", "-c", uri]
    hop = prefix[0] == "ssh"
    if hop and shot_fn is None:
        raise ValueError(
            "ssh-hop screenshots are two legs (live/shots.pull_screenshot); "
            "pass shot_fn — a single-leg default would strand frames on the host")
    _sleep = sleep_fn or time.sleep
    _clock = time_fn or time.monotonic
    out = Path(out_dir)
    out.mkdir(parents=True, exist_ok=True)
    samples = Samples()
    readings: list[dict[str, Any]] = []
    last_log_bytes: int | None = None

    for seq in range(iterations):
        t0 = _clock()
        try:
            qga = ping_fn() if ping_fn else _default_qga_ping(domain, uri, prefix)
        except Exception:
            qga = "unknown"
        frame = str(out / f"wd-{seq:02d}.ppm")
        if shot_fn is not None:
            shot_fn(seq, frame)
        else:
            subprocess.run(prefix + ["screenshot", domain, frame],
                           check=True, capture_output=True, timeout=120)
        fb_hash = hashlib.md5(Path(frame).read_bytes()).hexdigest()
        if log_fn is not None:
            info = log_fn()
        else:
            info = {"present": False, "bytes": 0}
        if not info.get("present", False):
            delta, present = 0, False  # missing file is missing, not stalled
        else:
            cur = int(info.get("bytes", 0))
            delta = max(0, cur - last_log_bytes) if last_log_bytes is not None else 0
            last_log_bytes = cur
            present = True
        poll = _clock() - t0
        overhead = poll / cadence_s * 100.0 if cadence_s > 0 else 0.0
        samples.add("watchdog.poll_s", poll, unit="s")
        samples.add("watchdog.overhead_pct", overhead, unit="%")
        readings.append({"seq": seq, "qga_state": qga, "fb_hash": fb_hash,
                         "log_present": present, "log_delta": delta,
                         "poll_s": poll, "overhead_pct": overhead})
        _sleep(max(0.0, cadence_s - poll))
    summary = samples.summary()
    summary["qga"] = {"ok": sum(1 for r in readings if r["qga_state"] == "ok"),
                      "unknown": sum(1 for r in readings if r["qga_state"] != "ok")}
    summary["fb_hash"] = {"frames": len(readings),
                          "distinct": len({r["fb_hash"] for r in readings})}
    (out / "metrics.json").write_text(
        json.dumps(summary, indent=2, sort_keys=True))
    (out / "readings.json").write_text(
        json.dumps(readings, indent=2, sort_keys=True))
    return {"dir": str(out), "readings": readings, "summary": summary}


def main(argv=None) -> int:
    """CLI: python -m harness.live.watchdog_run --iterations 12 --cadence 5
    --out runs/wd-01 [--active|--idle sets the cadence]."""
    import argparse
    ap = argparse.ArgumentParser(description="Watchdog live loop (Session 3)")
    ap.add_argument("--iterations", type=int, default=12)
    ap.add_argument("--cadence", type=float, default=None)
    ap.add_argument("--out", required=True)
    ap.add_argument("--domain", default="ubuntu-2204-stage0")
    ap.add_argument("--uri", default="qemu:///system")
    ap.add_argument("--ssh-hop", action="store_true",
                    help="route control through the ssh hop; screenshots pull "
                         "two legs (host capture + scp) via live/shots.py")
    mode = ap.add_mutually_exclusive_group()
    mode.add_argument("--active", action="store_true")
    mode.add_argument("--idle", action="store_true")
    args = ap.parse_args(argv)
    if args.cadence is None:
        cadence = ACTIVE_CADENCE_S if args.active or not args.idle else IDLE_CADENCE_S
    else:
        cadence = args.cadence
    prefix = None
    shot_fn = None
    if args.ssh_hop:
        from harness.live.shots import pull_screenshot
        from harness.live.virsh_argv import prefix_for
        from harness.vms.pool import parse_libvirt_uri
        prefix = prefix_for(args.uri, ssh_hop=True)
        info = parse_libvirt_uri(args.uri)
        target = f"{info['user']}@{info['host']}" if info["user"] else info["host"]
        ssh_prefix = ["ssh", "-o", "BatchMode=yes", "-o", "ConnectTimeout=10",
                      "-o", "StrictHostKeyChecking=accept-new", target]

        def run(cmd):
            p = subprocess.run(cmd, capture_output=True, text=True, timeout=120)
            return p.returncode, p.stdout, p.stderr

        def fetch(tgt, remote, local):
            subprocess.run(["scp", "-o", "BatchMode=yes",
                            f"{tgt}:{remote}", local],
                           check=True, capture_output=True, timeout=180)

        def shot_fn(seq, path, _t=target):
            pull_screenshot(virsh_prefix=prefix, ssh_prefix=ssh_prefix,
                            ssh_target=_t, domain=args.domain,
                            remote_path=f"/tmp/wd-{seq:02d}.ppm",
                            local_path=path, run=run, fetch=fetch)
    res = run_watchdog(args.iterations, cadence, args.out,
                       domain=args.domain, uri=args.uri,
                       shot_fn=shot_fn, virsh_prefix=prefix)
    summ = res["summary"]
    print(f"iters={args.iterations} cadence={cadence}s "
          f"overhead_p95={summ['watchdog.overhead_pct']['p95']:.2f}% "
          f"qga_ok={summ['qga']['ok']} fb_distinct={summ['fb_hash']['distinct']}")
    return 0


if __name__ == "__main__":
    raise SystemExit(main())
