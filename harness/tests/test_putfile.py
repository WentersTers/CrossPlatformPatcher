"""put-file: acked-but-empty is caught by the md5 gate, not trusted."""
import hashlib

import pytest

from harness.tools.putfile import PutFileError, put_file


def _md5(b: bytes) -> str:
    return hashlib.md5(b).hexdigest()


def test_clean_write_single_attempt():
    calls = []

    def write(b):
        calls.append(b)

    r = put_file("héllo " * 100, write, lambda: _md5(("héllo " * 100).encode()))
    assert r.ok and r.attempts == 1 and r.bytes_written > 200


def test_mismatch_retries_once_then_succeeds():
    remote = {"data": b""}
    calls = []

    def write(b):
        calls.append(b)
        remote["data"] = b

    # md5 reads a lagging store on attempt 1 (stale read), then current
    lag = {"n": 0}
    def lagging_md5():
        lag["n"] += 1
        return _md5(b"garbage") if lag["n"] == 1 else _md5(remote["data"])

    r = put_file(b"real-bytes", write, lagging_md5)
    assert r.ok and r.attempts == 2 and len(calls) == 2


def test_session1_scenario_truncated_delivery_fails_loud():
    """The live finding: QGA ACKs a write it never persisted. md5 of the
    remote empty file never matches -> retry -> PutFileError, never silent."""
    def write(b):
        pass  # ACKed...

    def md5():
        return _md5(b"")  # ...but nothing persisted

    with pytest.raises(PutFileError, match="2 attempts"):
        put_file(b"payload", write, md5)
    # bytes also accepted (type() path encodes first, same gate)
    with pytest.raises(PutFileError):
        put_file("payload", write, md5)


def test_transport_errors_retry_once():
    calls = {"n": 0}

    def flaky_write(b):
        calls["n"] += 1
        if calls["n"] == 1:
            raise ConnectionError("ssh reset")

    r = put_file(b"data", flaky_write, lambda: _md5(b"data"))
    assert r.ok and r.attempts == 2

    def dead_write(b):
        raise ConnectionError("down")

    with pytest.raises(PutFileError, match="down"):
        put_file(b"data", dead_write, lambda: "00")
