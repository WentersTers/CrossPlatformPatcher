"""Verdict-table rollup rules: every reliability class, generated counts.

The table is generated, never assembled: this pins the mapping from
(bucket, do-facet, manifest membership) to reliability class, plus the
counting convention (int-idx first draws only; probes/cleans/redraws
never in denominators).
"""
import pytest

from harness.live.verdict_table import build_table

FIRSTS = [
    {"idx": 0, "do": "misroute", "bucket": "known-bug", "audio": "joke.wav",
     "peak": 0.0, "detail": "x"},
    {"idx": 1, "do": "resolved", "bucket": "member-matched", "audio": "a.wav",
     "peak": 0.5, "detail": "x"},
    {"idx": 2, "do": "resolved", "bucket": "expected-silence", "audio": "b.wav",
     "peak": 0.0, "detail": "x"},
    {"idx": 3, "do": "resolved-404", "bucket": "observe-and-record", "audio": None,
     "peak": 0.0, "detail": "x"},
    {"idx": 4, "do": "resolved", "bucket": "fragment-inconclusive", "audio": "c.wav",
     "peak": 0.4, "detail": "x"},
    {"idx": 5, "do": "no-match", "bucket": "observe-and-record", "audio": None,
     "peak": 0.0, "detail": "x"},
    {"idx": 6, "do": "no-match", "bucket": "observe-and-record", "audio": None,
     "peak": 0.0, "detail": "x"},
    {"idx": 7, "do": "resolved", "bucket": "observe-and-record", "audio": "d.wav",
     "peak": 0.0, "detail": "x"},
    {"idx": 8, "do": "misroute", "bucket": "observe-and-record", "audio": "e.wav",
     "peak": 0.0, "detail": "x"},
]


def table():
    bodies = {r["idx"]: "b%d" % r["idx"] for r in FIRSTS}
    queues = {r["idx"]: "q" for r in FIRSTS}
    return build_table(FIRSTS, bodies, queues, {6}, {}, [])


def test_classes_cover_every_row():
    t = table()
    assert t["n"] == 9
    rel = {r["idx"]: r["reliability"] for r in t["rows"]}
    assert rel[0] == "hijacked"       # known-bug
    assert rel[1] == "works"          # matched
    assert rel[2] == "works-partial"  # expected silence, route right
    assert rel[3] == "works-partial"  # honest 404
    assert rel[4] == "fate-unclear"   # fragment
    assert rel[5] == "garble-rejected"
    assert rel[6] == "manifest-silence"
    assert rel[7] == "fate-silent"


def test_misroute_facet_normalized_to_hijacked():
    t = table()
    rel = {r["idx"]: r["reliability"] for r in t["rows"]}
    assert rel[8] == "hijacked"  # observe bucket + misroute facet
    assert t["do_facets"]["misroute"] == 2  # normalized, not as-harvested


def test_counts_close():
    t = table()
    assert sum(t["reliability"].values()) == t["n"] == 9
    assert sum(t["buckets"].values()) == 9
    assert t["fate_baseline"] == {"audible": 0, "of": 0}
