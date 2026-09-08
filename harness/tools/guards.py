"""Shared tool errors. Silent failure is a tool-implementation bug (§8)."""
from __future__ import annotations


class StaleScreenshotError(Exception):
    pass


class NoVisualChangeWarning(Warning):
    pass


class ToolResult(dict):
    pass
