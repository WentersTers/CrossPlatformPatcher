"""Tests for the ISS-046 provision guard (check_materialization.py).

The guard must refuse a void bundle (missing 7nbx materialization ADDs) and
accept a fixed one. These tests exercise the bytes, not a live provision.
"""
from __future__ import annotations

import importlib.util
import sys
from pathlib import Path

import pytest

_MOD_PATH = Path(__file__).resolve().parents[1] / "matrix" / "check_materialization.py"


def _load_module():
    spec = importlib.util.spec_from_file_location("check_materialization", _MOD_PATH)
    mod = importlib.util.module_from_spec(spec)
    sys.modules["check_materialization"] = mod
    spec.loader.exec_module(mod)
    return mod


guard = _load_module()


def test_add_names_are_the_pinned_pairing():
    assert guard.ADD_DIALOG == '7nbx}a5?U:!^=d#]"~1pIQ;X.resources'
    assert guard.ADD_TYPE == '7nbx}a5?U:!^=d#]"~1pIQ;X&.resources'


def test_accepts_when_both_adds_present(tmp_path):
    f = tmp_path / "fixed.exe"
    f.write_bytes(b"PADDING" * 100 + guard.ADD_DIALOG.encode() + b"x" + guard.ADD_TYPE.encode())
    assert guard.check(str(f))["ok"] is True


def test_refuses_when_adds_absent(tmp_path):
    f = tmp_path / "void.exe"
    # pristine shape: only the backslash-escaped linked entry, no ADDs
    f.write_bytes(b"PADDING" * 100 + b'7nbx}a5?U:!^=d#\\]"~1pIQ;X\\&.resources')
    res = guard.check(str(f))
    assert res["ok"] is False
    assert res["has_dialog_add"] is False
    assert res["has_type_add"] is False
    assert "missing" in res["reason"]


def test_linked_entry_does_not_satisfy_the_dialog_add(tmp_path):
    """The pristine linked name must not be mistaken for either ADD."""
    linked = b'7nbx}a5?U:!^=d#\\]"~1pIQ;X\\&.resources'
    assert guard.ADD_DIALOG.encode() not in linked
    assert guard.ADD_TYPE.encode() not in linked


def test_exit_codes(tmp_path):
    fixed = tmp_path / "fixed.exe"
    fixed.write_bytes(guard.ADD_DIALOG.encode() + guard.ADD_TYPE.encode() + b"pad" * 20000000)
    assert guard.main([str(fixed)]) == 0

    void = tmp_path / "void.exe"
    void.write_bytes(b"no adds here" * 1000)
    assert guard.main([str(void)]) == 1

    assert guard.main([str(tmp_path / "missing.exe")]) == 2


def test_size_warning_on_implausibly_small_bundle(tmp_path):
    f = tmp_path / "tiny.exe"
    f.write_bytes(guard.ADD_DIALOG.encode() + guard.ADD_TYPE.encode())
    res = guard.check(str(f))
    assert res["ok"] is True
    assert res["size_warning"] is True
