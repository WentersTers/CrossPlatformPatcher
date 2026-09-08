"""Qwen-CUA rule exact: 20 active; 21st folds oldest 10 deterministically."""
from harness.workers.screenshot_history import ScreenshotHistory


def _actions(n, start=1):
    kinds = ["click 100,200", "launch app", "assert_state(idle=pass)",
             "type hello", "exec ls"]
    return [kinds[(start + i) % len(kinds)] for i in range(n)]


def test_no_fold_at_20():
    h = ScreenshotHistory()
    for i, a in enumerate(_actions(20)):
        h.add(f"shot-{i:02d}.png", a)
    assert len(h.active) == 20
    assert h.folded_placeholders == []
    assert len(h.full_text_log) == 20  # text never folded


def test_fold_oldest_10_on_21st_deterministic():
    h = ScreenshotHistory()
    acts = _actions(21)
    for i, a in enumerate(acts):
        h.add(f"shot-{i:02d}.png", a)
    assert len(h.active) == 11  # 21 - 10 folded
    assert len(h.folded_placeholders) == 1
    ph = h.folded_placeholders[0]
    assert ph.startswith("[folded steps 01-10:")
    assert "see artifacts" in ph
    # deterministic: same actions -> same placeholder
    h2 = ScreenshotHistory()
    for i, a in enumerate(acts):
        h2.add(f"shot-{i:02d}.png", a)
    assert h2.folded_placeholders == h.folded_placeholders
    # full text log retained entirely
    assert len(h.full_text_log) == 21
    # context = placeholders + active
    ctx = h.get_context()
    assert ctx[0] == ph
    assert len(ctx) == 12


def test_second_fold_window():
    h = ScreenshotHistory()
    for i, a in enumerate(_actions(31)):
        h.add(f"shot-{i:02d}.png", a)
    assert len(h.folded_placeholders) == 2
    assert h.folded_placeholders[1].startswith("[folded steps 11-20:")
    assert len(h.full_text_log) == 31
