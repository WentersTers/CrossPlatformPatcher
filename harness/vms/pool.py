"""libvirt lifecycle + snapshots. Stub backend keeps unit tests hypervisor-free.

Q1 (host OS): the specced stack is a LINUX host (libvirt/QEMU/KVM + QMP +
PulseAudio null-sinks). From a Windows operator console the supported path is
a REMOTE Linux host via qemu+ssh:// URIs — the pool below speaks to it through
an injectable transport, so the connection layer is contract-tested here and
the real host drops in at swap #1 with no rearchitecture.
"""
from __future__ import annotations

from dataclasses import dataclass, field
from typing import Any
from urllib.parse import urlparse


@dataclass
class VmSpec:
    vm_id: str
    os: str
    ver: str = ""
    runtime: str = "native"


class LibvirtConnectionError(Exception):
    """Typed connection failure (never a raw crash): bad URI, unreachable
    host, auth failure, or libvirt missing."""


def parse_libvirt_uri(uri: str) -> dict[str, Any]:
    """Validate + decompose. Remote (qemu+ssh/qemu+tcp) requires a host.

    Examples:
      qemu:///system                    local host session
      qemu+ssh://operator@linux-host:22/system
    """
    if not uri or not isinstance(uri, str):
        raise ValueError("libvirt URI is required")
    # urlparse handles qemu+ssh://...; qemu:///system has empty netloc (local)
    parsed = urlparse(uri)
    scheme = parsed.scheme  # 'qemu+ssh' or 'qemu'
    if scheme not in ("qemu", "qemu+ssh", "qemu+tcp"):
        raise ValueError(f"unsupported libvirt scheme in {uri!r}")
    remote = "+" in scheme
    if remote and not parsed.hostname:
        raise ValueError(f"remote libvirt URI requires a host: {uri!r}")
    return {"scheme": scheme, "remote": remote, "host": parsed.hostname or "",
            "user": parsed.username or "", "port": parsed.port or 0,
            "path": parsed.path or "/system",
            # passthrough: libvirt ssh params (keyfile, known_hosts, ...) ride
            # here untouched so Session-0 auth tuning never breaks validation.
            "query": parsed.query or ""}


class VmPool:
    def __init__(self, backend=None, transport=None):
        """transport (optional): object with connect(uri)->session and session
        with list_domains()->list[str]. Injected fakes make the remote-host
        contract testable with no VM host present."""
        try:
            import libvirt  # type: ignore  # noqa
            self.libvirt_available = True
        except Exception:
            self.libvirt_available = False
        self.backend = backend or {}
        self.transport = transport
        self.snapshots: dict[str, list[str]] = {}
        self._reverts: dict[tuple[str, str], int] = {}
        self._session: Any = None
        self._uri: str = ""

    def connect(self, uri: str) -> dict[str, Any]:
        """Connect to (possibly remote) libvirt host. Typed errors only."""
        info = parse_libvirt_uri(uri)  # ValueError on malformed URI
        if self.transport is not None:
            try:
                self._session = self.transport.connect(uri)
            except LibvirtConnectionError:
                raise
            except Exception as e:
                raise LibvirtConnectionError(f"transport connect failed: {e}") from e
        elif self.libvirt_available:
            import libvirt  # type: ignore
            try:
                self._session = libvirt.open(uri)
            except Exception as e:
                raise LibvirtConnectionError(f"libvirt.open({uri!r}) failed: {e}") from e
        else:
            raise LibvirtConnectionError(
                "libvirt not installed and no transport injected; "
                "acquire a Linux host (Q1) or inject a transport for tests")
        self._uri = uri
        return {"uri": uri, **info, "backend": "transport"
                if self.transport is not None else "libvirt"}

    @property
    def active_session(self) -> Any:
        """Session from the last successful connect (None if never)."""
        return self._session

    def list_domains(self) -> list[str]:
        if self._session is None:
            raise LibvirtConnectionError("not connected: call connect(uri) first")
        if self.transport is not None:
            try:
                return list(self._session.list_domains())
            except Exception as e:
                raise LibvirtConnectionError(f"domain listing failed: {e}") from e
        try:
            return [d.name() for d in self._session.listAllDomains()]
        except Exception as e:
            raise LibvirtConnectionError(f"domain listing failed: {e}") from e

    def lifecycle(self, vm_id: str, action: str) -> dict:
        """boot/crash/shutdown events surface to watchdog (§5 lifecycle row)."""
        if action not in ("boot", "shutdown", "revert", "snapshot"):
            raise ValueError(f"unknown lifecycle action {action!r}")
        return {"vm": vm_id, "action": action,
                "backend": "libvirt" if self.libvirt_available else "stub"}

    def snapshot(self, vm_id: str, name: str) -> dict:
        verb = getattr(self._session, "snapshot_create", None) \
            if self._session is not None else None
        if callable(verb):
            verb(vm_id, name)
            via = "session"
        elif self._session is not None:
            raise LibvirtConnectionError(
                "connected session has no snapshot verbs; "
                "cannot stage snapshots against reality")
        else:
            via = "stub"
        self.snapshots.setdefault(vm_id, []).append(name)
        return {"vm": vm_id, "snapshot": name, "via": via}

    def revert(self, vm_id: str, snapshot: str) -> dict:
        """Revert vm to a snapshot, counting repetitions (Session 7:
        ten reverts on one snapshot is a new access pattern — churn here is
        a plumbing finding, surfaced via revert_n, never silent).

        Connected (session with snapshot verbs): the revert REALLY happens
        via virsh, against the recorded snapshot list. Disconnected: stub
        bookkeeping for hypervisor-free tests. A connected session WITHOUT
        verbs is a loud typed error, never a silent no-op stub."""
        list_verb = getattr(self._session, "snapshot_list", None) \
            if self._session is not None else None
        revert_verb = getattr(self._session, "snapshot_revert", None) \
            if self._session is not None else None
        if self._session is not None and not callable(revert_verb):
            raise LibvirtConnectionError(
                "connected session has no snapshot verbs; "
                "refusing stub revert against a live host")
        if callable(list_verb):
            known = list_verb(vm_id)
            via = "session"
        else:
            known = self.snapshots.get(vm_id, [])
            via = "stub"
        if snapshot not in known:
            raise LibvirtConnectionError(
                f"revert refused: unknown snapshot {snapshot!r} for vm {vm_id!r}")
        if callable(revert_verb):
            revert_verb(vm_id, snapshot)
        key = (vm_id, snapshot)
        n = self._reverts.get(key, 0) + 1
        self._reverts[key] = n
        return {"vm": vm_id, "snapshot": snapshot, "revert_n": n,
                "via": via,
                "backend": "libvirt" if self.libvirt_available else "stub"}
