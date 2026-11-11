"""Evidence emission for the walkthrough engine (REV-035).

Every captured frame produces one record in states.jsonl carrying: timestamp,
state, confidence, method, frame SHA-256, guest-subject SHA-256, model version
(when the learned tier is used), and an abstain flag. No verdicts are emitted.
"""
from __future__ import annotations

import hashlib
import json
import time
from dataclasses import asdict, dataclass, field
from pathlib import Path
from typing import Any


@dataclass
class EvidenceRecord:
    ts: float
    state: str
    confidence: float
    method: str
    frame_sha256: str
    guest_subject_sha256: str
    aborted: bool = False
    abstain: bool = False
    model_version: str | None = None
    extra: dict[str, Any] = field(default_factory=dict)

    def to_jsonl(self) -> str:
        d = asdict(self)
        # drop empty extras to keep lines tight
        if not d["extra"]:
            del d["extra"]
        if d["model_version"] is None:
            del d["model_version"]
        return json.dumps(d, separators=(",", ":"))


def frame_sha256(payload: bytes) -> str:
    return hashlib.sha256(payload).hexdigest()


def guest_subject_sha256(payload: bytes) -> str:
    return hashlib.sha256(payload).hexdigest()


class EvidenceWriter:
    """Append-only writer for states.jsonl."""

    def __init__(self, path: str | Path):
        self.path = Path(path)
        self.path.parent.mkdir(parents=True, exist_ok=True)

    def write(self, record: EvidenceRecord) -> None:
        with self.path.open("a", encoding="utf-8") as fh:
            fh.write(record.to_jsonl() + "\n")

    def read_all(self) -> list[dict[str, Any]]:
        if not self.path.exists():
            return []
        out: list[dict[str, Any]] = []
        for line in self.path.read_text(encoding="utf-8").splitlines():
            line = line.strip()
            if line:
                out.append(json.loads(line))
        return out
