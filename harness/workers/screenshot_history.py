"""20-active screenshot history with deterministic 10-fold (Qwen-CUA rule, exact).

On the 21st screenshot, fold the oldest 10 into a deterministic placeholder
derived from the text action log — no LLM summarization in the fold.
Text action/log history is never folded.
"""
from __future__ import annotations

from collections import Counter
from dataclasses import dataclass, field


def _classify_action(entry: str) -> str:
    e = entry.strip().lower()
    if e.startswith("click"):
        return "clicks"
    if e.startswith("launch") or "launch" in e.split(":")[0]:
        return "launch"
    if e.startswith("assert_state_sequence"):
        return "assert_state_sequence"
    if e.startswith("assert_state"):
        # keep state name if present e.g. assert_state(idle=pass)
        return "assert_state"
    if e.startswith("type"):
        return "types"
    if e.startswith("exec"):
        return "execs"
    if e.startswith("wait"):
        return "waits"
    return "actions"


@dataclass
class ScreenshotHistory:
    max_active: int = 20
    fold_batch: int = 10
    active: list[str] = field(default_factory=list)
    folded_placeholders: list[str] = field(default_factory=list)
    full_text_log: list[str] = field(default_factory=list)
    _fold_counter: int = 0
    _dropped_count: int = 0

    def add(self, screenshot_ref: str, action_log_entry: str) -> None:
        """Record one step. screenshot_ref is an artifact path/ID."""
        self.full_text_log.append(action_log_entry)
        self.active.append(screenshot_ref)
        if len(self.active) > self.max_active:
            self._fold_oldest()

    def _fold_oldest(self) -> None:
        oldest_imgs = self.active[: self.fold_batch]
        # text entries corresponding to the folded images: the oldest fold_batch
        # text entries not yet folded. Track via _dropped_count.
        start = self._dropped_count
        entries = self.full_text_log[start: start + self.fold_batch]
        counts = Counter(_classify_action(e) for e in entries)
        # detail assert_state states
        state_bits: list[str] = []
        for e in entries:
            el = e.strip().lower()
            if el.startswith("assert_state"):
                # extract inside parens if present
                inner = e[e.find("(") + 1: e.find(")")] if "(" in e else ""
                state_bits.append(f"assert_state({inner})" if inner else "assert_state")
        n_start = start + 1
        n_end = start + len(entries)
        parts: list[str] = []
        for k in ("clicks", "types", "execs", "launch", "assert_state",
                  "assert_state_sequence", "waits", "actions"):
            if counts.get(k):
                if k == "assert_state" and state_bits:
                    parts.append(f"{len([b for b in state_bits])} {k}({', '.join(state_bits)})")
                else:
                    parts.append(f"{counts[k]} {k}")
        summary = ", ".join(parts) if parts else "no-ops"
        self._fold_counter += 1
        placeholder = (
            f"[folded steps {n_start:02d}-{n_end:02d}: {summary}; "
            f"see artifacts/…; full text log retained]"
        )
        self.folded_placeholders.append(placeholder)
        self.active = self.active[self.fold_batch:]
        self._dropped_count += len(entries)
        _ = oldest_imgs  # refs retained via artifact store, not in context

    def get_context(self) -> list[str]:
        """Context-ready list: folded placeholders + active refs."""
        return list(self.folded_placeholders) + list(self.active)

    @property
    def folded_text_log(self) -> list[str]:
        return list(self.full_text_log)
