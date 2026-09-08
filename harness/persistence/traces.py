"""Stitched trace queryable by trace_id across manager + worker + gates (Stage 1 exit)."""
from __future__ import annotations

from typing import Any

from harness.persistence.checkpointer import Checkpointer


def stitch_trace(cp: Checkpointer, trace_id: str) -> list[dict[str, Any]]:
    """Return full ordered interaction log for trace_id."""
    events = cp.get_events(trace_id)
    # Already ordered by seq in checkpointer; defensive sort by (seq, ts) if present.
    def _key(e: dict[str, Any]):
        return (e.get("seq", 0), e.get("ts", ""))
    # seq is implicit in storage order; keep storage order (stable).
    return list(events)
