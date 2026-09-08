"""Gate 1 — deterministic, injection-immune (§6).

- exit_code == 0
- env logged every step: PAICOM_MIGRATION_MODE == full,
  PAICOM_RUNTIME_VERIFIED_64BIT == 1 (silent Vosk failure is usually env gating)
- files: extracted .so exists, +x bit, ldd clean at first boot;
  first-run vs warm-cache exercised as a test pair
- transcript match: injected-audio Whisper reference vs app Vosk output,
  word-level with tolerance (lowercase/no-punct normalized)
"""
from __future__ import annotations

import re
from dataclasses import dataclass, field

REQUIRED_ENV = {
    "PAICOM_MIGRATION_MODE": "full",
    "PAICOM_RUNTIME_VERIFIED_64BIT": "1",
}

_WORD = re.compile(r"[a-z0-9']+")


def normalize_words(text: str) -> list[str]:
    return _WORD.findall(text.lower())


def word_error_rate(ref: list[str], hyp: list[str]) -> float:
    """Levenshtein WER on word lists."""
    if not ref:
        return 0.0 if not hyp else 1.0
    prev = list(range(len(hyp) + 1))
    for i, rw in enumerate(ref, 1):
        cur = [i]
        for j, hw in enumerate(hyp, 1):
            cur.append(min(prev[j] + 1, cur[-1] + 1,
                           prev[j - 1] + (0 if rw == hw else 1)))
        prev = cur
    return prev[-1] / len(ref)


def transcript_match(reference: str, hypothesis: str, tolerance_wer: float = 0.2) -> tuple[bool, float]:
    ref_w = normalize_words(reference)
    hyp_w = normalize_words(hypothesis)
    wer = word_error_rate(ref_w, hyp_w)
    return wer <= tolerance_wer, wer


@dataclass
class Gate1Result:
    passed: bool
    failures: list[str] = field(default_factory=list)
    evidence: dict = field(default_factory=dict)


def check_exit_code(exit_code: int) -> str | None:
    return None if exit_code == 0 else f"exit_code={exit_code} != 0"


def check_env(env: dict[str, str]) -> list[str]:
    failures = []
    for k, want in REQUIRED_ENV.items():
        got = env.get(k)
        if got != want:
            failures.append(f"env {k}={got!r} != {want!r}")
    return failures


@dataclass
class NativeLibInfo:
    path: str
    exists: bool
    executable: bool
    ldd_clean: bool


def check_native_libs(libs: list[NativeLibInfo], first_boot: bool) -> list[str]:
    failures = []
    for lib in libs:
        if not lib.exists:
            failures.append(f"missing extracted lib {lib.path}")
        if not lib.executable:
            failures.append(f"lib not +x: {lib.path}")
        if first_boot and not lib.ldd_clean:
            failures.append(f"ldd not clean (first boot): {lib.path}")
    return failures


def evaluate(step: dict, first_boot: bool = False,
             tolerance_wer: float = 0.2) -> Gate1Result:
    """step keys: exit_code, env, libs (list[NativeLibInfo] or list[dict]),
    ref_transcript, hyp_transcript (optional pair)."""
    failures: list[str] = []
    evidence: dict = {}

    ec = step.get("exit_code", 1)
    if (f := check_exit_code(ec)) is not None:
        failures.append(f)
    evidence["exit_code"] = ec

    env = step.get("env", {})
    ef = check_env(env)
    failures.extend(ef)
    evidence["env"] = {k: env.get(k) for k in REQUIRED_ENV}

    raw_libs = step.get("libs", [])
    libs: list[NativeLibInfo] = []
    for item in raw_libs:
        if isinstance(item, NativeLibInfo):
            libs.append(item)
        else:
            libs.append(NativeLibInfo(**item))
    lf = check_native_libs(libs, first_boot)
    failures.extend(lf)
    evidence["libs_checked"] = len(libs)

    ref = step.get("ref_transcript")
    hyp = step.get("hyp_transcript")
    if ref is not None or hyp is not None:
        ok, wer = transcript_match(ref or "", hyp or "", tolerance_wer)
        evidence["wer"] = wer
        if not ok:
            failures.append(f"transcript WER {wer:.3f} > {tolerance_wer}")

    log_text = step.get("log_text")
    if log_text is not None:
        for pat in step.get("log_must_match", []):
            if not re.search(pat, log_text):
                failures.append(f"log missing required pattern {pat!r}")
        for pat in step.get("log_must_not_match", []):
            if re.search(pat, log_text):
                failures.append(f"log matched forbidden pattern {pat!r}")
        evidence["log_bytes"] = len(log_text)

    return Gate1Result(passed=not failures, failures=failures, evidence=evidence)
