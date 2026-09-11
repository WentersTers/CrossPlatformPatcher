"""Staging-freshness check: compare a source tree against an installed tree.

Catches the s3r3 class: an installed build silently behind its source
(85/113 where 94/125 exists, 11 voice commands unreachable, six of them
misfiring elsewhere). Runs at stage time, before the snapshot; any drift
fails loud with the itemized list, never a silent pass.
"""
from __future__ import annotations

from dataclasses import dataclass, field
from pathlib import Path


@dataclass
class TreeDrift:
    missing: list[str] = field(default_factory=list)      # in source, absent installed
    extra: list[str] = field(default_factory=list)        # installed, not in source
    size_changed: list[str] = field(default_factory=list)  # same path, different size
    manifest_delta: int = 0  # installed manifest lines minus source lines

    def is_clean(self) -> bool:
        return not (self.missing or self.extra or self.size_changed
                    or self.manifest_delta != 0)

    def summary(self) -> str:
        return (f"missing={len(self.missing)} extra={len(self.extra)} "
                f"changed={len(self.size_changed)} "
                f"manifest_delta={self.manifest_delta:+d}")


def inventory(root: str | Path) -> dict[str, int]:
    """Relative path -> size in bytes for all files under root."""
    out = {}
    for p in Path(root).rglob("*"):
        if p.is_file():
            try:
                out[str(p.relative_to(root)).replace("\\", "/")] = p.stat().st_size
            except OSError:
                continue
    return out


def manifest_lines(manifest_path: str | Path) -> int:
    try:
        return sum(1 for ln in Path(manifest_path).read_text(
            encoding="utf-8", errors="replace").splitlines() if ln.strip())
    except OSError:
        return -1


def check_freshness(source_root: str | Path, installed: dict[str, int]) -> TreeDrift:
    """installed: inventory() of the remote tree (names+sizes only: cheap
    to pull). Manifest-line drift is covered separately by manifest_gap
    (line sets, not counts: which commands are unreachable)."""
    src = inventory(source_root)
    drift = TreeDrift()
    for path, size in src.items():
        if path not in installed:
            drift.missing.append(path)
        elif installed[path] != size:
            drift.size_changed.append(path)
    for path in installed:
        if path not in src:
            drift.extra.append(path)
    return drift


def manifest_gap(source_manifest: str | Path,
                 installed_manifest_lines: list[str]) -> list[str]:
    """Command lines present in source but absent installed: commands that
    can never resolve on the installed build (silent) or, worse, whose
    phrasing may route elsewhere. Returns the missing lines."""
    src = [l.strip() for l in Path(source_manifest).read_text(
        encoding="utf-8", errors="replace").splitlines() if l.strip()]
    have = {l.strip() for l in installed_manifest_lines}
    return [l for l in src if l not in have]
