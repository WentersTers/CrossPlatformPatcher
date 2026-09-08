"""Tier B — VLM with enum-only state rubric, provisional per D9.

Constraints: enum-only output (schema-validated, no invented states, no hedging —
'unknown' if confidence < 0.5); must cite specific visual features; pet ROI in prompt.
VLMs can assert re-examination without re-seeing (VS-BENCH) — hence provisional.

Cost guards live here as helpers: 500ms cache + hard cap 5 VLM calls per assertion.
Cache is keyed on FRAME CONTENT hash (+ ROI): a time-only cache keyed on VM alone
returns stale states when the pet moves within the window (spec-trap fix).
"""
from __future__ import annotations

import hashlib
import time
from dataclasses import dataclass
from typing import Callable

import numpy as np

ALLOWED_STATES = ("idle", "listening", "thinking", "speaking", "acting", "error", "unknown")


@dataclass
class VlmStateResult:
    state: str
    confidence: float
    visual_evidence: str = ""
    is_animated_state: bool = False
    observed_motion: str = ""
    method: str = "vlm"


def validate_result(state: str, confidence: float, allowed: tuple[str, ...] = ALLOWED_STATES) -> str:
    """Enum-only: unknown on low confidence or invented state."""
    if state not in allowed:
        return "unknown"
    if confidence < 0.5:
        return "unknown"
    return state


def build_rubric_prompt(pet_roi: tuple[int, int, int, int] | None) -> str:
    return (
        "Classify the pet state. Respond JSON ONLY: "
        '{"state": "<idle|listening|thinking|speaking|acting|error|unknown>", '
        '"confidence": 0.0-1.0, "visual_evidence": "<specific pose/indicator cited>", '
        '"is_animated_state": true/false, "if_animated": "<observed motion>"}. '
        "Rules: enum value only; 'unknown' if confidence < 0.5; cite specific visual features. "
        f"Pet ROI: {pet_roi}."
    )


class VlmClassifier:
    """Base: override classify_impl. Counts calls for the 5-call hard cap."""

    def __init__(self, allowed_states: tuple[str, ...] = ALLOWED_STATES,
                 max_calls_per_assertion: int = 5, cache_ms: int = 500,
                 time_fn: Callable[[], float] | None = None):
        self.allowed_states = allowed_states
        self.max_calls = max_calls_per_assertion
        self.cache_ms = cache_ms
        self._calls = 0
        self._last_ts: float | None = None
        self._last_result: VlmStateResult | None = None
        self._last_key: bytes | None = None
        self._time = time_fn or time.monotonic

    def reset_budget(self) -> None:
        self._calls = 0
        self._last_ts = None
        self._last_result = None
        self._last_key = None

    @property
    def calls(self) -> int:
        return self._calls

    @staticmethod
    def _content_key(image: np.ndarray,
                     pet_roi: tuple[int, int, int, int] | None) -> bytes:
        h = hashlib.md5()
        h.update(np.ascontiguousarray(image).tobytes())
        h.update(repr(pet_roi).encode())
        return h.digest()

    def classify(self, image: np.ndarray,
                 pet_roi: tuple[int, int, int, int] | None) -> VlmStateResult | None:
        """Returns None when budget exhausted (caller fails gracefully as uncertain)."""
        now = self._time()
        key = self._content_key(image, pet_roi)
        if (self._last_ts is not None and self._last_result is not None
                and self._last_key == key
                and (now - self._last_ts) * 1000 < self.cache_ms):
            return self._last_result
        if self._calls >= self.max_calls:
            return None  # hard cap: return uncertain, no token burn
        self._calls += 1
        raw = self.classify_impl(image, pet_roi)
        state = validate_result(raw.state, raw.confidence, self.allowed_states)
        out = VlmStateResult(state, raw.confidence, raw.visual_evidence,
                             raw.is_animated_state, raw.observed_motion)
        self._last_ts = now
        self._last_result = out
        self._last_key = key
        return out

    def classify_impl(self, image: np.ndarray,
                      pet_roi: tuple[int, int, int, int] | None) -> VlmStateResult:
        raise NotImplementedError("inject a stub or model-backed subclass in production")


class StubVlmClassifier(VlmClassifier):
    """Deterministic stub for tests/evals: scripted state per call."""

    def __init__(self, script: list[tuple[str, float]] | None = None, **kw):
        super().__init__(**kw)
        self.script = list(script or [("idle", 0.9)])
        self._idx = 0

    def classify_impl(self, image, pet_roi) -> VlmStateResult:
        s, c = self.script[min(self._idx, len(self.script) - 1)]
        self._idx += 1
        return VlmStateResult(s, c, visual_evidence=f"stub:{s}",
                              is_animated_state=(s in ("listening", "speaking")))
