"""Tests for the learned tier (features + kNN) — synthetic, no image files."""
from __future__ import annotations

import numpy as np

from harness.walkthrough.features import FEAT_DIM
from harness.walkthrough.learned import KnnWalkClassifier


def _vec(seed: int, offset: float) -> np.ndarray:
    rng = np.random.default_rng(seed)
    v = rng.random(FEAT_DIM, dtype=np.float32) + offset
    return v


def test_feat_dim_constant():
    assert FEAT_DIM == 32 * 18 * 3 + 16 * 3 + 4 * 4


def test_knn_predicts_nearest_class():
    # two well-separated clusters
    a = [_vec(1, 0.0), _vec(2, 0.05)]
    b = [_vec(3, 10.0), _vec(4, 10.05)]
    feats = np.stack(a + b)
    labels = ["A", "A", "B", "B"]
    clf = KnnWalkClassifier(feats, labels, k=3)
    # query near cluster A
    q = _vec(9, 0.02)
    res = clf.predict_detailed(q)
    assert res is not None
    assert res.state == "A"


def test_knn_abstains_on_far_query():
    a = [_vec(1, 0.0), _vec(2, 0.05)]
    b = [_vec(3, 10.0), _vec(4, 10.05)]
    feats = np.stack(a + b)
    labels = ["A", "A", "B", "B"]
    # abstain_floor high -> even the correct class abstains on a tie-ish vote
    clf = KnnWalkClassifier(feats, labels, k=2, abstain_floor=0.99)
    q = _vec(9, 5.0)  # equidistant-ish between A and B -> low agreement
    assert clf.predict(q) is None


def test_knn_save_load_roundtrip(tmp_path):
    a = [_vec(1, 0.0), _vec(2, 0.05)]
    b = [_vec(3, 10.0), _vec(4, 10.05)]
    feats = np.stack(a + b)
    labels = ["A", "A", "B", "B"]
    clf = KnnWalkClassifier(feats, labels, k=3)
    p = tmp_path / "m.npz"
    clf.save(p)
    clf2 = KnnWalkClassifier.load(p)
    q = _vec(9, 0.02)
    assert clf2.predict(q)[0] == "A"
    assert clf2.labels == labels
