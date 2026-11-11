"""Train + evaluate the walkthrough learned tier (kNN over features).

De-duplicates frames by SHA (identical bytes teach nothing), extracts features,
reports leave-one-out accuracy per state, then fits the final bank on all data
and saves it. The states that never occurred stay empty -> the engine abstains.

Usage:
    python -m harness.walkthrough.train --frames <ref-frames-dir> --out <model.npz>
"""
from __future__ import annotations

import argparse
import hashlib
from collections import defaultdict
from pathlib import Path

import numpy as np

from harness.walkthrough.features import extract_features, load_frame
from harness.walkthrough.learned import KnnWalkClassifier

TRAIN_STATES = ("S0_boot", "S1_settle", "S2_yes_wall", "S3_credits", "S4_wall",
                "S5_post_click", "S6_blank", "S6_menu", "S7_pet", "S9_integrity")


def load_dataset(frames_dir: str | Path):
    """Return (features, labels, per_state_filecounts) after de-dup by SHA."""
    root = Path(frames_dir)
    seen: set[str] = set()
    feats, labels = [], []
    counts = defaultdict(lambda: {"files": 0, "distinct": 0})
    for state in TRAIN_STATES:
        d = root / state
        if not d.is_dir():
            continue
        for f in sorted(d.glob("*.png")):
            counts[state]["files"] += 1
            sha = hashlib.sha256(f.read_bytes()).hexdigest()
            if sha in seen:
                continue
            seen.add(sha)
            counts[state]["distinct"] += 1
            feats.append(extract_features(load_frame(f)))
            labels.append(state)
    return np.asarray(feats, dtype=np.float32), labels, counts


def leave_one_out_accuracy(feats: np.ndarray, labels: list[str], k: int = 3):
    """For each sample, classify using all OTHER samples as the bank."""
    n = len(labels)
    correct = 0
    per_state = defaultdict(lambda: [0, 0])  # correct, total
    mean = feats.mean(axis=0)
    std = feats.std(axis=0) + 1e-6
    z = (feats - mean) / std
    for i in range(n):
        d = np.sqrt(((z - z[i]) ** 2).sum(axis=1))
        d[i] = np.inf
        order = np.argsort(d)[:k]
        votes = defaultdict(float)
        for j in order:
            w = 1.0 / (1e-6 + d[j])
            votes[labels[j]] += w
        pred = max(votes, key=votes.get)
        per_state[labels[i]][1] += 1
        if pred == labels[i]:
            correct += 1
            per_state[labels[i]][0] += 1
    acc = correct / n if n else 0.0
    return acc, per_state


def main(argv=None):
    ap = argparse.ArgumentParser()
    ap.add_argument("--frames", required=True)
    ap.add_argument("--out", required=True)
    ap.add_argument("--k", type=int, default=3)
    args = ap.parse_args(argv)

    feats, labels, counts = load_dataset(args.frames)
    print(f"loaded {len(labels)} distinct frames across {len(set(labels))} states")
    print("state            files distinct")
    for s in TRAIN_STATES:
        c = counts[s]
        print(f"  {s:14s} {c['files']:5d} {c['distinct']:8d}")

    if len(labels) == 0:
        print("no frames - nothing to train")
        return 1

    acc, per_state = leave_one_out_accuracy(feats, labels, k=args.k)
    print(f"\nleave-one-out accuracy (k={args.k}): {acc:.3f}")
    print("state            acc   (correct/total)")
    for s in TRAIN_STATES:
        c, t = per_state[s]
        if t:
            print(f"  {s:14s} {c/t:.3f}  ({c}/{t})")
        else:
            print(f"  {s:14s}   --   (0/0)")

    # fit final bank on ALL data
    clf = KnnWalkClassifier(feats, labels, k=args.k)
    clf.save(args.out)
    print(f"\nsaved model -> {args.out}")
    return 0


if __name__ == "__main__":
    raise SystemExit(main())
