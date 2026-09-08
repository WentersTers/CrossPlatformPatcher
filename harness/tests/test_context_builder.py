"""Deterministic OS-quirk injection; trust boundary present."""
from harness.workers.context_builder import build_context, get_os_quirk_block


def test_quirk_blocks_deterministic():
    u1 = get_os_quirk_block("ubuntu-22.04")
    u2 = get_os_quirk_block("Ubuntu 22.04 X11")
    assert u1 == u2
    assert "ydotool" in get_os_quirk_block("fedora-39").lower() or "wayland" in get_os_quirk_block("fedora-39").lower()
    w = get_os_quirk_block("windows-11")
    assert "schtasks" in w
    assert "Session 0" in w


def test_build_context_has_state_ref_and_trust():
    ctx = build_context("ubuntu", {"id": "t1", "type": "boot",
                                   "params": {}, "success_criteria": "x"}, "state.json")
    assert "state.json" in ctx
    assert "UNTRUSTED" in ctx
    assert "t1" in ctx
