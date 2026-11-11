"""Tests for rig-side glue (GuestBridge) using a fake executor."""
from __future__ import annotations

from harness.walkthrough import GuestBridge, GuestBridgeConfig, build_default_steps


def make_bridge(executor):
    cfg = GuestBridgeConfig(guest_ip="10.0.0.5", guest_user="user", display=":77")
    return GuestBridge(cfg, executor=executor)


def test_capture_frame_bytes_decodes_base64():
    import base64
    payload = b"\x89PNG\r\n\x1a\n" + b"\x00" * 4
    b64 = base64.b64encode(payload).decode()

    def exec_(cmd):
        if "base64" in cmd:
            return b64 + "\n"
        return ""

    b = make_bridge(exec_)
    assert b.capture_frame_bytes() == payload


def test_capture_empty_returns_empty():
    b = make_bridge(lambda cmd: "")
    assert b.capture_frame_bytes() == b""


def test_guest_subject_sha256_parses():
    def exec_(cmd):
        if "sha256sum" in cmd:
            return "abc123def  /home/user/game/uninstall.exe.patched.exe\n"
        return ""

    b = make_bridge(exec_)
    assert b.guest_subject_sha256() == "abc123def"


def test_guest_subject_empty_on_failure():
    b = make_bridge(lambda cmd: "")
    assert b.guest_subject_sha256() == ""


def test_audio_state_up():
    def exec_(cmd):
        if "pactl info" in cmd:
            return "Server Name: PulseAudio (on PipeWire 1.4.11)\n"
        return ""

    b = make_bridge(exec_)
    assert b.audio_state() == "up"


def test_audio_state_off():
    b = make_bridge(lambda cmd: "NO_SERVER\n")
    assert b.audio_state() == "off"


def test_motion_sweep_calls_executor():
    calls = []

    def exec_(cmd):
        calls.append(cmd)
        return ""

    b = make_bridge(exec_)
    b.motion_sweep()
    assert any("xdotool mousemove" in c for c in calls)


def test_click_calls_executor():
    calls = []

    def exec_(cmd):
        calls.append(cmd)
        return ""

    b = make_bridge(exec_)
    b.click(100, 200)
    assert any("mousemove 100 200" in c for c in calls)
    assert any("click 1" in c for c in calls)


def test_key_calls_executor():
    calls = []

    def exec_(cmd):
        calls.append(cmd)
        return ""

    b = make_bridge(exec_)
    b.key("Return")
    assert any("xdotool key Return" in c for c in calls)


def test_audio_on_off_calls_executor():
    calls = []

    def exec_(cmd):
        calls.append(cmd)
        return ""

    b = make_bridge(exec_)
    b.audio_on()
    b.audio_off()
    joined = "\n".join(calls)
    assert "pipewire" in joined
    assert "pkill -x pipewire" in joined


def test_build_default_steps_count():
    b = make_bridge(lambda cmd: "")
    steps = build_default_steps(b)
    assert len(steps) == 9
    assert steps[0].name == "credits_dismiss"
    assert steps[4].name == "byeee_click"


def test_run_subcommand_help():
    import subprocess, sys
    r = subprocess.run(
        [sys.executable, "-m", "harness.walkthrough.rig", "--help"],
        capture_output=True, text=True,
        cwd="D:/SteamLibrary/steamapps/common/CrossPlatformPatcher",
    )
    assert r.returncode == 0
    assert "guest-ip" in r.stdout
