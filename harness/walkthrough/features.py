"""Feature extraction for the walkthrough learned tier (PIL + numpy only).

No torch/torchvision dependency: the console torch build is broken (NumPy 2.x
vs torch built for 1.x). For ~38 distinct samples the plan's "nearest-neighbor
over embeddings" route is the right fit - and hand-crafted features are MORE
auditable than a neural embedding.

Features per frame (1792 dims):
  - downsampled RGB grid (32x18, preserves 16:9 layout)  = 1728
  - per-channel color histogram (16 bins)               =   48
  - edge-density grid (4x4)                             =   16

The grid carries WHERE the window is and its colors; the histogram carries the
global color profile; the edge grid carries texture/layout density. Together
these separate the walk states (black, dialog, wall, menu, pet, blank) cleanly.
"""
from __future__ import annotations

import hashlib
from pathlib import Path

import numpy as np
from PIL import Image, ImageFilter

GRID_W = 32
GRID_H = 18
HIST_BINS = 16
EDGE_GRID = 4
FEAT_DIM = GRID_W * GRID_H * 3 + HIST_BINS * 3 + EDGE_GRID * EDGE_GRID


def _to_array(img: Image.Image) -> np.ndarray:
    return np.asarray(img.convert("RGB"), dtype=np.float32)


def extract_features(img: Image.Image) -> np.ndarray:
    """Return a fixed-length float32 feature vector for one frame."""
    img = img.convert("RGB")

    # 1. downsampled RGB grid (0..1)
    grid = img.resize((GRID_W, GRID_H), Image.BILINEAR)
    g = _to_array(grid) / 255.0
    grid_feat = g.reshape(-1)

    # 2. color histogram per channel (normalized)
    full = _to_array(img)
    hist_feats = []
    for c in range(3):
        hist, _ = np.histogram(full[:, :, c], bins=HIST_BINS, range=(0, 255))
        hist = hist.astype(np.float32)
        s = hist.sum()
        hist_feats.append(hist / s if s > 0 else hist)
    hist_feat = np.concatenate(hist_feats)

    # 3. edge-density grid (Sobel-ish magnitude via PIL FIND_EDGES)
    edges = img.convert("L").filter(ImageFilter.FIND_EDGES)
    e = np.asarray(edges, dtype=np.float32) / 255.0
    eh, ew = e.shape
    edge_cells = []
    for gy in range(EDGE_GRID):
        for gx in range(EDGE_GRID):
            y0, y1 = int(gy * eh / EDGE_GRID), int((gy + 1) * eh / EDGE_GRID)
            x0, x1 = int(gx * ew / EDGE_GRID), int((gx + 1) * ew / EDGE_GRID)
            edge_cells.append(float(e[y0:y1, x0:x1].mean()))
    edge_feat = np.asarray(edge_cells, dtype=np.float32)

    feat = np.concatenate([grid_feat, hist_feat, edge_feat]).astype(np.float32)
    assert feat.shape[0] == FEAT_DIM, f"dim mismatch {feat.shape[0]} != {FEAT_DIM}"
    return feat


def load_frame(path: str | Path) -> Image.Image:
    return Image.open(path).convert("RGB")


def frame_sha(path: str | Path) -> str:
    return hashlib.sha256(Path(path).read_bytes()).hexdigest()
