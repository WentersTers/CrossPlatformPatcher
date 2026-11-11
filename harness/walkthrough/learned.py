"""Learned tier: k-nearest-neighbors over hand-crafted features.

Implements the walkthrough LearnedClassifier protocol:
    predict(frame) -> (state, confidence) | None

Deduplicated reference frames become the neighbor bank. Predict by majority
vote over k nearest; confidence is distance-weighted agreement. Abstain (return
None) when the nearest neighbor is farther than a calibrated threshold, so the
engine falls back to SX. Fully auditable: every prediction cites its neighbors.
"""
from __future__ import annotations

import json
from dataclasses import dataclass
from pathlib import Path

import numpy as np

from harness.walkthrough.features import FEAT_DIM, extract_features, load_frame

ABSTAIN_DEFAULT = 0.35  # nearest-neighbor agreement below this -> abstain


@dataclass
class KnPrediction:
    state: str
    confidence: float
    neighbors: list[tuple[str, float]]  # (state, distance) for the k nearest


class KnnWalkClassifier:
    """kNN over feature vectors. Serializable to/from JSON."""

    def __init__(self, features: np.ndarray, labels: list[str],
                 k: int = 3, abstain_floor: float = ABSTAIN_DEFAULT):
        self.features = np.asarray(features, dtype=np.float32)
        self.labels = list(labels)
        self.k = k
        self.abstain_floor = abstain_floor
        # standardize using bank stats
        self.mean = self.features.mean(axis=0)
        self.std = self.features.std(axis=0) + 1e-6
        self._zs = (self.features - self.mean) / self.std

    # -- LearnedClassifier protocol ------------------------------------
    def predict(self, frame) -> tuple[str, float] | None:
        import numpy as _np
        if hasattr(frame, "convert"):  # PIL image
            feat = extract_features(frame)
        else:  # already a feature vector (tests)
            feat = _np.asarray(frame, dtype=_np.float32)
        res = self.predict_detailed(feat)
        if res is None:
            return None
        return (res.state, res.confidence)

    # -- richer API ----------------------------------------------------
    def predict_detailed(self, feat: np.ndarray) -> KnPrediction | None:
        z = (feat - self.mean) / self.std
        d = np.sqrt(((self._zs - z) ** 2).sum(axis=1))
        order = np.argsort(d)[: self.k]
        neigh = [(self.labels[i], float(d[i])) for i in order]
        # distance-weighted majority vote
        votes: dict[str, float] = {}
        for st, dist in neigh:
            w = 1.0 / (1e-6 + dist)
            votes[st] = votes.get(st, 0.0) + w
        best = max(votes, key=votes.get)
        total = sum(votes.values())
        conf = votes[best] / total if total > 0 else 0.0
        if conf < self.abstain_floor:
            return None
        return KnPrediction(best, conf, neigh)

    # -- serialization -------------------------------------------------
    def save(self, path: str | Path) -> None:
        Path(path).parent.mkdir(parents=True, exist_ok=True)
        np.savez_compressed(
            Path(path),
            features=self.features, mean=self.mean, std=self.std,
            labels=np.asarray(self.labels), k=self.k,
            abstain_floor=self.abstain_floor, feat_dim=FEAT_DIM,
        )

    @classmethod
    def load(cls, path: str | Path) -> "KnnWalkClassifier":
        data = np.load(Path(path), allow_pickle=False)
        return cls(
            features=data["features"], labels=[str(x) for x in data["labels"]],
            k=int(data["k"]), abstain_floor=float(data["abstain_floor"]),
        )
