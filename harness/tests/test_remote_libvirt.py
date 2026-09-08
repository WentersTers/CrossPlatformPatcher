"""Remote-libvirt contract (Q1): URI validation, typed failures, domain listing.

Pure connection-layer — no VM host needed. Swap #1 (real Linux host) must
satisfy this contract: same URIs, same typed errors, same listing shape.
"""
import pytest

from harness.vms.pool import (LibvirtConnectionError, VmPool,
                              parse_libvirt_uri)


class FakeSession:
    def __init__(self, domains):
        self._domains = list(domains)

    def list_domains(self):
        return list(self._domains)


class FakeTransport:
    def __init__(self, domains=("ubuntu-22.04", "windows-11"), fail_with=None):
        self.domains = domains
        self.fail_with = fail_with
        self.seen_uris: list[str] = []

    def connect(self, uri):
        self.seen_uris.append(uri)
        if self.fail_with is not None:
            raise self.fail_with
        return FakeSession(self.domains)


def test_parse_remote_and_local_uris():
    info = parse_libvirt_uri("qemu+ssh://operator@linux-host:22/system")
    assert info["remote"] and info["host"] == "linux-host"
    assert info["user"] == "operator" and info["scheme"] == "qemu+ssh"
    local = parse_libvirt_uri("qemu:///system")
    assert local["remote"] is False
    # ssh tuning params ride through validation untouched (Session-0 auth)
    keyed = parse_libvirt_uri(
        "qemu+ssh://operator@linux-host/system?keyfile=/home/op/.ssh/id_ed25519"
        "&known_hosts=/home/op/.ssh/known_hosts")
    assert "keyfile=" in keyed["query"] and "known_hosts=" in keyed["query"]
    assert local["remote"] is False
    with pytest.raises(ValueError):
        parse_libvirt_uri("hyperv://windows-host/vms")  # unsupported scheme
    with pytest.raises(ValueError):
        parse_libvirt_uri("qemu+ssh:///system")  # remote without host
    with pytest.raises(ValueError):
        parse_libvirt_uri("")


def test_connect_and_list_via_transport():
    pool = VmPool(transport=FakeTransport())
    res = pool.connect("qemu+ssh://operator@linux-host:22/system")
    assert res["host"] == "linux-host" and res["backend"] == "transport"
    assert pool.list_domains() == ["ubuntu-22.04", "windows-11"]


def test_connection_failure_is_typed_not_crash():
    pool = VmPool(transport=FakeTransport(fail_with=OSError("no route to host")))
    with pytest.raises(LibvirtConnectionError, match="no route"):
        pool.connect("qemu+ssh://operator@dead-host/system")


def test_listing_before_connect_is_typed():
    pool = VmPool(transport=FakeTransport())
    with pytest.raises(LibvirtConnectionError, match="not connected"):
        pool.list_domains()


def test_no_libvirt_no_transport_actionable():
    pool = VmPool()
    if pool.libvirt_available:
        pytest.skip("real libvirt present on this host")
    with pytest.raises(LibvirtConnectionError, match="Linux host"):
        pool.connect("qemu+ssh://operator@linux-host/system")
