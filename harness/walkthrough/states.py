"""Walk states for the harness walkthrough engine (REV-035 state machine)."""
from __future__ import annotations

from enum import Enum


class WalkState(str, Enum):
    S0_BOOT = "S0_boot"
    S1_SETTLE = "S1_settle"
    S2_YES_WALL = "S2_yes_wall"
    S3_CREDITS = "S3_credits"
    S4_WALL = "S4_wall"
    S5_POST_CLICK = "S5_post_click"
    S6_MENU = "S6_menu"
    S7_PET = "S7_pet"
    # failure track
    S6_BLANK = "S6_blank"
    S8_BUBBLE = "S8_bubble_dismissal"
    S9_INTEGRITY = "S9_integrity_unknown"
    S10_EXCEPTION = "S10_exception"
    # mandatory abstain
    SX_UNKNOWN = "SX_unknown"


# Ordered happy path (for documentation / expected-sequence checks).
HAPPY_PATH: tuple[WalkState, ...] = (
    WalkState.S0_BOOT,
    WalkState.S1_SETTLE,
    WalkState.S2_YES_WALL,
    WalkState.S3_CREDITS,
    WalkState.S4_WALL,
    WalkState.S5_POST_CLICK,
    WalkState.S6_MENU,
    WalkState.S7_PET,
)

FAILURE_STATES: frozenset[WalkState] = frozenset({
    WalkState.S6_BLANK,
    WalkState.S8_BUBBLE,
    WalkState.S9_INTEGRITY,
    WalkState.S10_EXCEPTION,
})

# All states the classifier may emit, including abstain.
WALK_STATES: frozenset[WalkState] = frozenset(WalkState)

# The abstain sentinel.
SX = WalkState.SX_UNKNOWN


def is_abstain(state: WalkState | str) -> bool:
    s = state.value if isinstance(state, WalkState) else str(state)
    return s == WalkState.SX_UNKNOWN.value


def is_failure(state: WalkState | str) -> bool:
    s = state.value if isinstance(state, WalkState) else str(state)
    return s in {x.value for x in FAILURE_STATES}
