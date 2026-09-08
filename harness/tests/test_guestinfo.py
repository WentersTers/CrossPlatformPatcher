"""Guest IP is a lookup through QGA introspection, never config."""
import pytest

from harness.tools.guestinfo import GuestInfoError, parse_ipv4, resolve_guest_ip


# shape of the real Session-1 answer: lo + MAC-only ens3 (pre-DHCP)
PRE_DHCP = [
    {"name": "lo",
     "ip-addresses": [{"ip-address-type": "ipv4", "ip-address": "127.0.0.1"},
                      {"ip-address-type": "ipv6", "ip-address": "::1"}]},
    {"name": "ens3", "hardware-address": "52:54:00:b3:41:6a",
     "ip-addresses": []},
]

LEASED = PRE_DHCP + [
    {"name": "primary",
     "ip-addresses": [{"ip-address-type": "ipv4",
                       "ip-address": "192.168.122.112"}]},
]


def test_loopback_and_down_interfaces_skipped():
    assert parse_ipv4(PRE_DHCP) is None  # no address yet, never 0.0.0.0
    assert parse_ipv4(LEASED) == "192.168.122.112"
    assert parse_ipv4([]) is None
    v6only = [{"name": "eth0",
               "ip-addresses": [{"ip-address-type": "ipv6", "ip-address": "::1"}]}]
    assert parse_ipv4(v6only) is None


def test_resolve_raises_until_leased():
    with pytest.raises(GuestInfoError, match="no DHCP lease"):
        resolve_guest_ip(lambda: PRE_DHCP)
    assert resolve_guest_ip(lambda: LEASED) == "192.168.122.112"
