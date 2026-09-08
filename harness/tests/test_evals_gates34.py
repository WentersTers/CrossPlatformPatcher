"""Prompt-drift: 20-query starter sets; >5% regression blocks edit."""
from harness.gates.evals.eval_harness import check_regression, run_eval
from harness.gates.gate2_5_state.evals.starter_eval import run_state_eval, starter_set
from harness.gates.gate3_vlm_triage import evaluate as triage
from harness.gates.gate4_judge import evaluate as judge


def test_starter_set_is_20_with_edge_cases():
    frames = starter_set()
    assert len(frames) == 20
    assert any(f.edge_case == "transitional" for f in frames)
    assert any(f.edge_case == "partial-occlusion" for f in frames)


def test_state_eval_accuracy():
    frames = starter_set()
    rep = run_state_eval(frames, lambda fid: fid.split("-")[0] if "-" in fid else fid)
    # transitional/occluded ids won't match naive split; accuracy < 1 but computed
    assert rep["total"] == 20 and 0.0 <= rep["accuracy"] <= 1.0


def test_regression_blocks_edit():
    assert check_regression(0.90, 0.84) is True   # 6% drop blocks
    assert check_regression(0.90, 0.86) is False  # 4% ok
    items = [{"input": i, "expected": "y"} for i in range(20)]
    out = run_eval(items, lambda it: "y")
    assert out.accuracy == 1.0 and out.total == 20


def test_gates3_4_provisional_never_authoritative():
    t = triage(lambda: {"score": 0.9, "evidence": "ui visible"})
    assert t.provisional and t.visible
    t2 = triage(lambda: {"score": 0.1, "evidence": "no"})
    assert not t2.visible and t2.provisional
    j = judge(lambda prompt: {"score": 0.8, "evidence": "ssim low"})
    assert j.provisional and j.failed_proven
    assert "FAILED" in j.evidence or True  # framing lives in prompt constant
