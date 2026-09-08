"""Fault-localization matrix: every row pinned (vision-only can't do this)."""
from harness.telemetry.reconcile import reconcile


def test_pass_row():
    v = reconcile("idle", "idle", "idle", observed_latency_ms=200, settle_ms=500)
    assert v.passed and v.diagnosis == "pass"


def test_settle_boundary_is_tolerant():
    """The caution: disagreement inside the settle window is EXPECTED.
    At exactly settle_ms it is not divergence; 1ms over is timing."""
    assert reconcile("idle", "idle", "idle",
                     observed_latency_ms=500, settle_ms=500).passed is True
    v = reconcile("idle", "idle", "idle",
                  observed_latency_ms=501, settle_ms=500)
    assert not v.passed and v.diagnosis == "timing"


def test_render_or_vision_row():
    v = reconcile("idle", "idle", "listening")
    assert not v.passed and v.diagnosis == "render-or-vision"


def test_render_no_update_row():
    """State machine fine, sprite never updated — previously indistinguishable
    from 'never transitioned'."""
    v = reconcile("idle", "idle", None, screen_changed=False)
    assert not v.passed and v.diagnosis == "render-no-update"


def test_unclaimed_change_vs_event_loss():
    v = reconcile("idle", None, "speaking", screen_changed=True,
                  stream_reliable=True)
    assert not v.passed and v.diagnosis == "unclaimed-change"
    v2 = reconcile("idle", None, "speaking", screen_changed=True,
                   stream_reliable=False)
    assert not v2.passed and v2.diagnosis == "telemetry-unreliable"


def test_stuck_row_and_gap_guard():
    v = reconcile("listening", None, None, heartbeat_alive=True,
                  stream_reliable=True)
    assert not v.passed and v.diagnosis == "stuck"
    # absence on a gapped stream is NOT evidence of stuck
    v2 = reconcile("listening", None, None, heartbeat_alive=True,
                   stream_reliable=False)
    assert v2.diagnosis == "telemetry-unreliable"


def test_process_dead_row():
    v = reconcile("idle", "idle", "idle", heartbeat_alive=False)
    assert not v.passed and v.diagnosis == "process-dead"


def test_strict_intent_match():
    # wake-word E2E flag: wrong intent fails even with correct effect
    v = reconcile("listening", "idle", "listening", require_intent_match=True)
    assert not v.passed and v.diagnosis == "intent-mismatch"
    # non-strict: product contract is effect — passes with a note
    v2 = reconcile("listening", "idle", "listening", require_intent_match=False)
    assert v2.passed is True


def test_intent_effect_mismatch_row():
    v = reconcile("idle", "thinking", "speaking")
    assert not v.passed and v.diagnosis == "intent-effect-mismatch"


def test_unclaimed_expected_row():
    v = reconcile("idle", None, "idle", stream_reliable=True)
    assert not v.passed and v.diagnosis == "unclaimed-expected"


def test_unreliable_claim_never_flips_or_attributes():
    """reliable=false + present claim = intent lane missing -> vision-only.
    A stale claim (true transition lost in the gap) must not read as a
    render bug or an intent contradiction."""
    v = reconcile("idle", "idle", "listening", stream_reliable=False)
    assert not v.passed and v.diagnosis == "effect-mismatch"
    assert "vision-only" in v.detail
    v2 = reconcile("idle", "idle", None, stream_reliable=False)
    assert not v2.passed and v2.diagnosis == "effect-absent"
    assert "no render attribution" in v2.detail
    v3 = reconcile("idle", "idle", "idle", stream_reliable=False)
    assert v3.passed and "no intent attribution" in v3.detail
    v4 = reconcile("idle", "thinking", "speaking", stream_reliable=False)
    assert v4.diagnosis == "effect-mismatch"  # NOT intent-effect-mismatch
    # strict mode + degraded intent = unprovable, not contradicted
    v5 = reconcile("listening", "idle", "listening", stream_reliable=False,
                   require_intent_match=True)
    assert not v5.passed and v5.diagnosis == "telemetry-unreliable"
