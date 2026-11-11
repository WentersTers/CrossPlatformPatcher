#!/usr/bin/env python3
"""ISS-046 guard: refuse a void bundle at provision time.

A bundle is "void" when the patcher produced a memo-gate-unfixed binary —
i.e. the two 7nbx materialization resource entries are absent. Historically
the repo patcher lacked this fix (REV-041), so every fresh provision silently
produced a void bundle (L13's 8699d930: 240 MB, all-platform natives, zero
7nbx ADDs). The fix now lives in Core/MaterializationResourceFixer.cs; this
guard prevents recurrence from ANY path (patch, chain-exe swap, skip-app,
future regressions).

Primary check (resdump-equivalent ADD presence): both embedded resource names
are present in the module metadata strings.
  - 7nbx}a5?U:!^=d#]"~1pIQ;X.resources    (24-stem dialog-pinned)
  - 7nbx}a5?U:!^=d#]"~1pIQ;X&.resources   (26-char metadata-type)
The pristine's linked entry (7nbx...\\]\\&.resources) contains backslashes, so
it does NOT contain either ADD name as a substring — presence of the exact
ADD name is therefore a true ADD indicator.

Secondary check (size sanity): a near-zero or absurdly small input is
rejected; the fixed reference build is ~89 MB lean (o1craft) / ~240 MB with
all-platform natives (repo patcher). Size is a WARNING only — it cannot
distinguish void from fixed on the repo patcher.

Usage:
    python3 check_materialization.py <patched-exe> [--json]
Exit: 0 = has both ADDs (OK) | 1 = void (missing ADD) | 2 = unreadable/usage
"""
from __future__ import annotations

import json
import os
import sys

STEM = '7nbx}a5?U:!^=d#]"~1pIQ;X'
ADD_DIALOG = STEM + '.resources'
ADD_TYPE = STEM + '&.resources'

# Soft floor for the size sanity warning (bytes). Fixed references are far
# above this; anything under it is almost certainly wrong.
SIZE_WARN_FLOOR = 50 * 1024 * 1024  # 50 MB


def check(path: str) -> dict:
    result = {
        "path": path,
        "ok": False,
        "has_dialog_add": False,
        "has_type_add": False,
        "size": 0,
        "size_warning": False,
        "reason": "",
    }
    if not os.path.isfile(path):
        result["reason"] = "file not found"
        return result

    size = os.path.getsize(path)
    result["size"] = size
    if size == 0:
        result["reason"] = "zero-byte file"
        return result

    with open(path, "rb") as fh:
        data = fh.read()

    has_dialog = ADD_DIALOG.encode("utf-8") in data
    has_type = ADD_TYPE.encode("utf-8") in data
    result["has_dialog_add"] = has_dialog
    result["has_type_add"] = has_type
    result["size_warning"] = size < SIZE_WARN_FLOOR

    if has_dialog and has_type:
        result["ok"] = True
        result["reason"] = "both 7nbx materialization ADDs present"
    else:
        missing = []
        if not has_dialog:
            missing.append("dialog-pinned ADD")
        if not has_type:
            missing.append("metadata-type ADD")
        result["reason"] = "missing " + ", ".join(missing)
    return result


def main(argv: list[str]) -> int:
    as_json = "--json" in argv
    paths = [a for a in argv if not a.startswith("--")]
    if not paths:
        print("usage: check_materialization.py <patched-exe> [--json]", file=sys.stderr)
        return 2

    res = check(paths[0])
    if as_json:
        print(json.dumps(res, indent=2))
    else:
        if res["ok"]:
            print(f"[materialization-guard] OK  {res['path']}  ({res['size']} bytes; both ADD names present)")
        else:
            print(f"[materialization-guard] VOID  {res['path']}  ({res['size']} bytes; {res['reason']})")
        if res["size_warning"]:
            print(f"[materialization-guard] WARN  size {res['size']} < {SIZE_WARN_FLOOR} bytes — implausibly small for a fixed bundle")

    if res["ok"]:
        return 0
    if res["size"] == 0 or res["reason"] == "file not found":
        return 2
    return 1


if __name__ == "__main__":
    raise SystemExit(main(sys.argv[1:]))
