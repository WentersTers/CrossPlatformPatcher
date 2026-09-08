"""Tier-1 event schema (intent channel). JSONL, sequence-numbered, env-gated.

`seq` is load-bearing: gaps mean dropped writes and the consumer must mark
telemetry UNRELIABLE rather than treating event-absence as evidence. A full
disk mid-run reads as "state machine stopped" without this.
"""
from __future__ import annotations

import json
from datetime import datetime, timezone


class TelemetrySchemaError(ValueError):
    pass


KNOWN_EVENTS = frozenset({
    "heartbeat", "state_change", "animation_start", "animation_end",
    "command_exec", "speak_start", "speak_end", "vosk_result", "error",
})

# per-event required fields beyond the envelope
REQUIRED_FIELDS: dict[str, tuple[str, ...]] = {
    "heartbeat": ("loop",),
    "state_change": ("from", "to"),
    "animation_start": ("name", "state"),
    "animation_end": ("name", "state"),
    "command_exec": ("cmd", "status"),
    "speak_start": ("text",),
    "speak_end": (),
    "vosk_result": ("text",),
    "error": ("source", "detail"),
}

SCHEMA_VERSION = 1


def parse_ts(ts: str) -> datetime:
    try:
        dt = datetime.fromisoformat(ts.replace("Z", "+00:00"))
    except (ValueError, AttributeError) as e:
        raise TelemetrySchemaError(f"bad ts {ts!r}: {e}") from e
    if dt.tzinfo is None:
        dt = dt.replace(tzinfo=timezone.utc)
    return dt


def validate_event(obj: dict) -> dict:
    """Schema-validate one event object. Returns normalized copy with `_dt`.

    Content fields (text/detail/arg/...) are DATA — kept verbatim, never
    interpreted. Unknown event names and missing fields raise.
    """
    if not isinstance(obj, dict):
        raise TelemetrySchemaError("event must be a JSON object")
    if obj.get("v") != SCHEMA_VERSION:
        raise TelemetrySchemaError(f"unsupported schema v={obj.get('v')!r}")
    if not isinstance(obj.get("seq"), int) or obj["seq"] < 0:
        raise TelemetrySchemaError(f"bad seq {obj.get('seq')!r}")
    if "ts" not in obj or "event" not in obj:
        raise TelemetrySchemaError("event missing ts/event")
    name = obj["event"]
    if name not in KNOWN_EVENTS:
        raise TelemetrySchemaError(f"unknown event {name!r}")
    for f in REQUIRED_FIELDS[name]:
        if f not in obj:
            raise TelemetrySchemaError(f"{name} missing {f!r}")
    out = dict(obj)
    out["_dt"] = parse_ts(obj["ts"])
    return out


def parse_line(line: str) -> dict:
    try:
        obj = json.loads(line)
    except json.JSONDecodeError as e:
        raise TelemetrySchemaError(f"not JSONL: {e}") from e
    return validate_event(obj)
