"""Additional tests for capture/motion/frames helpers."""
from __future__ import annotations

import hashlib

from harness.walkthrough import (
    CapturedFrame,
    capture_frame,
    decode_base64_frame,
    make_capture_from_bytes,
    default_motion,
    make_sweep_motion,
    load_reference_frames,
    inventory,
    sha256_file,
)
from harness.walkthrough.capture import _sniff_extended


def test_sniff_png():
    assert _sniff_extended(b"\x89PNG\r\n\x1a\n") == "png"


def test_sniff_jpeg():
    assert _sniff_extended(b"\xff\xd8\xff\xe0") == "jpeg"


def test_capture_frame_roundtrip():
    payload = b"\x89PNG\r\n\x1a\n" + b"\x00" * 4
    src = make_capture_from_bytes([payload])
    cf = capture_frame(src)
    assert cf.fmt == "png"
    assert cf.sha256 == hashlib.sha256(payload).hexdigest()
    assert cf.is_valid


def test_decode_base64():
    import base64
    raw = b"hello"
    assert decode_base64_frame(base64.b64encode(raw).decode()) == raw


def test_motion_default_emits():
    calls = []

    def fn(evt):
        calls.append(evt)

    emit = default_motion(fn)
    evt = emit(x=5, y=7, kind="nudge")
    assert evt.x == 5 and evt.y == 7 and evt.kind == "nudge"
    assert len(calls) == 1


def test_make_sweep_motion():
    swept = {"n": 0}

    def sweep():
        swept["n"] += 1

    fn = make_sweep_motion(sweep)
    fn()
    assert swept["n"] == 1


def test_inventory_empty_for_missing(tmp_path):
    assert inventory(tmp_path / "nope") == {}


def test_load_reference_frames_empty(tmp_path):
    assert load_reference_frames(tmp_path / "nope") == {}


def test_load_reference_frames_groups(tmp_path):
    d = tmp_path / "refs" / "S6_menu"
    d.mkdir(parents=True)
    from PIL import Image
    Image.new("RGB", (4, 4), (10, 20, 30)).save(d / "a.png")
    # a non-image file is ignored
    (d / "note.txt").write_text("x")
    inv = inventory(tmp_path / "refs")
    assert inv == {"S6_menu": 1}
    frames = load_reference_frames(tmp_path / "refs")
    assert "S6_menu" in frames
    assert len(frames["S6_menu"]) == 1


def test_sha256_file(tmp_path):
    p = tmp_path / "x.bin"
    p.write_bytes(b"abc")
    assert sha256_file(p) == hashlib.sha256(b"abc").hexdigest()
