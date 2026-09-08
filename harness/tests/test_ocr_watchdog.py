"""OCR backend + watchdog live loop (Session 3 mock residue)."""
import numpy as np
import pytest
from PIL import Image

from harness.live.watchdog_run import run_watchdog
from harness.tools.ocr import TesseractBackend, TesseractError, scoped_ocr


def _text_image(text: str = "Hello", size=(200, 60)):
    # content-agnostic: the fake runner echoes geometry, proving crop paths
    return np.full((*reversed(size), 3), 200, dtype=np.uint8)


def _geom_runner(cmd, stdin_bytes):
    from PIL import Image as I
    import io
    im = I.open(io.BytesIO(stdin_bytes))
    return f"{im.width}x{im.height}"


def test_roi_crop_reaches_backend():
    b = TesseractBackend(runner=_geom_runner)
    img = _text_image()
    assert b.ocr(img, None) == "200x60"
    assert b.ocr(img, (10, 10, 100, 30)) == "100x30"  # crop, not full frame
    # scoped recipe: pad clamped to bounds, then 2x upscale (pins geometry)
    assert scoped_ocr(img, (0, 0, 50, 20), b) == "148x88"
    assert scoped_ocr(img, (190, 50, 50, 20), b, pad=24, scale=1) == "34x34"
    assert scoped_ocr(img, (0, 0, 50, 20), b, pad=0, scale=1) == "50x20"
    with pytest.raises(TesseractError):
        b.ocr(img, (0, 0, 0, 0))  # empty ROI fails loud


def test_psm_flag_reaches_cli():
    seen = {}

    def runner(cmd, data):
        seen["cmd"] = cmd
        return "text"

    TesseractBackend(runner=runner).ocr(_text_image())
    assert "--psm" not in seen["cmd"]  # default: PSM 3, no flag
    TesseractBackend(runner=runner, psm=6).ocr(_text_image(), (0, 0, 10, 10))
    assert seen["cmd"][-2:] == ["--psm", "6"]


def test_transport_failures_typed():
    def boom(cmd, data):
        raise OSError("pipe broken")

    with pytest.raises(TesseractError, match="transport"):
        TesseractBackend(runner=boom).ocr(_text_image())

    def fail(cmd, data):
        raise TesseractError("tesseract failed: bad params")

    with pytest.raises(TesseractError):
        TesseractBackend(runner=fail).ocr(_text_image())


def test_watchdog_overhead_math_and_missing_log(tmp_path):
    clock = [0.0]
    log_bytes = [0]

    def ping():
        clock[0] += 0.2
        return "ok"

    def shot(seq, path):
        clock[0] += 0.3
        Image.fromarray(np.full((16, 16), 100, dtype=np.uint8)).save(path)

    def log_missing():
        return {"present": False, "bytes": 0}

    slept = []
    res = run_watchdog(3, 5.0, tmp_path, ping_fn=ping, shot_fn=shot,
                       log_fn=log_missing,
                       sleep_fn=lambda s: slept.append(s),
                       time_fn=lambda: clock[0])
    assert all(r["log_present"] is False and r["log_delta"] == 0
               for r in res["readings"])  # missing, never stalled
    assert all(r["qga_state"] == "ok" for r in res["readings"])
    # poll 0.5s of 5s cadence -> 10% overhead, sleeps the remainder
    assert res["summary"]["watchdog.overhead_pct"]["p50"] == pytest.approx(10.0)
    assert slept == [4.5, 4.5, 4.5]
    assert (tmp_path / "metrics.json").exists()


def test_watchdog_log_growth_and_qga_unknown(tmp_path):
    clock = [0.0]
    sizes = [100, 150, 150]

    def ping():
        raise ConnectionError("agent timeout")  # -> unknown, not a crash

    def shot(seq, path):
        Image.fromarray(np.full((8, 8), 50, dtype=np.uint8)).save(path)

    it = iter(sizes)

    def log():
        return {"present": True, "bytes": next(it)}

    res = run_watchdog(3, 10.0, tmp_path, ping_fn=ping, shot_fn=shot,
                       log_fn=log, sleep_fn=lambda s: None,
                       time_fn=lambda: clock[0])
    assert [r["log_delta"] for r in res["readings"]] == [0, 50, 0]
    assert res["summary"]["qga"] == {"ok": 0, "unknown": 3}


def test_ocr_run_cli_shape(tmp_path):
    from harness.live.ocr_run import main
    img = tmp_path / "shot.ppm"
    Image.fromarray(np.full((40, 20, 3), 180, dtype=np.uint8)).save(img)
    out = tmp_path / "ocr.json"

    class FakeBackend:
        def ocr(self, image, roi=None):
            img = image
            if roi is not None:
                x, y, w, h = roi
                img = image[y: y + h, x: x + w]
            h, w = img.shape[:2]
            return f"seen-{w}x{h}"

    rc = main(["--shot", str(img), "--out", str(out),
               "--roi", "0,0,10,10"], backend=FakeBackend())
    assert rc == 0
    import json as j
    res = j.loads(out.read_text())
    assert res["full_text"] == "seen-20x40"  # 40 rows x 20 cols
    assert res["scoped"][0]["text"] == "seen-40x68"  # padded+scaled ROI
    assert res["metrics"]["ocr.scoped_ms"]["n"] == 1
