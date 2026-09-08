"""noVNC mosaic model: read-only default, per-tile status + pet state (§11)."""
from __future__ import annotations

from dataclasses import dataclass


@dataclass
class MosaicTile:
    vm_id: str
    worker_badge: str = ""
    task: str = ""
    progress: float = 0.0
    status_color: str = "gray"  # green/red/yellow/gray
    pet_state: str = "unknown"  # observed (vision, authoritative)
    claimed_state: str = "unknown"  # intent (telemetry, diagnostic only)
    read_only: bool = True
    manual_override: bool = False

    @property
    def mismatch(self) -> bool:
        """Claimed vs observed indicator (§11). Unknowns never mismatch."""
        if self.claimed_state in ("unknown", "") or self.pet_state in ("unknown", ""):
            return False
        return self.claimed_state != self.pet_state

    def to_dict(self) -> dict:
        return {"vm_id": self.vm_id, "worker_badge": self.worker_badge,
                "task": self.task, "progress": self.progress,
                "status_color": self.status_color, "pet_state": self.pet_state,
                "claimed_state": self.claimed_state, "mismatch": self.mismatch,
                "read_only": self.read_only and not self.manual_override}
