"""OCR run (Session 3): full-desktop pass + scoped passes with latencies.

The oracle shape: full-desktop text (triage only) vs active-window-scoped
text (verdict path). One invocation captures the screenshot once and runs
all passes, recording milliseconds per pass for the cadence budget.
ROIs are x,y,w,h integers, given as --roi x,y,w,h (repeatable).
"""
from __future__ import annotations

import argparse
import json
import subprocess
import time
from pathlib import Path

import numpy as np

from harness.measurements import Samples
from harness.tools.ocr import TesseractBackend, scoped_ocr


def _load(path: str) -> np.ndarray:
    from PIL import Image
    return np.asarray(Image.open(path).convert("RGB"))


def run_ocr(shot_path: str | None, rois: list[tuple[int, int, int, int]],
            domain: str = "ubuntu-2204-stage0", uri: str = "qemu:///system",
            backend: TesseractBackend | None = None,
            scoped_psm: int | None = 6,
            time_fn=None) -> dict:
    _clock = time_fn or time.monotonic
    be = backend or TesseractBackend()
    scoped_be = backend or TesseractBackend(psm=scoped_psm)
    samples = Samples()
    if shot_path is None:
        shot_path = "/tmp/ocr-shot.ppm"
        subprocess.run(["virsh", "-c", uri, "screenshot", domain, shot_path],
                       check=True, capture_output=True, timeout=120)
    img = _load(shot_path)
    t0 = _clock()
    full = be.ocr(img, None)
    full_ms = (_clock() - t0) * 1000.0
    samples.add("ocr.full_ms", full_ms, unit="ms")
    scoped = []
    for roi in rois:
        t1 = _clock()
        text = scoped_ocr(img, roi, scoped_be)
        ms = (_clock() - t1) * 1000.0
        samples.add("ocr.scoped_ms", ms, unit="ms")
        scoped.append({"roi": list(roi), "text": text, "ms": ms})
    return {"shot": str(shot_path),
            "size": [int(img.shape[1]), int(img.shape[0])],
            "full_text": full, "full_ms": full_ms, "scoped": scoped,
            "metrics": samples.summary()}


def main(argv=None, backend=None) -> int:
    ap = argparse.ArgumentParser(description="OCR run (Session 3)")
    ap.add_argument("--shot", default=None)
    ap.add_argument("--out", required=True)
    ap.add_argument("--roi", action="append", default=[],
                    help="x,y,w,h (repeatable)")
    ap.add_argument("--domain", default="ubuntu-2204-stage0")
    ap.add_argument("--uri", default="qemu:///system")
    ap.add_argument("--psm", type=int, default=6,
                    help="tesseract PSM for scoped passes (6 = uniform block)")
    args = ap.parse_args(argv)
    rois = [tuple(int(v) for v in r.split(",")) for r in args.roi]  # noqa
    res = run_ocr(args.shot, rois, domain=args.domain, uri=args.uri,
                  backend=backend, scoped_psm=args.psm)
    Path(args.out).parent.mkdir(parents=True, exist_ok=True)
    Path(args.out).write_text(json.dumps(res, indent=2, sort_keys=True))
    print(f"full_ms={res['full_ms']:.0f} scoped={len(res['scoped'])} "
          f"chars={len(res['full_text'])}")
    return 0


if __name__ == "__main__":
    raise SystemExit(main())
