"""QGA put-file with write→md5→retry-once→fail-loud (Session-1 finding).

QEMU 6.2's guest-file-write ACKed writes it never persisted (md5-verified
empty on read-back). An acknowledgment is a hypothesis until the effect is
observed: every put-file verifies the remote content hash, retries once on
mismatch or transport error, then fails LOUD — never silently delivers a
truncated file into the type() long-text path (§8).
"""
from __future__ import annotations

import hashlib
from dataclasses import dataclass
from typing import Callable


class PutFileError(Exception):
    """Raised after the single retry is exhausted: loud, with attempt count."""


@dataclass
class PutResult:
    ok: bool
    bytes_written: int
    attempts: int
    md5: str


def _md5(data: bytes) -> str:
    return hashlib.md5(data).hexdigest()


def put_file(data: bytes | str,
             write_fn: Callable[[bytes], None],
             md5_fn: Callable[[], str],
             max_attempts: int = 2) -> PutResult:
    """Write `data` via QGA and verify by remote md5.

    write_fn: delivers bytes (raises on transport failure).
    md5_fn: returns hex md5 of the remote file (raises on failure).
    Retries once total on write error or hash mismatch, then PutFileError.
    """
    raw = data.encode("utf-8") if isinstance(data, str) else bytes(data)
    want = _md5(raw)
    last_error: Exception | None = None
    for attempt in range(1, max_attempts + 1):
        try:
            write_fn(raw)
            got = md5_fn()
        except Exception as e:  # transport failure: one retry, then loud
            last_error = e
            continue
        if got.lower() == want:
            return PutResult(True, len(raw), attempt, want)
        last_error = ValueError(f"md5 mismatch: remote {got} != local {want}")
    raise PutFileError(f"put-file failed after {max_attempts} attempts: {last_error}")
