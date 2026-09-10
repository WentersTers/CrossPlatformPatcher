"""Verdict-table rollup: mechanical assembly from evidence JSON.

Takes first-draw verdict rows (produced by the voice loop + adjudication
rules) and emits the reliability decomposition. No hand assembly: every
rate divides by len(firsts), probe/clean/redraw rows never enter (callers
filter to int-idx first draws before calling).

Reliability classes: hijacked | works | works-partial | fate-unclear |
fate-silent | garble-rejected | manifest-silence. See build_table.
"""
from __future__ import annotations

from collections import Counter
from typing import Any


def build_table(firsts: list[dict[str, Any]], bodies: dict[int, str],
                queues: dict[int, str], manifest_absent: set[int],
                inventory: dict[str, int],
                browser_evs: list[dict[str, Any]]) -> dict[str, Any]:
    """Assemble the table. firsts: 109 first-draw verdict rows with keys
    idx/do/bucket/detail/peak/audio. bodies: idx->command body (for display).
    queues: idx->queue name. manifest_absent: idx set missing from the
    installed manifest. inventory: audio filename->bytes (fate baseline).
    browser_evs: browser evidence rows (fate baseline + redraw tallies)."""
    from harness.live import voice_gate as vg

    table = []
    for r in sorted(firsts, key=lambda x: x["idx"]):
        i = r["idx"]
        do = r.get("do", "?")
        bucket = r["bucket"]
        if bucket == "known-bug" and do != "misroute":
            do = "misroute"  # wrong-target is misroute, whatever the facet said
        if bucket == "known-bug" or do == "misroute":
            rel = "hijacked"
        elif bucket == "member-matched":
            rel = "works"
        elif bucket == "expected-silence" or do == "resolved-404":
            rel = "works-partial"
        elif bucket == "fragment-inconclusive":
            rel = "fate-unclear"
        elif do == "no-match" and i in manifest_absent:
            rel = "manifest-silence"
        elif do == "no-match":
            rel = "garble-rejected"
        else:
            rel = "fate-silent"
        table.append({"idx": i, "body": bodies.get(i, ""),
                      "queue": queues.get(i), "do": do,
                      "route_audio": r.get("audio"), "say_bucket": bucket,
                      "reliability": rel, "peak": r.get("peak"),
                      "detail": (r.get("detail") or "")[:200]})
    aud = tot = 0
    for e in browser_evs:
        if not isinstance(e.get("idx"), int):
            continue
        if vg.draw_expectation(e.get("member_bytes", 10 ** 9)) == "audible":
            tot += 1
            aud += (e["rb_peak"] >= vg.ONSET_PEAK_TH)
    return {"n": len(table),
            "buckets": dict(Counter(r["say_bucket"] for r in table)),
            "do_facets": dict(Counter(r["do"] for r in table)),
            "reliability": dict(Counter(r["reliability"] for r in table)),
            "fate_baseline": {"audible": aud, "of": tot},
            "rows": table}
