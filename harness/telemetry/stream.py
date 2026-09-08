"""Telemetry consumer stream: seq-gap reliability, heartbeat, claimed state.

Event stream is cheap text — formatted lines go in the worker's NEVER-FOLDED
text history (§4), ideal context. `wait_for_event` is strictly stronger than
`wait_for` text-in-screenshot (§8).
"""
from __future__ import annotations

import asyncio
from datetime import datetime, timezone
from typing import Any, Awaitable, Callable

from harness.telemetry.schema import TelemetrySchemaError, parse_line, validate_event


def format_log_line(event: dict[str, Any]) -> str:
    """One cheap text line per event for the never-folded history."""
    seq, name = event.get("seq"), event.get("event")
    if name == "state_change":
        return (f"[telemetry #{seq} state_change {event.get('from')}->"
                f"{event.get('to')} trigger={event.get('trigger', '')}]")
    if name == "heartbeat":
        return f"[telemetry #{seq} heartbeat loop={event.get('loop')}]"
    return f"[telemetry #{seq} {name}]"


class TelemetryStream:
    def __init__(self, now_fn: Callable[[], datetime] | None = None):
        self._now = now_fn or (lambda: datetime.now(timezone.utc))
        self.events: list[dict[str, Any]] = []
        self._seen_seq: set[int] = set()
        self._max_seq: int | None = None
        self.gap_count = 0
        self.invalid_lines = 0

    # -- ingest --
    def ingest_line(self, line: str) -> dict[str, Any] | None:
        try:
            return self.ingest(parse_line(line))
        except TelemetrySchemaError:
            self.invalid_lines += 1
            return None

    def ingest(self, event: dict[str, Any]) -> dict[str, Any]:
        event = validate_event(dict(event))
        seq = event["seq"]
        if seq not in self._seen_seq:
            self._seen_seq.add(seq)
            if self._max_seq is None:
                self._max_seq = seq  # join point: no gap flagged for pre-join
            elif seq > self._max_seq:
                if seq > self._max_seq + 1:
                    self.gap_count += seq - self._max_seq - 1
                self._max_seq = seq
            # late/duplicate seqs: stored once, never counted as gaps
            self.events.append(event)
            self.events.sort(key=lambda e: e["seq"])
        return event

    @property
    def reliable(self) -> bool:
        """No seq gaps AND no invalid lines. Unreliable telemetry must never
        launder event-absence into evidence."""
        return self.gap_count == 0 and self.invalid_lines == 0

    # -- liveness / intent --
    def last_heartbeat(self) -> dict[str, Any] | None:
        for e in reversed(self.events):
            if e["event"] == "heartbeat":
                return e
        return None

    def heartbeat_age_s(self, now: datetime | None = None) -> float | None:
        hb = self.last_heartbeat()
        if hb is None:
            return None
        return ((now or self._now()) - hb["_dt"]).total_seconds()

    def claimed_state(self) -> tuple[str | None, int | None]:
        """Latest state_change target (intent), or (None, None)."""
        for e in reversed(self.events):
            if e["event"] == "state_change":
                return e["to"], e["seq"]
        return None, None

    def read_telemetry(self, since_seq: int) -> list[dict[str, Any]]:
        return [e for e in self.events if e["seq"] > since_seq]

    # -- blocking wait --
    async def wait_for_event(
        self, event: str, timeout_s: float,
        predicate: Callable[[dict[str, Any]], bool] | None = None,
        sleep_fn: Callable[[float], Awaitable[None]] | None = None,
        time_fn: Callable[[], float] | None = None,
    ) -> dict[str, Any] | None:
        """Blocking wait for a telemetry event. Returns the event or None on
        timeout. Strictly stronger than `wait_for` text-in-screenshot."""
        import time as _time
        _sleep = sleep_fn or asyncio.sleep
        _clock = time_fn or _time.monotonic
        pred = predicate or (lambda e: True)
        start = _clock()
        while True:
            for e in self.events:
                if e["event"] == event and pred(e):
                    return e
            if _clock() - start >= timeout_s:
                return None
            await _sleep(0.05)
