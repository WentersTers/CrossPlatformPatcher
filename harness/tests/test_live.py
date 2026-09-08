"""Live drivers: transport mapping/payloads, ledgered click + resume, battery."""
import numpy as np
import pytest
from PIL import Image

from harness.live.battery import run_battery
from harness.live.click_driver import drive_click, resume_click
from harness.live.qmp_transport import QmpTransport, to_absolute
from harness.tools.qmp_ledger import QmpLedger


class FakeTransport:
    def __init__(self, kill_after_press=False):
        self.log: list[str] = []
        self.kill_after_press = kill_after_press

    def press(self, x, y):
        self.log.append("press")
        if self.kill_after_press:
            raise RuntimeError("simulated kill between press and release")
        return 0.05

    def release(self, x, y):
        self.log.append("release")
        return 0.06


def test_absolute_mapping_corners_and_clamp():
    assert to_absolute(0, 0) == (0, 0)
    assert to_absolute(1920, 1080) == (32767, 32767)
    assert to_absolute(960, 540) == (16384, 16384)
    assert to_absolute(99999, -5) == (32767, 0)


def test_move_only_sends_absolute_no_button():
    import json as j
    seen = {}

    def runner(cmd):
        seen["payload"] = j.loads(cmd[-1])
        return '{"return":{}}'

    t = QmpTransport(domain="d", runner=runner)
    t.move(100, 200)
    evts = seen["payload"]["arguments"]["events"]
    assert all(e["type"] == "abs" for e in evts)  # no btn event
    assert t.send_log[-1]["event"] == "move"


def test_press_payload_shape():
    import json as j
    seen = {}

    def runner(cmd):
        seen["payload"] = j.loads(cmd[-1])
        return '{"return":{}}'

    t = QmpTransport(domain="d", runner=runner)
    dt = t.press(960, 540)
    assert dt >= 0
    evts = seen["payload"]["arguments"]["events"]
    assert seen["payload"]["execute"] == "input-send-event"
    assert evts[0] == {"type": "abs", "data": {"axis": "x", "value": 16384}}
    assert evts[2]["data"] == {"down": True, "button": "left"}
    assert t.send_log[0]["event"] == "press"
    t.release(0, 0)
    assert t.send_log[1]["event"] == "release"


def test_drive_click_clean_and_resume_paths():
    ft = FakeTransport()
    ledger = QmpLedger()
    out = drive_click(ledger, ft, "c1", 100, 200)
    assert out["outcome"] == {"press": "sent", "release": "sent"}
    assert ft.log == ["press", "release"] and out["stuck"] == []

    # kill between press and release: press lands, driver dies in the window
    ft2 = FakeTransport(kill_after_press=True)
    ledger2 = QmpLedger()
    with pytest.raises(RuntimeError, match="simulated kill"):
        drive_click(ledger2, ft2, "c2", 10, 20, kill_window_s=0.0)
    # press was acked before the kill only if the raise came after mark_acked:
    # here the raise happens INSTEAD of the send, so press is 'sent'/unacked.
    assert ft2.log == ["press"]
    # resume with no effect observed -> full click redriven (press never acked)
    ft3 = FakeTransport()
    r = resume_click(ledger2, ft3, "c2", 10, 20, effect_observed=False)
    assert r["decision"] == "resend-press"
    assert ft3.log == ["press", "release"]

    # kill AFTER press-ack (ledger says acked) -> only release fires
    ledger3 = QmpLedger()
    ledger3.claim("c3:press", "press", {})
    ledger3.mark_sent("c3:press")
    ledger3.mark_acked("c3:press")
    ft4 = FakeTransport()
    r2 = resume_click(ledger3, ft4, "c3", 10, 20, effect_observed=False)
    assert r2["decision"] == "send-release"
    assert ft4.log == ["release"]  # one release, zero presses


def test_kill_window_opens_between_events():
    slept = []
    ft = FakeTransport()
    drive_click(QmpLedger(), ft, "c", 5, 5, kill_window_s=2.0,
                sleep_fn=lambda s: slept.append(s))
    assert slept == [2.0] and ft.log == ["press", "release"]


def test_checkpoint_fires_around_window_and_resume_replays(tmp_path):
    """File-backed resume: press ack persisted pre-kill, release fires post."""
    import json as j

    from harness.live.click_driver import _load_ledger, _save_ledger
    ckpts = []
    ft = FakeTransport()
    ledger = QmpLedger()
    out = drive_click(ledger, ft, "c9", 7, 7,
                      checkpoint_fn=lambda: ckpts.append(ledger.to_dict()))
    assert out["outcome"] == {"press": "sent", "release": "sent"}
    assert len(ckpts) == 2  # after press AND after release
    assert ckpts[0]["actions"]["c9:press"]["state"] == "acked"
    assert "c9:release" not in ckpts[0]["actions"]  # window state is exact
    # resume from the FIRST checkpoint file content with a fresh ledger
    resumed = QmpLedger.from_dict(ckpts[0])
    ft2 = FakeTransport()
    out2 = drive_click(resumed, ft2, "c9", 7, 7)
    assert out2["outcome"] == {"press": "replayed", "release": "sent"}
    assert ft2.log == ["release"]
    assert _load_ledger("/nonexistent-xyz/ledger.json")._actions == {}
    _save_ledger(resumed, str(tmp_path / "ledger.json"))
    assert _load_ledger(str(tmp_path / "ledger.json"))._actions.keys() == \
        resumed._actions.keys()


def test_battery_main_cli(tmp_path):
    from harness.live.battery import main
    arr = np.full((32, 32), 100, dtype=np.uint8)

    def fake_capture(i, path):
        Image.fromarray(arr).save(path)

    rc = main(["--n", "3", "--out", str(tmp_path / "b")], capture_fn=fake_capture)
    assert rc == 0
    import json as j
    summ = j.loads((tmp_path / "b" / "metrics.json").read_text())
    assert summ["fb_hash"] == {"frames": 3, "distinct": 1, "stable": True}


def test_battery_noise_floor_and_hash(tmp_path):
    base = np.full((64, 64), 128, dtype=np.uint8)
    changed = base.copy()
    changed[0:16, 0:16] = 200

    def capture(i, path):
        Image.fromarray(base if i < 4 else changed).save(path)

    clock = [0.0]

    def tick():
        return clock[0]

    def fake_capture(i, path):
        capture(i, path)
        clock[0] += 0.1

    res = run_battery(5, tmp_path, capture_fn=fake_capture, time_fn=tick)
    summ = res["summary"]
    assert summ["screenshot.latency_s"]["mean"] == pytest.approx(0.1)
    assert summ["screenshot.bytes"]["n"] == 5
    # identical pairs -> ssim 1.0; the one changed pair drags the floor
    assert summ["noise_floor"]["pairs"] == 4
    assert summ["noise_floor"]["min_ssim"] < 1.0
    assert summ["fb_hash"]["frames"] == 5
    assert (tmp_path / "metrics.json").exists()
