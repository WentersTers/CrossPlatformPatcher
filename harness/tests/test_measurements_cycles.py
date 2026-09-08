"""Evidence-table helper: nearest-rank p50/p95, JSON artifacts."""
from harness.dry_run import main as dry_main
from harness.measurements import Samples


def test_percentiles_nearest_rank():
    s = Samples()
    for v in range(1, 101):
        s.add("shot_ms", v, unit="ms")
    summ = s.summary()["shot_ms"]
    assert (summ["n"], summ["min"], summ["max"]) == (100, 1.0, 100.0)
    assert summ["p50"] == 50.0 and summ["p95"] == 95.0
    assert summ["mean"] == 50.5 and summ["unit"] == "ms"


def test_single_sample_and_empty():
    s = Samples()
    s.add("overhead_pct", 2.5, unit="%")
    summ = s.summary()["overhead_pct"]
    assert summ["p50"] == summ["p95"] == summ["min"] == 2.5
    assert Samples().summary() == {}


def test_write_json_round_trip(tmp_path):
    s = Samples()
    s.add("ssim_delta", 0.42)
    s.add("ssim_delta", 0.58)
    out = s.write_json(tmp_path / "metrics.json")
    assert out["ssim_delta"]["n"] == 2
    import json
    assert json.loads((tmp_path / "metrics.json").read_text()) == out


def test_cycles_produce_distinct_dirs(tmp_path):
    rc = dry_main(["--root", str(tmp_path / "runs"), "--cycles", "2",
                   "--ts-base", "20260908T000000Z"])
    assert rc == 0
    base = tmp_path / "runs" / "20260908T000000Z" / "ubuntu-22.04"
    assert (base / "test_wake_word_seq" / "verdict.json").exists()
    base2 = tmp_path / "runs" / "20260908T000001Z" / "ubuntu-22.04"
    assert (base2 / "test_wake_word_seq" / "verdict.json").exists()
