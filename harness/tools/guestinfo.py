"""Guest address discovery via QGA introspection (Session-2 rule).

192.168.122.112 is today's DHCP answer, not a constant. exec/get_logs
resolve the guest address through guest-network-get-interfaces on every run —
never from config, never cached across boots.
"""
from __future__ import annotations

from typing import Callable


class GuestInfoError(Exception):
    pass


def parse_ipv4(interfaces: list[dict]) -> str | None:
    """First non-loopback IPv4 across interfaces, or None (no address yet —
    e.g. pre-DHCP boot counts as 'not yet', never as '0.0.0.0')."""
    for iface in interfaces:
        for addr in iface.get("ip-addresses", []):
            if addr.get("ip-address-type") != "ipv4":
                continue
            ip = addr.get("ip-address", "")
            if ip and not ip.startswith("127."):
                return ip
    return None


def resolve_guest_ip(query_fn: Callable[[], list[dict]]) -> str:
    """query_fn returns guest-network-get-interfaces output. Raises
    GuestInfoError when the guest has no address (caller waits/retries)."""
    ip = parse_ipv4(query_fn())
    if ip is None:
        raise GuestInfoError("guest has no IPv4 address yet (no DHCP lease?)")
    return ip
