"""D3/D9 tracker: rolling-50 window + consecutive reset (spec-trap fix)."""
from harness.manager.trigger_tracker import TriggerTracker


def test_consecutive_resets_on_non_false_pass():
    t = TriggerTracker()
    t.record_test(False, True, False)  # false-pass x1
    assert t.consecutive_false_pass == 1
    assert t.d3().escalate is False
    t.record_test(False, True, True)  # worker pass, gate pass -> NOT false-pass
    assert t.consecutive_false_pass == 0
    t.record_test(False, True, False)
    t.record_test(False, True, False)
    assert t.consecutive_false_pass == 2
    assert t.d3().escalate is True  # >=2 consecutive fires even under 50 tests


def test_rolling_window_not_cumulative():
    t = TriggerTracker()
    # 6 disagreements in first 10, then 40+ agreements: cumulative rate over
    # 60 tests would be 10%, rolling-50 must forget the early burst.
    for i in range(60):
        t.record_test(i < 6, False, True)
    assert t.total_completed == 60
    assert len(t.disagreements) == 50
    assert t.rolling_disagree_rate == 0.0  # early burst aged out
    assert t.d3().escalate is False


def test_rolling_window_fires_over_10pct():
    t = TriggerTracker()
    for i in range(50):
        t.record_test(i < 6, False, True)  # 6/50 = 12% in-window
    assert abs(t.rolling_disagree_rate - 0.12) < 1e-9
    d = t.d3()
    assert d.escalate is True and "D3" in d.reason


def test_under_50_no_rate_fire():
    t = TriggerTracker()
    for _ in range(10):
        t.record_test(True, False, True)
    assert t.d3().escalate is False  # rate undefined until window full


def test_d9_accumulators():
    t = TriggerTracker()
    for _ in range(10):
        t.record_state_frame("idle")
    assert t.d9().escalate is False
    t2 = TriggerTracker()
    for _ in range(10):
        t2.record_state_frame("ambiguous", "idle")  # 100% ambiguous
    assert t2.d9().escalate is True
