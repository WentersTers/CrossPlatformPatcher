"""Transcript comparison: Whisper reference vs Vosk output, word-level tolerance."""
from __future__ import annotations

from harness.gates.gate1_deterministic import transcript_match


def compare_transcripts(reference: str, hypothesis: str,
                        tolerance_wer: float = 0.2) -> dict:
    ok, wer = transcript_match(reference, hypothesis, tolerance_wer)
    return {"ok": ok, "wer": wer, "tolerance": tolerance_wer}
