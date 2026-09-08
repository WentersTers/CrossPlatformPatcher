"""Per-VM worker session runner (§4). Fresh session each task."""
from __future__ import annotations

import json
from dataclasses import dataclass, field
from typing import Any, Callable

from harness.workers.context_builder import build_context
from harness.workers.screenshot_history import ScreenshotHistory


@dataclass
class WorkerReport:
    vm_id: str
    task_id: str
    status: str  # pass | fail | aborted
    gates_passed: list[Any] = field(default_factory=list)
    evidence: dict[str, Any] = field(default_factory=dict)


class WorkerAgent:
    """Minimal worker: assembles context fresh per task, calls guarded tools."""

    ALLOWED_TOOLS = (
        "screenshot", "click", "type", "key", "wait_for", "exec", "get_logs",
        "inject_audio", "read_transcript", "assert_state",
        "assert_state_sequence", "update_state", "report_status",
        "report_completion", "report_failure",
    )

    def __init__(self, vm_id: str, os_name: str,
                 history: ScreenshotHistory | None = None,
                 on_event: Callable[[dict[str, Any]], None] | None = None):
        self.vm_id = vm_id
        self.os_name = os_name
        self.history = history or ScreenshotHistory()
        self.on_event = on_event

    def assemble(self, task: dict[str, Any], state_ref: str = "state.json") -> str:
        return build_context(self.os_name, task, state_ref)

    def _emit(self, payload: dict[str, Any]) -> None:
        if self.on_event:
            self.on_event(payload)

    def validate_handoff(self, payload: Any) -> dict[str, Any]:
        """Strict JSON schema at every handoff; malformed = reject-retry."""
        if not isinstance(payload, dict):
            raise ValueError("malformed handoff: not a JSON object")
        if "task_id" not in payload or "status" not in payload:
            raise ValueError("malformed handoff: missing task_id/status")
        return payload

    def run_task(self, task: dict[str, Any],
                 driver: Callable[[dict[str, Any]], WorkerReport]) -> WorkerReport:
        """Run one task via injected driver (tool-calling loop lives in driver).

        Keeps the agent unit-testable without VMs: driver performs guarded tool
        calls and returns the final report.
        """
        ctx = self.assemble(task)
        self._emit({"vm": self.vm_id, "event": "task_start",
                    "task_id": task.get("id"), "context_bytes": len(ctx)})
        report = driver(task)
        self.validate_handoff({"task_id": report.task_id, "status": report.status})
        self._emit({"vm": self.vm_id, "event": "task_end",
                    "task_id": report.task_id, "status": report.status})
        return report

    @staticmethod
    def report_to_json(report: WorkerReport) -> str:
        return json.dumps({"vm_id": report.vm_id, "task_id": report.task_id,
                           "status": report.status, "gates_passed": report.gates_passed,
                           "evidence": report.evidence}, sort_keys=True)
