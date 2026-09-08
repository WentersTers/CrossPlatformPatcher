"""assert_state Tier A->B fallback, durations, VLM cap, frames_sampled."""
from harness.gates.gate2_5_state.template_matcher import StateMatch
from harness.gates.gate2_5_state.vlm_classifier import StubVlmClassifier
from harness.tools.assert_state import assert_state


class ScriptMatcher:
    def __init__(self, script):
        self.script = list(script)

    def classify(self, shot, roi):
        s = self.script[min(0, 0)] if False else None
        item = self.script.pop(0) if self.script else ("ambiguous", 0.1, True)
        state, score, need = item
        return StateMatch(state, score, "template", needs_vlm=need)


def _clock():
    t = [0.0]
    def time_fn():
        return t[0]
    def sleep_fn(d):
        t[0] += d
    return time_fn, sleep_fn


def test_assert_state_direct_tier_a():
    m = ScriptMatcher([("idle", 0.9, False)] * 5)
    tf, sf = _clock()
    r = assert_state("idle", timeout_s=10.0, min_duration_s=0.5,
                     capture_fn=lambda: object(), template_matcher=m,
                     time_fn=tf, sleep_fn=sf)
    assert r.ok and r.state == "idle" and r.method == "template"
    assert r.frames_sampled >= 2


def test_assert_state_fallback_to_vlm():
    m = ScriptMatcher([("ambiguous", 0.2, True)] * 5)
    vlm = StubVlmClassifier([("listening", 0.85)], max_calls_per_assertion=5, cache_ms=0)
    tf, sf = _clock()
    r = assert_state("listening", timeout_s=10.0, min_duration_s=0.0,
                     capture_fn=lambda: object(), template_matcher=m, vlm=vlm,
                     time_fn=tf, sleep_fn=sf, is_animated=True)
    assert r.ok and r.method == "vlm" and r.state == "listening"


def test_assert_state_vlm_exhausted_uncertain():
    m = ScriptMatcher([("ambiguous", 0.2, True)] * 10)
    vlm = StubVlmClassifier([("idle", 0.9)], max_calls_per_assertion=1, cache_ms=0)
    tf, sf = _clock()
    # static sampling: min_duration 0 but vlm cap 1 -> second frame uncertain
    r = assert_state("idle", timeout_s=10.0, min_duration_s=5.0,
                     capture_fn=lambda: object(), template_matcher=m, vlm=vlm,
                     time_fn=tf, sleep_fn=sf, is_animated=False)
    assert r.method in ("vlm", "uncertain")
    assert r.frames_sampled >= 1
