"""Telemetry worker tools (§8): wait_for_event + read_telemetry.

wait_for_event is blocking and strictly stronger than `wait_for`
text-in-screenshot. Events feed the never-folded text history (§4).
"""
from __future__ import annotations

from typing import Any, Awaitable, Callable


async def wait_for_event(stream, event: str, timeout_s: float,
                         predicate=None,
                         sleep_fn: Callable[[float], Awaitable[None]] | None = None,
                         time_fn: Callable[[], float] | None = None) -> dict[str, Any] | None:
    """Block until a matching telemetry event arrives or timeout. None = timeout."""
    return await stream.wait_for_event(event, timeout_s, predicate,
                                       sleep_fn=sleep_fn, time_fn=time_fn)


def read_telemetry(stream, since_seq: int) -> list[dict[str, Any]]:
    return stream.read_telemetry(since_seq)
