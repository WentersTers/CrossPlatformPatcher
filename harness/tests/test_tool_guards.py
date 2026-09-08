"""§8 tool guards: click/type/exec/screenshot/audio/logs/transcript."""
import numpy as np
import pytest

from harness.tools import audio, logs
from harness.tools.click import click
from harness.tools.exec import wrap_windows_gui
from harness.tools.guards import StaleScreenshotError
from harness.tools.screenshot import sniff_format, take_screenshot
from harness.tools.transcript import compare_transcripts
from harness.tools.type import route_typing


def _gray(v=100):
    return np.full((16, 16), v, dtype=np.uint8)


def test_click_press_release_split_and_bounds():
    calls = []
    r = click(100, 200, screenshot_ts=0.0, now_ts=10.0,
              qmp_press=lambda x, y: calls.append(("press", x, y)),
              qmp_release=lambda x, y: calls.append(("release", x, y)),
              before_img=_gray(100), after_img=_gray(100))
    assert r.ok and [c[0] for c in calls] == ["press", "release"]
    assert r.no_visual_change is True  # identical -> warning, not silent
    with pytest.raises(ValueError):
        click(1920, 1080, 0.0, 1.0, lambda x, y: None, lambda x, y: None)
    with pytest.raises(StaleScreenshotError):
        click(10, 10, screenshot_ts=0.0, now_ts=200.0,
              qmp_press=lambda x, y: None, qmp_release=lambda x, y: None)
    with pytest.raises(ValueError):
        click(10, 10, 0.0, 1.0, lambda x, y: None, lambda x, y: None,
              tablet_absolute=False)


def test_click_visual_change_detected():
    calls = []
    # need 3-channel or matching shapes; use gray arrays
    r = click(10, 10, 0.0, 1.0, lambda x, y: calls.append(1),
              lambda x, y: calls.append(2),
              before_img=_gray(0), after_img=_gray(255))
    assert r.no_visual_change is False


def test_type_routing_wayland_and_qga():
    assert route_typing("hello", "x11").route == "xdotool"
    assert route_typing("hello", "wayland").route == "ydotool/wtype"
    assert route_typing("x" * 201, "x11").route == "qga-put-file"
    assert route_typing("héllo", "x11").route == "qga-put-file"
    assert abs(route_typing("x" * 100, "x11").est_delay_s - 2.0) < 1e-6


def test_exec_schtasks_wrap():
    cmd, wrapped = wrap_windows_gui("notepad.exe", True, True)
    assert wrapped and "schtasks /run" in cmd
    cmd2, w2 = wrap_windows_gui("ls", False, False)
    assert not w2 and cmd2 == "ls"


def test_screenshot_sniff():
    assert sniff_format(b"\x89PNG\r\n\x1a\n....") == "png"
    assert sniff_format(b"P6\n# hi") == "ppm"
    assert sniff_format(b"GIF89a") == "unknown"
    shot = take_screenshot(lambda: b"\x89PNG\r\n\x1a\n" + b"\x00" * 100)
    assert shot.fmt == "png"
    with pytest.raises(ValueError):
        take_screenshot(lambda: b"GIF89a....")


def test_audio_guards():
    assert audio.validate_audio_params(16000, 1, "s16le") == []
    assert audio.validate_audio_params(44100, 2, "f32le") != []
    assert audio.validate_gain(0.5) and not audio.validate_gain(0.9)
    assert audio.check_libasound("ubuntu-24.04", []) != []
    assert audio.check_libasound("ubuntu-24.04", ["libasound2t64"]) == []


def test_logs_and_transcript():
    assert any("journalctl" in c for c in logs.get_log_commands("ubuntu"))
    assert any("wevtutil" in c for c in logs.get_log_commands("windows 11"))
    assert compare_transcripts("Hey PAIcom", "hey paicom")["ok"] is True


def test_two_tier_verify_effect():
    import harness.tools.click as clickmod
    from harness.tools.click import verify_effect
    base = np.zeros((100, 100), dtype=np.uint8)
    # gross change: verdict without region work (real SSIM path)
    big = base.copy()
    big[20:80, 20:80] = 200
    v = verify_effect(base, big, 50, 50)
    assert v["verdict"] == "change" and v["ssim"] < 0.95
    # identical: none (real SSIM path)
    v0 = verify_effect(base, base.copy(), 50, 50)
    assert v0["verdict"] == "none"
    # gray zone (forced entry: whole-image SSIM craters on synthetic black,
    # so pin the decision table with recorded live magnitudes instead)
    real_ssim = clickmod.ssim_gray
    try:
        clickmod.ssim_gray = lambda a, b: 0.97752  # the defocus recording
        at_aim = base.copy()
        at_aim[45:55, 45:55] = 200
        assert verify_effect(base, at_aim, 50, 50, radius=30)["verdict"] == "effect"
        far = base.copy()
        far[5:15, 5:15] = 200  # ~50px+ from aim with radius 30: outside
        # 40px block far from aim: near-field clean -> noise. Note the real
        # defocus case had near EXACTLY 0.0; any cursor-at-aim pixels would
        # read effect here (documented limitation, cancelled by protocol).
        assert verify_effect(base, far, 50, 50, radius=30)["verdict"] == "noise"
    finally:
        clickmod.ssim_gray = real_ssim
