"""Meta-contract: repo-wide hygiene the suite enforces on itself."""
import pathlib
import re

HARNESS = pathlib.Path(__file__).resolve().parent.parent

_BROKEN_TYPING = re.compile(r"^from typing (?!import)\w", re.MULTILINE)


def test_no_broken_typing_imports():
    """`from typing X` (missing `import`) is a SyntaxError at collection time
    that presents as a whole-module collection error. Pin the pattern so the
    typo fails as one clear assertion instead of N collection errors."""
    offenders = [str(p.relative_to(HARNESS)) for p in HARNESS.rglob("*.py")
                 if _BROKEN_TYPING.search(p.read_text(encoding="utf-8"))]
    assert offenders == [], f"broken typing imports in: {offenders}"
