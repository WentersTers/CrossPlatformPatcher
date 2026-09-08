"""type(text) guards (§8): ~50 chars/s scancode US; non-ASCII/>200 via QGA put-file;
detects Wayland via XDG_SESSION_TYPE and refuses xdotool paths."""
from __future__ import annotations

from dataclasses import dataclass


@dataclass
class TypePlan:
    route: str  # xdotool | ydotool/wtype | qga-put-file
    est_delay_s: float


def route_typing(text: str, session_type: str = "x11") -> TypePlan:
    non_ascii = any(ord(c) > 127 for c in text)
    if non_ascii or len(text) > 200:
        return TypePlan("qga-put-file", 0.0)
    st = (session_type or "x11").lower()
    if "wayland" in st:
        return TypePlan("ydotool/wtype", len(text) / 50.0)
    return TypePlan("xdotool", len(text) / 50.0)


def type_text(text: str, session_type: str = "x11",
              xdotool_fn=None, wtype_fn=None) -> TypePlan:
    """Refuses xdotool on Wayland (raises if caller forces wrong backend)."""
    plan = route_typing(text, session_type)
    if "wayland" in (session_type or "").lower() and xdotool_fn is not None and plan.route != "xdotool":
        raise ValueError("Refusing xdotool on Wayland: use ydotool/wtype only")
    return plan
