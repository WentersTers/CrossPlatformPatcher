"""Single source of truth for virsh argv on this console (§9).

Local:  ["virsh", "-c", <uri>, ...]
Hop:    ["ssh", <opts>, [user@]host, "virsh", "-c", "qemu:///system", ...]

The hop is the CONTROL plane (~280ms/round-trip measured). It must never
become the per-event OBSERVATION plane silently: screenshot pulls are two
legs (host-side capture + scp pull, see live/shots.py), and per-event QMP
input over the hop pays ~280ms/event honestly measured in the send log —
the host-local monitor socket stays the fast path.
"""
from __future__ import annotations

from harness.vms.pool import LibvirtConnectionError, parse_libvirt_uri

REMOTE_URI = "qemu:///system"


def prefix_for(uri: str, ssh_hop: bool = False,
               ssh_opts: list[str] | None = None,
               connect_timeout_s: int = 10) -> list[str]:
    """Argv prefix for a virsh invocation. Raises LibvirtConnectionError
    (never silently degrades) on bad URIs and local-URI+hop combos."""
    if not ssh_hop:
        return ["virsh", "-c", uri]
    try:
        info = parse_libvirt_uri(uri)
    except ValueError as e:
        raise LibvirtConnectionError(f"bad URI: {e}") from e
    if not info["remote"]:
        raise LibvirtConnectionError(
            f"ssh-hop needs a remote URI, got local {uri!r}; "
            "use VirshTransport for local libvirt")
    target = f"{info['user']}@{info['host']}" if info["user"] else info["host"]
    cmd = ["ssh", "-o", "BatchMode=yes",
           "-o", f"ConnectTimeout={connect_timeout_s}",
           "-o", "StrictHostKeyChecking=accept-new"]
    if info["port"]:
        cmd += ["-p", str(info["port"])]
    cmd += list(ssh_opts or [])
    return cmd + [target, "virsh", "-c", REMOTE_URI]
