"""Screenshot battery (Session 2 item 2): N captures -> latency, bytes, noise.

Per capture: latency + byte size into Samples. Post-battery: consecutive-pair
SSIM across all frames (the nothing-changed distribution that calibrates
NO_VISUAL_CHANGE) and the watchdog fb-hash per frame (Session-3 idle false
deltas, measured here for zero extra live time). Writes metrics JSON.
"""
from __future__ import annotations

import json
import subprocess
import time
from pathlib import Path

import numpy as np

from harness.gates.gate2_ssim_ocr import ssim_gray, to_gray
from harness.measurements import Samples
from harness.watchdog.checks import downscale_grayscale_hash


def _load_gray(path: str | Path) -> np.ndarray:
    from PIL import Image
    return np.asarray(Image.open(path).convert("L"))


def _raw_rgb(path: str | Path) -> tuple[bytes, int, int]:
    from PIL import Image
    im = Image.open(path).convert("RGB")
    return im.tobytes(), im.width, im.height


def default_capture(domain: str, uri: str, path: str) -> None:
    subprocess.run(["virsh", "-c", uri, "screenshot", domain, path],
                   check=True, capture_output=True, timeout=120)


def run_battery(n: int, out_dir: str | Path, domain: str = "ubuntu-2204-stage0",
                uri: str = "qemu:///system",
                capture_fn=None, time_fn=None, interval_s: float = 0.0) -> dict:
    """Capture n frames, analyze, write metrics.json. Returns paths + summary."""
    _clock = time_fn or time.monotonic
    out = Path(out_dir)
    out.mkdir(parents=True, exist_ok=True)
    shots = [out / f"shot-{i:02d}.ppm" for i in range(n)]
    samples = Samples()
    for i, path in enumerate(shots):
        t0 = _clock()
        if capture_fn is not None:
            capture_fn(i, str(path))
        else:
            default_capture(domain, uri, str(path))
        dt = _clock() - t0
        size = path.stat().st_size
        samples.add("screenshot.latency_s", dt, unit="s")
        samples.add("screenshot.bytes", size, unit="B")
        if interval_s > 0 and i < n - 1:
            time.sleep(interval_s)
    # pairwise (consecutive) SSIM: the noise floor
    ssims: list[float] = []
    for a, b in zip(shots, shots[1:]):
        ssims.append(ssim_gray(_load_gray(a), _load_gray(b)))
        samples.add("screenshot.pair_ssim", ssims[-1])
    # watchdog fb-hash stability over the same frames
    hashes = []
    for path in shots:
        raw, w, h = _raw_rgb(path)
        hashes.append(downscale_grayscale_hash(raw, w, h))
    summary = samples.summary()
    summary["fb_hash"] = {"frames": n, "distinct": len(set(hashes)),
                          "stable": len(set(hashes)) == 1}
    summary["noise_floor"] = {"pairs": len(ssims),
                              "min_ssim": min(ssims) if ssims else 1.0}
    (out / "metrics.json").write_text(
        json.dumps(summary, indent=2, sort_keys=True))
    return {"dir": str(out), "shots": [str(p) for p in shots],
            "summary": summary}


def main(argv=None, capture_fn=None) -> int:
    """CLI: python -m harness.live.battery --n 30 --out runs/battery-01."""
    import argparse
    ap = argparse.ArgumentParser(description="Screenshot battery (Session 2)")
    ap.add_argument("--n", type=int, default=30)
    ap.add_argument("--out", required=True)
    ap.add_argument("--domain", default="ubuntu-2204-stage0")
    ap.add_argument("--uri", default="qemu:///system")
    ap.add_argument("--interval", type=float, default=0.0,
                    help="seconds between captures (span a clock tick)")
    args = ap.parse_args(argv)
    res = run_battery(args.n, args.out, domain=args.domain, uri=args.uri,
                      capture_fn=capture_fn, interval_s=args.interval)
    summ = res["summary"]
    print(f"frames={summ['fb_hash']['frames']} "
          f"latency_p50={summ['screenshot.latency_s']['p50']:.3f}s "
          f"latency_p95={summ['screenshot.latency_s']['p95']:.3f}s "
          f"bytes_mean={summ['screenshot.bytes']['mean']:.0f} "
          f"noise_min_ssim={summ['noise_floor']['min_ssim']:.5f} "
          f"fb_stable={summ['fb_hash']['stable']}")
    return 0


if __name__ == "__main__":
    raise SystemExit(main())
