"""SQLite-backed durable checkpointer (D5)."""
from __future__ import annotations

import json
import sqlite3
import threading
from datetime import datetime, timezone
from pathlib import Path
from typing import Any, Optional


def _utcnow() -> str:
    return datetime.now(timezone.utc).isoformat()


class Checkpointer:
    """Durable LangGraph-style checkpointer backed by SQLite."""

    def __init__(self, db_path: str | Path):
        self.db_path = Path(db_path)
        self.db_path.parent.mkdir(parents=True, exist_ok=True)
        self._lock = threading.Lock()
        self._init_db()

    def _connect(self) -> sqlite3.Connection:
        conn = sqlite3.connect(str(self.db_path))
        conn.row_factory = sqlite3.Row
        return conn

    def _init_db(self) -> None:
        with self._lock, self._connect() as conn:
            conn.execute(
                """CREATE TABLE IF NOT EXISTS checkpoints (
                    trace_id TEXT PRIMARY KEY,
                    state_json TEXT NOT NULL,
                    updated_at TEXT NOT NULL
                )"""
            )
            conn.execute(
                """CREATE TABLE IF NOT EXISTS events (
                    id INTEGER PRIMARY KEY AUTOINCREMENT,
                    trace_id TEXT NOT NULL,
                    seq INTEGER NOT NULL,
                    event_json TEXT NOT NULL,
                    ts TEXT NOT NULL,
                    UNIQUE(trace_id, seq)
                )"""
            )
            conn.execute(
                "CREATE INDEX IF NOT EXISTS idx_events_trace ON events(trace_id, seq)"
            )

    def save_checkpoint(self, trace_id: str, state: dict[str, Any]) -> None:
        if not trace_id:
            raise ValueError("trace_id is required")
        payload = json.dumps(state, sort_keys=True)
        with self._lock, self._connect() as conn:
            conn.execute(
                """INSERT INTO checkpoints(trace_id, state_json, updated_at)
                   VALUES(?,?,?)
                   ON CONFLICT(trace_id) DO UPDATE SET
                     state_json=excluded.state_json, updated_at=excluded.updated_at""",
                (trace_id, payload, _utcnow()),
            )

    def load_checkpoint(self, trace_id: str) -> Optional[dict[str, Any]]:
        with self._lock, self._connect() as conn:
            row = conn.execute(
                "SELECT state_json FROM checkpoints WHERE trace_id=?", (trace_id,)
            ).fetchone()
        if row is None:
            return None
        return json.loads(row["state_json"])

    def append_event(self, trace_id: str, event: dict[str, Any]) -> int:
        """Append event; every event carries trace_id. Returns seq number."""
        if not trace_id:
            raise ValueError("trace_id is required")
        evt = dict(event)
        evt.setdefault("trace_id", trace_id)
        if evt.get("trace_id") != trace_id:
            raise ValueError("event trace_id mismatch")
        evt.setdefault("ts", _utcnow())
        with self._lock, self._connect() as conn:
            row = conn.execute(
                "SELECT COALESCE(MAX(seq), -1) AS m FROM events WHERE trace_id=?",
                (trace_id,),
            ).fetchone()
            seq = int(row["m"]) + 1
            conn.execute(
                "INSERT INTO events(trace_id, seq, event_json, ts) VALUES(?,?,?,?)",
                (trace_id, seq, json.dumps(evt, sort_keys=True), evt["ts"]),
            )
            return seq

    def get_events(self, trace_id: str) -> list[dict[str, Any]]:
        with self._lock, self._connect() as conn:
            rows = conn.execute(
                "SELECT event_json FROM events WHERE trace_id=? ORDER BY seq ASC",
                (trace_id,),
            ).fetchall()
        return [json.loads(r["event_json"]) for r in rows]

    def completed_action_ids(self, trace_id: str) -> set[str]:
        """Idempotency keys of already-executed tool calls (resume-after)."""
        return {e["action_id"] for e in self.get_events(trace_id)
                if isinstance(e.get("action_id"), str)}

    def append_event_idempotent(self, trace_id: str, action_id: str,
                                event: dict[str, Any]) -> tuple[int, bool]:
        """Claim-and-record a tool call exactly once.

        Returns (seq, replayed). If action_id already recorded, returns the
        original seq with replayed=True and does NOT re-insert — so a resume
        never re-executes the in-flight QMP click (double-click trap).
        Driver rule: check completed_action_ids() after resume and skip the
        tool call, reusing the recorded result event.
        """
        if not action_id:
            raise ValueError("action_id is required")
        with self._lock, self._connect() as conn:
            rows = conn.execute(
                "SELECT seq, event_json FROM events WHERE trace_id=? ORDER BY seq ASC",
                (trace_id,),
            ).fetchall()
            for r in rows:
                try:
                    e = json.loads(r["event_json"])
                except ValueError:
                    continue
                if e.get("action_id") == action_id:
                    return int(r["seq"]), True
            row = conn.execute(
                "SELECT COALESCE(MAX(seq), -1) AS m FROM events WHERE trace_id=?",
                (trace_id,),
            ).fetchone()
            seq = int(row["m"]) + 1
            evt = dict(event)
            evt["action_id"] = action_id
            evt.setdefault("trace_id", trace_id)
            if evt.get("trace_id") != trace_id:
                raise ValueError("event trace_id mismatch")
            evt.setdefault("ts", _utcnow())
            conn.execute(
                "INSERT INTO events(trace_id, seq, event_json, ts) VALUES(?,?,?,?)",
                (trace_id, seq, json.dumps(evt, sort_keys=True), evt["ts"]),
            )
            return seq, False

    def list_traces(self) -> list[str]:
        with self._lock, self._connect() as conn:
            rows = conn.execute("SELECT trace_id FROM checkpoints ORDER BY trace_id").fetchall()
        return [r["trace_id"] for r in rows]
