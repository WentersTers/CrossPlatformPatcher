"""Versioned state.json writer, last-writer-wins (§9)."""
from __future__ import annotations

import json
from datetime import datetime, timezone
from pathlib import Path
from typing import Any


def new_initial_state(vm: str, runtime: str = "native-linux-x64") -> dict[str, Any]:
    return {
        "version": 1,
        "vm": vm,
        "app": {"running": False, "runtime": runtime, "launched_at": None},
        "pet": {"last_observed_state": "unknown", "state_confidence": 0.0,
                "state_method": "none"},
        "audio": {"device": "pulse", "vosk_initialized": False,
                  "env_migration_mode": "full"},
        "runtime_selected": runtime,
        "artifacts": [],
        "last_task": None,
        # telemetry intent lane (§9/§11): app-claimed state + the seq that
        # claimed it; None until the first state_change is ingested.
        "app_claimed_state": None,
    }


def load_state(path: str | Path) -> dict[str, Any]:
    p = Path(path)
    if not p.exists():
        raise FileNotFoundError(str(p))
    return json.loads(p.read_text(encoding="utf-8"))


def _validate(state: dict[str, Any]) -> None:
    if "version" not in state or not isinstance(state["version"], int):
        raise ValueError("state.json must contain integer 'version'")
    if state["version"] < 1:
        raise ValueError("version must be >= 1")


class StaleVersionError(ValueError):
    """Raised in reject mode when expected_version != file version."""


def write_state(path: str | Path, patch: dict[str, Any],
                expected_version: int | None = None,
                on_conflict: str = "lww") -> dict[str, Any]:
    """Apply patch, bump version.

    `version` is a managed field: any `version` key inside `patch` is ignored
    (callers cannot spoof it; previously it was silently overwritten, which
    made the version look enforced while it wasn't).

    - on_conflict="lww" (spec §9 default): concurrent writes resolve by
      last-writer-wins-on-version, version = max(current, expected) + 1.
    - on_conflict="reject": stale expected_version raises StaleVersionError
      and writes nothing — use on critical paths where a lost update must
      be retried, not papered over.
    """
    if on_conflict not in ("lww", "reject"):
        raise ValueError("on_conflict must be 'lww' or 'reject'")
    p = Path(path)
    p.parent.mkdir(parents=True, exist_ok=True)
    patch = {k: v for k, v in patch.items() if k != "version"}
    if p.exists():
        current = json.loads(p.read_text(encoding="utf-8"))
        _validate(current)
        base_version = current["version"]
        if expected_version is not None and base_version != expected_version:
            if on_conflict == "reject":
                raise StaleVersionError(
                    f"stale write: expected v{expected_version}, file is v{base_version}")
            base_version = max(base_version, expected_version)
        merged = dict(current)
        merged.update(patch)
        merged["version"] = base_version + 1
    else:
        merged = dict(patch)
        merged.setdefault("version", 1)
        if not isinstance(merged["version"], int):
            raise ValueError("version must be int")
    _validate(merged)
    p.write_text(json.dumps(merged, indent=2, sort_keys=True), encoding="utf-8")
    return merged
