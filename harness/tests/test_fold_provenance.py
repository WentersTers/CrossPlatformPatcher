"""Fold provenance contract (spec-trap fix): the 10-fold must be programmatically
derived from the text action log. An LLM/model call in this path reintroduces
the telephone game the Qwen-CUA rule exists to prevent."""
import pathlib


def test_fold_path_has_no_model_calls():
    import re
    src = pathlib.Path(__file__).resolve().parent.parent / "workers" / "screenshot_history.py"
    text = src.read_text(encoding="utf-8")
    # strip docstrings/comments: only code tokens count (prose may say "no LLM")
    code = re.sub(r'"""[\s\S]*?"""', "", text)
    code = re.sub(r"#[^\n]*", "", code).lower()
    for banned in ("openai", "anthropic", "langchain", "transformer",
                   "import llm", "llm.", "summariz", "inference"):
        assert banned not in code, f"banned {banned!r} in fold code path"


def test_fold_derived_from_log():
    from harness.workers.screenshot_history import ScreenshotHistory
    h1, h2 = ScreenshotHistory(), ScreenshotHistory()
    acts = ["click 1,2"] * 10 + ["type hi"] * 11
    for i, a in enumerate(acts):
        h1.add(f"s{i}", a)
    for i, a in enumerate(reversed(acts)):
        h2.add(f"s{i}", a)
    # different logs -> different placeholders (derived, not canned)
    assert h1.folded_placeholders != h2.folded_placeholders
