"""Real OCR backend: tesseract CLI + active-window scoping (Session 3).

Until now OCR was only ever an injected stub. This backend shells to
`tesseract stdin stdout` with PNG bytes on stdin (no temp files on the hot
path). Scoping discipline (Gate 2): verdict OCR reads the ACTIVE WINDOW
region only — full-desktop text is triage data, never verdict evidence.
"""
from __future__ import annotations

import subprocess
from typing import Callable

import numpy as np

Runner = Callable[[list[str], bytes], str]


class TesseractError(Exception):
    pass


def _default_runner(cmd: list[str], stdin_bytes: bytes) -> str:
    try:
        p = subprocess.run(cmd, input=stdin_bytes, capture_output=True,
                           timeout=120)
    except FileNotFoundError:
        raise TesseractError("tesseract executable not found (apt install tesseract-ocr)")
    except subprocess.TimeoutExpired as e:
        raise TesseractError(f"tesseract timed out: {e}") from e
    if p.returncode != 0:
        raise TesseractError(f"tesseract failed: {p.stderr.decode()[:200]}")
    return p.stdout.decode("utf-8", errors="replace")


def _to_png(image: np.ndarray) -> bytes:
    from PIL import Image
    import io
    arr = image if image.ndim in (2, 3) else image[:, :, 0]
    buf = io.BytesIO()
    Image.fromarray(arr.astype("uint8")).save(buf, format="PNG")
    return buf.getvalue()


class TesseractBackend:
    """ocr(image, roi) -> text. roi = (x, y, w, h) or None (full frame).

    psm: tesseract page-seg mode (None = default 3). Tight terminal-strip
    ROIs need psm 6 (uniform block); pass it for scoped verdict reads.
    """

    def __init__(self, runner: Runner | None = None, lang: str = "eng",
                 psm: int | None = None):
        self._run = runner or _default_runner
        self.lang = lang
        self.psm = psm

    def ocr(self, image: np.ndarray,
            roi: tuple[int, int, int, int] | None = None) -> str:
        img = image
        if roi is not None:
            x, y, w, h = roi
            img = image[y: y + h, x: x + w]
            if img.size == 0:
                raise TesseractError(f"empty ROI {roi}")
        cmd = ["tesseract", "stdin", "stdout", "-l", self.lang]
        if self.psm is not None:
            cmd += ["--psm", str(self.psm)]
        try:
            return self._run(cmd, _to_png(img))
        except OSError as e:
            raise TesseractError(f"ocr transport failed: {e}") from e


def scoped_ocr(screenshot: np.ndarray,
               roi: tuple[int, int, int, int],
               backend: TesseractBackend,
               pad: int = 24, scale: int = 2) -> str:
    """Verdict-path OCR: the ROI is mandatory, never None. Full-desktop reads
    go through backend.ocr(img, None) explicitly and are triage-only.

    Recipe (Session-3 sweep): pad generously with clamped margins (tight
    strips starve the line finder at every PSM), upscale 2x LANCZOS
    (13px terminal glyphs need the pixels), PSM from the backend (6 for
    uniform text blocks). Either ingredient alone fails; together they read
    perfectly on real pixels.
    """
    x, y, w, h = roi
    H, W = screenshot.shape[:2]
    x0, y0 = max(0, x - pad), max(0, y - pad)
    x1, y1 = min(W, x + w + pad), min(H, y + h + pad)
    crop = screenshot[y0:y1, x0:x1]
    if scale != 1:
        from PIL import Image
        crop = np.asarray(Image.fromarray(crop.astype("uint8")).resize(
            ((x1 - x0) * scale, (y1 - y0) * scale), Image.LANCZOS))
    return backend.ocr(crop, None)
