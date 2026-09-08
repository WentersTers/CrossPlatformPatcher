"""Event-synchronized capture (§6 Gate 2.5): on `state_change`, wait `settle_ms`
from the APP's own reconcile window, then capture.

Blind 2Hz sampling becomes the fallback (no event in timeout). Capturing
after the animation settles mostly eliminates transitional-frame ambiguity
storms — you sample the settled state, not the transition.
"""
from __future__ import annotations

import asyncio
import time
from typing import Any, Awaitable, Callable

SETTLE_FALLBACK_MS = 500


async def capture_after_settle(stream, state: str, timeout_s: float,
                               capture_fn: Callable[[], Any],
                               sleep_fn: Callable[[float], Awaitable[None]] | None = None,
                               time_fn: Callable[[], float] | None = None,
                               settle_fallback_ms: float = SETTLE_FALLBACK_MS) -> dict[str, Any]:
    """Wait for state_change->state, sleep its settle_ms, capture.

    Returns {frame, mode, settle_ms, event_seq}. mode is 'event-sync' or
    'blind' (timeout fallback — transitional ambiguity possible).
    """
    _sleep = sleep_fn or asyncio.sleep
    _clock = time_fn or time.monotonic
    evt = await stream.wait_for_event(
        "state_change", timeout_s,
        predicate=lambda e: e.get("to") == state,
        sleep_fn=_sleep, time_fn=_clock)
    if evt is None:
        return {"frame": capture_fn(), "mode": "blind",
                "settle_ms": settle_fallback_ms, "event_seq": None}
    settle_ms = float(evt.get("settle_ms", settle_fallback_ms))
    await _sleep(settle_ms / 1000.0)
    _ = _clock()
    return {"frame": capture_fn(), "mode": "event-sync",
            "settle_ms": settle_ms, "event_seq": evt["seq"]}
