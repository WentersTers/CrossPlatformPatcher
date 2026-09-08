"""Hard-evidence capture (runbook table): samples -> p50/p95 summary JSON.

Sessions 2-3 produce real constants (screenshot latency, click SSIM deltas,
watchdog overhead). This helper turns raw samples into artifact-grade
summaries so seed guesses get replaced by pinned, measured values.
Percentiles: nearest-rank (deterministic, no numpy needed).
"""
from __future__ import annotations

import json
import math
from pathlib import Path


class Samples:
    def __init__(self):
        self._data: dict[str, dict] = {}  # name -> {"values": [], "unit": str}

    def add(self, name: str, value: float, unit: str = "") -> None:
        slot = self._data.setdefault(name, {"values": [], "unit": unit})
        slot["values"].append(float(value))
        if unit:
            slot["unit"] = unit

    @staticmethod
    def _pct(sorted_vals: list[float], pct: float) -> float:
        k = max(1, math.ceil(pct / 100.0 * len(sorted_vals)))
        return sorted_vals[k - 1]

    def summary(self) -> dict:
        out: dict = {}
        for name, slot in self._data.items():
            vals = sorted(slot["values"])
            if not vals:
                continue
            out[name] = {"n": len(vals), "unit": slot["unit"],
                         "min": vals[0], "max": vals[-1],
                         "mean": sum(vals) / len(vals),
                         "p50": self._pct(vals, 50), "p95": self._pct(vals, 95)}
        return out

    def write_json(self, path: str | Path) -> dict:
        summary = self.summary()
        p = Path(path)
        p.parent.mkdir(parents=True, exist_ok=True)
        p.write_text(json.dumps(summary, indent=2, sort_keys=True),
                     encoding="utf-8")
        return summary
