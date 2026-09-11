"""Staging-freshness checks: drift between source and installed trees.

Pins the s3r3 lesson: the installed build shipped 85/113 where 94/125
existed and 11 commands silently vanished (six misfiring elsewhere).
"""
import pytest

from harness.live import tree_freshness as tf


def test_clean_trees_pass():
    import tempfile
    with tempfile.TemporaryDirectory() as d:
        (open(d + "/a.txt", "w")).write("hello")
        drift = tf.check_freshness(d, {"a.txt": 5})
        assert drift.is_clean(), drift.summary()


def test_missing_extra_changed_all_reported():
    import tempfile
    with tempfile.TemporaryDirectory() as d:
        (open(d + "/same.txt", "w")).write("12345")
        (open(d + "/gone.txt", "w")).write("x")
        (open(d + "/diff.txt", "w")).write("12345")
        drift = tf.check_freshness(d, {"same.txt": 5, "diff.txt": 6,
                                       "ghost.txt": 1})
        assert not drift.is_clean()
        assert drift.missing == ["gone.txt"]
        assert drift.extra == ["ghost.txt"]
        assert drift.size_changed == ["diff.txt"]
        assert "missing=1 extra=1 changed=1" in drift.summary()


def test_manifest_gap_names_unreachable_commands():
    import tempfile
    with tempfile.TemporaryDirectory() as d:
        m = d + "/commands.txt"
        (open(m, "w")).write("hey paicom open hulu (hulu.txt)\n"
                             "hey paicom open hbo (hbo.txt)\n")
        gap = tf.manifest_gap(m, ["hey paicom open hulu (hulu.txt)"])
        assert gap == ["hey paicom open hbo (hbo.txt)"]


def test_empty_manifest_gap_is_clean():
    import tempfile
    with tempfile.TemporaryDirectory() as d:
        m = d + "/commands.txt"
        (open(m, "w")).write("hey paicom open hulu (hulu.txt)\n")
        assert tf.manifest_gap(m, ["hey paicom open hulu (hulu.txt)"]) == []


def test_exclude_skips_staged_additions():
    import tempfile
    with tempfile.TemporaryDirectory() as d:
        (open(d + "/app.exe", "w")).write("12345")
        drift = tf.check_freshness(
            d, {"app.exe": 5, ".wine-prefix/drive_c/x": 9,
                "models/m.dat": 3, "run.sh": 4},
            exclude=(".wine-prefix/", "models/", "run.sh"))
        assert drift.is_clean(), drift.summary()
