"""Storm-row confirmation probe (Session 5): sustained ambiguous source live.

Polls N frames against Tier A (seed golden), tracking consecutive-ambiguous
runs and fb-frozen state via the rolling set. Reports BOTH verdicts:
- current logic: >=5 consecutive ambiguous -> storm (vision-quality label)
- deferred row: persistent-ambiguous AND fb-frozen AND log-delta-zero ->
  wrong-ROI-or-no-pet, storm suppressed.
Log delta is 0 by construction (no app runs during the probe) — documented,
not measured. If the current logic storms on a static textured wallpaper,
the misdiagnosis is confirmed and the row gets implemented against reality.
"""
from __future__ import annotations

import argparse
import json
import subprocess
import time
from dataclasses import dataclass
from pathlib import Path
from typing import Any, Callable

from harness.watchdog.checks import HashWindow, downscale_grayscale_hash

STORM_N = 5


@dataclass
class ProbeFrame:
    state: str
    score: float
    fb_hash: str


def assess(frames: list[ProbeFrame], log_delta: int = 0,
           storm_n: int = STORM_N) -> dict[str, Any]:
    """Pure verdict core (testable): current mislabel vs deferred row."""
    max_run, run = 0, 0
    for f in frames:
        run = run + 1 if f.state in ("ambiguous", "uncertain") else 0
        max_run = max(max_run, run)
    window = HashWindow(k=4)
    frozen_flags = [window.observe(f.fb_hash) for f in frames]
    frozen = bool(frozen_flags[-1]) if frozen_flags else False
    current = ("storm" if max_run >= storm_n else "none",
               "vision-quality" if max_run >= storm_n else "")
    if max_run >= storm_n and frozen and log_delta == 0:
        deferred = ("wrong-ROI-or-no-pet", True)
    else:
        deferred = ("", False)
    return {"frames": len(frames), "max_consecutive_ambiguous": max_run,
            "fb_frozen": frozen, "log_delta": log_delta,
            "current_verdict": current[0], "current_label": current[1],
            "deferred_diagnosis": deferred[0],
            "deferred_suppresses_storm": deferred[1]}


def _seed_matcher():
    import cv2
    import numpy as np

    from harness.gates.gate2_5_state.template_matcher import StateTemplateMatcher
    img = np.zeros((60, 60, 3), dtype=np.uint8)
    cv2.circle(img, (30, 30), 15, (255, 255, 255), 2)
    return StateTemplateMatcher({"idle": [img]},
                                {"idle": {"min_confidence": 0.80}})


def main(argv=None, classify_fn=None, capture_fn=None,
         sleep_fn=None) -> int:
    ap = argparse.ArgumentParser(description="Storm-row probe (Session 5)")
    ap.add_argument("--n", type=int, default=12)
    ap.add_argument("--interval", type=float, default=5.0)
    ap.add_argument("--out", required=True)
    ap.add_argument("--domain", default="ubuntu-2204-stage")
    ap.add_argument("--uri", default="qemu:///system")
    ap.add_argument("--shots-dir", default=None)
    ap.add_argument("--roi", default=None,
                    help="x,y,w,h fixed ROI (else full frame)")
    args = ap.parse_args(argv)
    _sleep = sleep_fn or time.sleep
    roi = tuple(int(v) for v in args.roi.split(",")) if args.roi else None
    matcher = _seed_matcher() if classify_fn is None else None
    out = Path(args.out)
    out.mkdir(parents=True, exist_ok=True)
    frames: list[ProbeFrame] = []
    import numpy as np
    from PIL import Image
    for i in range(args.n):
        if args.shots_dir is not None:
            path = str(Path(args.shots_dir) / f"shot-{i:02d}.ppm")
            img = np.asarray(Image.open(path).convert("RGB"))
        elif capture_fn is not None:
            path = str(out / f"probe-{i:02d}.png")
            capture_fn(i, path)
            img = np.asarray(Image.open(path).convert("RGB"))
        else:
            path = str(out / f"probe-{i:02d}.ppm")
            subprocess.run(["virsh", "-c", args.uri, "screenshot",
                            args.domain, path],
                           check=True, capture_output=True, timeout=120)
            img = np.asarray(Image.open(path).convert("RGB"))
        if classify_fn is not None:
            state, score = classify_fn(img, roi)
        else:
            sub = img
            if roi is not None:
                x, y, w, h = roi
                sub = img[y:y + h, x:x + w]
            m = matcher.classify(sub, None)
            state, score = m.state, float(m.score)
        frames.append(ProbeFrame(state, score,
                                   downscale_grayscale_hash(
                                       img.tobytes(), img.shape[1],
                                       img.shape[0])))
        if i < args.n - 1:
            _sleep(args.interval)
    report = assess(frames)
    (out / "storm-report.json").write_text(
        json.dumps(report, indent=2, sort_keys=True))
    print(f"max_amb_run={report['max_consecutive_ambiguous']} "
          f"frozen={report['fb_frozen']} "
          f"current={report['current_verdict']}/{report['current_label']} "
          f"deferred={report['deferred_diagnosis'] or '-'}")
    return 0


if __name__ == "__main__":
    raise SystemExit(main())
