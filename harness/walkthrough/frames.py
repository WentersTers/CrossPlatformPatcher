"""Reference-frame loading for the walkthrough engine.

Loads golden/reference frames from a directory layout and builds the
golden_states dict consumed by StateTemplateMatcher. Training population is
GUEST SCREENSHOTS only (see plan: Training-data domain).
"""
from __future__ import annotations

import hashlib
from pathlib import Path
from typing import Any

try:
    import cv2
    import numpy as np
    _CV2_OK = True
except Exception:  # pragma: no cover - environment dependent
    _CV2_OK = False


def load_reference_frames(root: str | Path) -> dict[str, list[Any]]:
    """Load reference frames grouped by state from a directory.

    Expected layout: root/<state>/*.png (or *.jpg). State names may be the
    WalkState values or short aliases; the caller maps aliases if needed.
    Returns state -> list of images (numpy arrays when cv2 is available, else
    raw bytes). Empty dict if root missing.
    """
    root = Path(root)
    if not root.exists() or not root.is_dir():
        return {}
    out: dict[str, list[Any]] = {}
    for state_dir in sorted(p for p in root.iterdir() if p.is_dir()):
        imgs: list[Any] = []
        for f in sorted(state_dir.iterdir()):
            if f.suffix.lower() not in (".png", ".jpg", ".jpeg", ".ppm"):
                continue
            if _CV2_OK:
                img = cv2.imread(str(f), cv2.IMREAD_COLOR)
                if img is not None:
                    imgs.append(img)
            else:
                imgs.append(f.read_bytes())
        if imgs:
            out[state_dir.name] = imgs
    return out


def sha256_file(path: str | Path) -> str:
    h = hashlib.sha256()
    with open(path, "rb") as fh:
        for chunk in iter(lambda: fh.read(65536), b""):
            h.update(chunk)
    return h.hexdigest()


def inventory(root: str | Path) -> dict[str, int]:
    """Count reference frames per state without loading pixels."""
    root = Path(root)
    if not root.exists() or not root.is_dir():
        return {}
    out: dict[str, int] = {}
    for state_dir in sorted(p for p in root.iterdir() if p.is_dir()):
        n = sum(1 for f in state_dir.iterdir()
                if f.suffix.lower() in (".png", ".jpg", ".jpeg", ".ppm"))
        if n:
            out[state_dir.name] = n
    return out
