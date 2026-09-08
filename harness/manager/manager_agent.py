"""LangGraph-style supervisor node (status-only, §3).

Manager reasoning is cheap because it never carries pixels. The one screenshot
exception is gated: escalations deliver gate output packets, never raw pixels.
"""
from __future__ import annotations

from dataclasses import dataclass, field
from typing import Any

from harness.manager.budgets import BudgetTracker
from harness.manager.delegation import Task, completion_predicate, validate_task
from harness.manager.escalation import EscalationDecision, auto_escalation_reason
from harness.manager.trigger_tracker import TriggerTracker
from harness.persistence.checkpointer import Checkpointer


@dataclass
class VmStatus:
    vm_id: str
    reported: dict[str, Any] = field(default_factory=dict)
    observed: dict[str, Any] = field(default_factory=dict)
    divergence: bool = False
    task: str = ""
    budget: dict[str, int] = field(default_factory=dict)


class ManagerAgent:
    def __init__(self, checkpointer: Checkpointer | None = None,
                 tracker: TriggerTracker | None = None):
        self.cp = checkpointer
        self.budgets = BudgetTracker()
        self.tracker = tracker or TriggerTracker()
        self.queue: list[Task] = []
        self.statuses: dict[str, VmStatus] = {}
        self.rollbacks_this_run = 0
        self.escalations: list[dict] = []

    # -- tools --
    def get_vm_status(self, vm_id: str) -> VmStatus:
        return self.statuses.get(vm_id, VmStatus(vm_id))

    def delegate_task(self, vm_id: str, payload: dict) -> Task:
        task = validate_task(payload)
        self.queue.append(task)
        st = self.get_vm_status(vm_id)
        st.task = task.id
        self.statuses[vm_id] = st
        if self.cp:
            self.cp.append_event(task.id, {"actor": "manager", "action": "delegate",
                                           "vm": vm_id,
                                           "predicate": completion_predicate(task.type)})
        return task

    def abort_task(self, vm_id: str, reason: str) -> None:
        st = self.get_vm_status(vm_id)
        st.task = ""
        self.statuses[vm_id] = st
        self.escalations.append({"vm": vm_id, "action": "abort", "reason": reason})

    def rollback_vm(self, vm_id: str, reason: str, human_approved: bool = False) -> dict:
        """First 2 per run autonomous; beyond -> human-gated (§3)."""
        if self.rollbacks_this_run >= 2 and not human_approved:
            self.escalations.append({"vm": vm_id, "action": "rollback-gated",
                                     "reason": reason})
            return {"gated": True, "reason": "rollback beyond 2 requires human"}
        self.rollbacks_this_run += 1
        return {"gated": False, "rollbacks": self.rollbacks_this_run}

    def escalate_to_human(self, reason: str) -> dict:
        dec = auto_escalation_reason(reason)
        self.escalations.append({"action": "escalate", "reason": reason,
                                 "auto": dec.escalate})
        return {"escalated": True, "reason": reason}

    def record_test_outcome(self, worker_said_pass: bool, gate_said_pass: bool,
                            gate_disagreed: bool = False) -> EscalationDecision:
        """Close the loop gate output -> verdict -> D3 (the path that was
        disarmed before TriggerTracker owned the stream). Every completed
        test flows here; the returned decision fires mechanically."""
        self.tracker.record_test(gate_disagreed, worker_said_pass, gate_said_pass)
        dec = self.tracker.d3()
        if dec.escalate:
            self.escalations.append({"action": "force-option-b", "reason": dec.reason})
        return dec
