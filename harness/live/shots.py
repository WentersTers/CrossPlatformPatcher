"""Two-leg screenshot pull over the hop (observation plane, Session 7).

Leg 1 (host): `virsh screenshot` writes a file ON THE HOST — never on the
console. Leg 2 (pull): scp to the console, verified by format sniff
(PPM/PNG magic, tools/screenshot.py) AND host-vs-local md5 (Session-1:
every delivery channel gets effect verification, scp included).

A naive argv-prefix swap of the local screenshot call sites would "succeed"
while stranding the artifact on the host — this helper makes the second leg
structural. All failures are loud (ScreenshotError), never absent files.
"""
from __future__ import annotations

import hashlib
from pathlib import Path

from harness.tools.screenshot import sniff_format


class ScreenshotError(Exception):
    """Leg-1 failure, scp failure, unknown magic, or md5 mismatch."""


def pull_screenshot(*, virsh_prefix: list[str], ssh_prefix: list[str],
                    ssh_target: str, domain: str, remote_path: str,
                    local_path: str | Path,
                    run, fetch) -> dict:
    """run(cmd: list[str]) -> (rc, out, err); fetch(target, remote, local)
    performs the scp (raises on transport failure). Returns artifact facts."""
    rc, _out, err = run([*virsh_prefix, "screenshot", domain, remote_path])
    if rc != 0:
        raise ScreenshotError(
            f"leg-1 capture failed for {domain!r}: {err.strip()[:300]}")
    fetch(ssh_target, remote_path, str(local_path))
    data = Path(local_path).read_bytes()
    fmt = sniff_format(data[:8])
    if fmt == "unknown":
        raise ScreenshotError(
            f"pulled {len(data)} bytes for {domain!r} but magic is not PPM/PNG")
    rc, out, err = run([*ssh_prefix, "md5sum", remote_path])
    parts = out.split()
    if rc != 0 or not parts:
        raise ScreenshotError(
            f"host md5sum failed for {remote_path!r}: {err.strip()[:300]}")
    want = parts[0].lower()
    got = hashlib.md5(data).hexdigest()
    if got != want:
        raise ScreenshotError(
            f"md5 mismatch on {domain!r} screenshot: local {got} != host {want}")
    try:
        run([*ssh_prefix, "rm", "-f", remote_path])
    except Exception:
        pass
    return {"domain": domain, "remote_path": remote_path,
            "local_path": str(local_path), "bytes": len(data),
            "fmt": fmt, "md5": got, "via": "ssh-hop"}
