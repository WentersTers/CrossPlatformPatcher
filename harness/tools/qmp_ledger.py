"""QMP-side exactly-once discipline (network-boundary half of resume safety).

append_event_idempotent covers the EVENT LOG side. This ledger covers the QMP
side: exactly-once over a network boundary is impossible, so the rule is
claim -> sent -> acked, and on resume anything sent-but-unacked is RECONCILED
BY OBSERVATION (post-action SSIM / state re-read) before any resend — never a
blind re-execution of the in-flight click.
"""
from __future__ import annotations

from dataclasses import dataclass, field


@dataclass
class QmpAction:
    action_id: str
    kind: str
    payload: dict
    state: str = "claimed"  # claimed -> sent -> acked


class QmpLedger:
    def __init__(self):
        self._actions: dict[str, QmpAction] = {}

    def claim(self, action_id: str, kind: str, payload: dict) -> tuple[QmpAction, bool]:
        """Idempotent claim. Returns (action, replayed)."""
        if action_id in self._actions:
            return self._actions[action_id], True
        act = QmpAction(action_id, kind, dict(payload))
        self._actions[action_id] = act
        return act, False

    def mark_sent(self, action_id: str) -> None:
        self._actions[action_id].state = "sent"

    def mark_acked(self, action_id: str) -> None:
        self._actions[action_id].state = "acked"

    def unacked(self) -> list[QmpAction]:
        """Sent-but-unacked at resume: reconcile each by observation first."""
        return [a for a in self._actions.values() if a.state == "sent"]

    def to_dict(self) -> dict:
        """Snapshot for file persistence across process kills."""
        return {"actions": {aid: {"kind": a.kind, "payload": a.payload,
                                  "state": a.state}
                            for aid, a in self._actions.items()}}

    @classmethod
    def from_dict(cls, data: dict) -> QmpLedger:
        ledger = cls()
        for aid, a in (data or {}).get("actions", {}).items():
            ledger._actions[aid] = QmpAction(aid, a["kind"], a["payload"],
                                             a.get("state", "claimed"))
        return ledger
    def reconcile(self, action_id: str, effect_observed: bool) -> str:
        """effect_observed: did post-action observation (SSIM/state) show the
        effect already applied? Returns 'acked' (skip resend) or 'resend'."""
        act = self._actions[action_id]
        if effect_observed:
            act.state = "acked"
            return "acked"
        act.state = "claimed"  # back to claimed: exactly one resend allowed
        return "resend"


def _fire(ledger: QmpLedger, action_id: str, kind: str, payload: dict,
          send_fn) -> str:
    """Fire one QMP event exactly-once-per-ack: replayed/skipped if already
    acked (resume path), else claim -> sent -> call -> acked. A raise leaves
    state 'sent' so resume reconciles instead of assuming."""
    act, _ = ledger.claim(action_id, kind, payload)
    if act.state == "acked":
        return "replayed"
    ledger.mark_sent(action_id)
    send_fn()
    ledger.mark_acked(action_id)
    return "sent"


def click_press_release(ledger: QmpLedger, action_id: str, x: int, y: int,
                        press_fn, release_fn) -> dict[str, str]:
    """A click is TWO ledgered events, not one atomic action.

    Kill between press and release resumes as: press already acked (skipped,
    never re-sent) + release still pending. Resending the whole click would
    double-press while the first is held; marking complete without the release
    leaves the guest button stuck held (the silent-poison state).
    """
    press = _fire(ledger, f"{action_id}:press", "press",
                  {"x": x, "y": y}, lambda: press_fn(x, y))
    release = _fire(ledger, f"{action_id}:release", "release",
                    {"x": x, "y": y}, lambda: release_fn(x, y))
    return {"press": press, "release": release}


def reconcile_click(ledger: QmpLedger, action_id: str,
                    effect_observed: bool) -> str:
    """Resume adjudication for an in-flight click (Session-2 centerpiece).

    - effect present -> 'acked' (both lanes closed, no resend)
    - press acked + release missing -> 'send-release' (complete the release,
      NEVER resend the press)
    - press never sent -> 'resend-press' (full click still needed)
    """
    press = ledger._actions.get(f"{action_id}:press")
    release = ledger._actions.get(f"{action_id}:release")
    if effect_observed:
        if press is not None:
            press.state = "acked"
        if release is not None:
            release.state = "acked"
        return "acked"
    if press is not None and press.state == "acked":
        if release is None or release.state != "acked":
            return "send-release"
        return "complete"
    return "resend-press"


def audit_stuck_press(ledger: QmpLedger) -> list[str]:
    """Silent-poison detector: press acked but release never acked means the
    guest button may still be held — every subsequent input is a drag and
    nothing reports an error. Returns the base action_ids at risk."""
    stuck: list[str] = []
    for aid, act in ledger._actions.items():
        if not aid.endswith(":press") or act.state != "acked":
            continue
        base = aid[: -len(":press")]
        rel = ledger._actions.get(f"{base}:release")
        if rel is None or rel.state != "acked":
            stuck.append(base)
    return stuck
