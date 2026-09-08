"""QMP reconcile ledger: sent-but-unacked resumes via observation, never blind resend."""
from harness.tools.qmp_ledger import QmpLedger


def test_effect_present_no_resend():
    """Kill landed between QMP send and ack, but the click took effect:
    reconcile observes it -> acked, QMP sent exactly once."""
    ledger = QmpLedger()
    sends: list[str] = []
    act, replayed = ledger.claim("click-1", "click", {"x": 100, "y": 200})
    assert replayed is False
    sends.append(act.action_id)
    ledger.mark_sent("click-1")
    # ... process dies here (no ack) ...
    resumed = QmpLedger()
    resumed._actions = dict(ledger._actions)  # persisted ledger reloaded
    assert [a.action_id for a in resumed.unacked()] == ["click-1"]
    # reconcile by observation: post-action SSIM shows the click landed
    assert resumed.reconcile("click-1", effect_observed=True) == "acked"
    assert sends == ["click-1"]  # sent exactly once


def test_effect_absent_single_resend():
    ledger = QmpLedger()
    sends: list[str] = []
    ledger.claim("click-2", "click", {"x": 5, "y": 5})
    sends.append("click-2")
    ledger.mark_sent("click-2")
    assert ledger.reconcile("click-2", effect_observed=False) == "resend"
    sends.append("click-2")  # the one allowed resend
    ledger.mark_sent("click-2")
    ledger.mark_acked("click-2")
    assert ledger.unacked() == [] and sends == ["click-2", "click-2"]


def test_claim_idempotent():
    ledger = QmpLedger()
    a1, r1 = ledger.claim("c", "click", {})
    a2, r2 = ledger.claim("c", "click", {})
    assert r1 is False and r2 is True and a1 is a2


def test_ledger_survives_process_death_via_dict():
    """Kill-mid-window resume: the ledger file carries press-ack across."""
    ledger = QmpLedger()
    ledger.claim("k:press", "press", {"x": 1, "y": 2})
    ledger.mark_sent("k:press")
    ledger.mark_acked("k:press")
    data = ledger.to_dict()
    resumed = QmpLedger.from_dict(data)
    assert resumed._actions["k:press"].state == "acked"
    assert QmpLedger.from_dict({})._actions == {}
    assert QmpLedger.from_dict(None)._actions == {}


def test_per_event_resume_skips_acked_press():
    """Kill between press-ack and release: resume sends ONLY the release.
    The QMP log is the artifact that adjudicates the ledger's granularity."""
    from harness.tools.qmp_ledger import (audit_stuck_press, click_press_release,
                                          reconcile_click)
    ledger = QmpLedger()
    qmp_log: list[str] = []
    out = click_press_release(ledger, "click-1", 100, 200,
                              lambda x, y: qmp_log.append("press"),
                              lambda x, y: qmp_log.append("release"))
    assert out == {"press": "sent", "release": "sent"}
    assert qmp_log == ["press", "release"]

    # fresh ledger state as after a kill between press-ack and release-call:
    # press already acked, release never claimed.
    ledger2 = QmpLedger()
    ledger2.claim("click-2:press", "press", {"x": 1, "y": 2})
    ledger2.mark_sent("click-2:press")
    ledger2.mark_acked("click-2:press")
    out2 = click_press_release(ledger2, "click-2", 1, 2,
                               lambda x, y: qmp_log.append("press"),
                               lambda x, y: qmp_log.append("release"))
    assert out2 == {"press": "replayed", "release": "sent"}
    assert qmp_log[-1] == "release" and qmp_log.count("press") == 1


def test_reconcile_click_send_release_not_full_resend():
    from harness.tools.qmp_ledger import reconcile_click
    ledger = QmpLedger()
    ledger.claim("c:press", "press", {})
    ledger.mark_sent("c:press")
    ledger.mark_acked("c:press")
    # effect absent + press in log + no release -> send the release only
    assert reconcile_click(ledger, "c", False) == "send-release"
    # effect present -> everything acked, no resend at all
    assert reconcile_click(ledger, "c", True) == "acked"
    assert reconcile_click(ledger, "c", False) == "send-release"
    # once the release is claimed+acked too, the click is complete
    ledger.claim("c:release", "release", {})
    ledger.mark_sent("c:release")
    ledger.mark_acked("c:release")
    assert reconcile_click(ledger, "c", False) == "complete"
    # press never sent -> full click still needed
    assert reconcile_click(QmpLedger(), "fresh", False) == "resend-press"


def test_audit_flags_silent_poison():
    """Press acked + release never acked = button possibly held: every later
    input is a drag and nothing errors. The audit names the action at risk."""
    from harness.tools.qmp_ledger import audit_stuck_press
    ledger = QmpLedger()
    ledger.claim("bad:press", "press", {})
    ledger.mark_sent("bad:press")
    ledger.mark_acked("bad:press")
    assert audit_stuck_press(ledger) == ["bad"]
    ledger.claim("bad:release", "release", {})
    ledger.mark_sent("bad:release")
    ledger.mark_acked("bad:release")
    assert audit_stuck_press(ledger) == []
    assert audit_stuck_press(QmpLedger()) == []
