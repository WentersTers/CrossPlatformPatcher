"""Session-0 pre-flight acceptance (runbook): live testing starts when
`virsh -c qemu+ssh://user@host/system list --all` succeeds from this console.

Two transports:
- VirshTransport: local `virsh -c <uri>` (Linux host, or Windows with a
  working native qemu+ssh stack).
- SshHopTransport (`--ssh-hop`): `ssh … virsh -c qemu:///system …` — for
  consoles whose native virsh cannot do qemu+ssh (native Windows
  PowerShell). Same URI in, same session interface out.

Invokable: python harness/preflight.py --uri qemu+ssh://user@host/system
            [--ssh-hop] [--bad-uri qemu+ssh://user@nonexistent/system]
Exit 0 only if: URI valid, connect OK, domains listed, AND the bad-host probe
(if given) fails with the TYPED error — the failure path exercised in reality,
not just the success path.
"""
from __future__ import annotations

import argparse
import subprocess
import sys
from pathlib import Path
from typing import Any, Callable

HARNESS_ROOT = Path(__file__).resolve().parent

sys.path.insert(0, str(HARNESS_ROOT.parent))

from harness.vms.pool import LibvirtConnectionError, VmPool, parse_libvirt_uri  # noqa: E402

Runner = Callable[[list[str]], tuple[int, str, str]]


class VirshTransport:
    """Real transport over the virsh CLI (libvirt clients, incl. Windows builds).

    Session 0 runs this for real; tests inject a fake runner.
    """

    def __init__(self, runner: Runner | None = None):
        self._run = runner or self._subprocess

    @staticmethod
    def _subprocess(cmd: list[str]) -> tuple[int, str, str]:
        try:
            p = subprocess.run(cmd, capture_output=True, text=True, timeout=30)
        except FileNotFoundError:
            raise LibvirtConnectionError(
                "virsh executable not found; install libvirt clients on this "
                "console or acquire the Linux host (runbook Session 0)")
        except subprocess.TimeoutExpired as e:
            raise LibvirtConnectionError(f"virsh timed out: {e}") from e
        return p.returncode, p.stdout, p.stderr

    def _call(self, cmd: list[str]) -> tuple[int, str, str]:
        try:
            return self._run(cmd)
        except OSError as e:
            raise LibvirtConnectionError(
                f"virsh executable not found or not runnable: {e}") from e

    def connect(self, uri: str):
        rc, _out, err = self._call(["virsh", "-c", uri, "list", "--all", "--name"])
        if rc != 0:
            raise LibvirtConnectionError(
                f"virsh list failed for {uri!r}: {err.strip()[:300]}")
        return _VirshSession(["virsh", "-c", uri], self._call)


class SshHopTransport:
    """virsh-over-ssh hop for consoles without a working native qemu+ssh
    virsh (native Windows PowerShell). The input URI stays
    qemu+ssh://[user@]host[:port]/system; the hop ssh's to the host and runs
    `virsh -c qemu:///system` there. Same session interface out as
    VirshTransport, so check()/VmPool work unchanged.

    BatchMode=yes: auth failures are fast typed errors, never password
    prompts (a prompt is a silent hang, §8). Session 0 runs this for real;
    tests inject a fake runner.
    """

    REMOTE_URI = "qemu:///system"

    def __init__(self, runner: Runner | None = None,
                 ssh_opts: list[str] | None = None,
                 connect_timeout_s: int = 10):
        self._run = runner or VirshTransport._subprocess
        self._extra_opts = list(ssh_opts or [])
        self._timeout = connect_timeout_s

    def _base(self, uri: str) -> list[str]:
        from harness.live.virsh_argv import prefix_for
        return prefix_for(uri, ssh_hop=True, ssh_opts=self._extra_opts,
                          connect_timeout_s=self._timeout)

    def _call(self, cmd: list[str]) -> tuple[int, str, str]:
        try:
            return self._run(cmd)
        except OSError as e:
            raise LibvirtConnectionError(
                f"ssh executable not found or not runnable: {e}") from e

    def connect(self, uri: str):
        prefix = self._base(uri)
        rc, _out, err = self._call(prefix + ["list", "--all", "--name"])
        if rc != 0:
            raise LibvirtConnectionError(
                f"ssh-hop virsh list failed for {uri!r}: {err.strip()[:300]}")
        return _VirshSession(prefix, self._call)


class _VirshSession:
    def __init__(self, argv_prefix: list[str], runner):
        self._prefix = list(argv_prefix)
        self._runner = runner

    def _argv(self, *args: str) -> list[str]:
        return [*self._prefix, *args]

    def _label(self) -> str:
        return " ".join(self._prefix[:4]) + ("…" if len(self._prefix) > 4 else "")

    def list_domains(self) -> list[str]:
        rc, out, err = self._runner(self._argv("list", "--all", "--name"))
        if rc != 0:
            raise LibvirtConnectionError(
                f"virsh list failed ({self._label()}): {err.strip()[:300]}")
        return [ln.strip() for ln in out.splitlines() if ln.strip()]

    def capabilities(self) -> str:
        """Raw capabilities XML — the /dev/kvm question answered by the host."""
        rc, out, err = self._runner(self._argv("capabilities"))
        if rc != 0:
            raise LibvirtConnectionError(
                f"virsh capabilities failed ({self._label()}): "
                f"{err.strip()[:300]}")
        return out

    def snapshot_list(self, domain: str) -> list[str]:
        """Snapshot names for a domain (`virsh snapshot-list --name`)."""
        rc, out, err = self._runner(
            self._argv("snapshot-list", domain, "--name"))
        if rc != 0:
            raise LibvirtConnectionError(
                f"virsh snapshot-list failed for {domain!r} "
                f"({self._label()}): {err.strip()[:300]}")
        return [ln.strip() for ln in out.splitlines() if ln.strip()]

    def snapshot_revert(self, domain: str, snapshot: str) -> str:
        """Revert a domain to a snapshot. Loud on failure; no --force —
        if libvirt ever demands it, that demand is a recorded finding."""
        rc, out, err = self._runner(
            self._argv("snapshot-revert", domain, snapshot))
        if rc != 0:
            raise LibvirtConnectionError(
                f"virsh snapshot-revert failed for {domain!r}@{snapshot!r} "
                f"({self._label()}): {err.strip()[:300]}")
        return out.strip()

    def snapshot_create(self, domain: str, snapshot: str) -> str:
        rc, out, err = self._runner(
            self._argv("snapshot-create-as", domain, snapshot))
        if rc != 0:
            raise LibvirtConnectionError(
                f"virsh snapshot-create failed for {domain!r}@{snapshot!r} "
                f"({self._label()}): {err.strip()[:300]}")
        return out.strip()


def has_kvm(caps_xml: str) -> bool:
    """True when the host exposes a kvm domain type (i.e. /dev/kvm present)."""
    import xml.etree.ElementTree as ET
    try:
        root = ET.fromstring(caps_xml)
    except ET.ParseError:
        return False
    return any(d.get("type") == "kvm" for d in root.iter("domain"))


def check_kvm(session) -> dict:
    """Probe KVM support. Sessions without a capabilities method (stub
    transports) report checked=False rather than failing."""
    caps = getattr(session, "capabilities", None)
    if caps is None:
        return {"checked": False, "kvm": False,
                "note": "transport has no capabilities probe"}
    try:
        xml = caps()
    except LibvirtConnectionError as e:
        return {"checked": True, "kvm": False, "error": str(e)}
    return {"checked": True, "kvm": has_kvm(xml)}


HOST_KEY_RE = None  # compiled lazily to keep import light


def host_key_hint(error: str) -> str:
    """Session-0 friction #1: first-contact host-key prompts surface as opaque
    connect failures. Recognize them and say the fix, not the symptom."""
    import re
    global HOST_KEY_RE
    if HOST_KEY_RE is None:
        HOST_KEY_RE = re.compile(r"host key|known_hosts|Host key verification",
                                 re.IGNORECASE)
    if HOST_KEY_RE.search(error or ""):
        return ("host-key verification failed: pre-seed known_hosts "
                "(ssh-keyscan <host> >> ~/.ssh/known_hosts) or use "
                "StrictHostKeyChecking=accept-new for the first connect, "
                "then re-run preflight")
    return ""

def check(uri: str, transport=None, bad_uri: str | None = None,
          time_fn=None, samples=None) -> dict:
    """Run Session-0 acceptance. Never raises: all outcomes in the report.

    Connect/list latency is recorded (report["latency"], plus Samples entries
    preflight.connect_s / preflight.list_s when `samples` is given) —
    measurement zero for the stage-2 cadence question: every virsh call is an
    SSH round trip, and WAN latency bounds the watchdog budget.
    """
    import time as _time
    _clock = time_fn or _time.monotonic
    report: dict = {"uri": uri, "uri_ok": False, "connected": False,
                    "domains": [], "error": "", "hint": "", "kvm": None,
                    "latency": {}, "bad_host": None}
    try:
        info = parse_libvirt_uri(uri)
    except ValueError as e:
        report["error"] = f"bad URI: {e}"
        return report
    report["uri_ok"] = True
    report["remote"] = info["remote"]
    pool = VmPool(transport=transport)
    try:
        t0 = _clock()
        pool.connect(uri)
        t1 = _clock()
        report["domains"] = pool.list_domains()
        t2 = _clock()
        report["connected"] = True
        report["latency"] = {"connect_s": t1 - t0, "list_s": t2 - t1}
        if samples is not None:
            samples.add("preflight.connect_s", t1 - t0, unit="s")
            samples.add("preflight.list_s", t2 - t1, unit="s")
        report["kvm"] = check_kvm(pool.active_session)
    except LibvirtConnectionError as e:
        report["error"] = str(e)
        report["hint"] = host_key_hint(str(e))
        return report
    if bad_uri is not None:
        bad = VmPool(transport=transport)
        try:
            bad.connect(bad_uri)
            report["bad_host"] = {"typed_failure": False,
                                  "error": "UNEXPECTED SUCCESS against bad host"}
        except LibvirtConnectionError as e:
            report["bad_host"] = {"typed_failure": True, "error": str(e)}
    return report


def main(argv: list[str] | None = None) -> int:
    ap = argparse.ArgumentParser(description="Session-0 pre-flight (real transport)")
    ap.add_argument("--uri", required=True,
                    help="e.g. qemu+ssh://user@host/system")
    ap.add_argument("--bad-uri", default=None,
                    help="deliberate bad host to see the typed failure fire")
    ap.add_argument("--ssh-hop", action="store_true",
                    help="route virsh through `ssh … virsh -c qemu:///system` "
                         "(native Windows consoles without working qemu+ssh)")
    ap.add_argument("--ssh-opt", action="append", default=[],
                    help="extra raw ssh option for --ssh-hop "
                         "(repeatable, e.g. --ssh-opt=-i --ssh-opt=~/.ssh/id_x)")
    ap.add_argument("--metrics-out", default=None,
                    help="write latency Samples JSON here (measurement zero)")
    args = ap.parse_args(argv)
    from harness.measurements import Samples
    samples = Samples()
    if args.ssh_hop:
        transport: Any = SshHopTransport(ssh_opts=args.ssh_opt)
    else:
        transport = VirshTransport()
    report = check(args.uri, transport=transport, bad_uri=args.bad_uri,
                   samples=samples)
    if args.metrics_out:
        samples.write_json(args.metrics_out)
        print(f"metrics: {args.metrics_out}")
    print(f"uri: {report['uri']} (valid={report['uri_ok']})")
    print(f"connected: {report['connected']}")
    if report["connected"]:
        print(f"domains: {report['domains']}")
        lat = report["latency"]
        print(f"latency: connect={lat.get('connect_s', 0):.3f}s "
              f"list={lat.get('list_s', 0):.3f}s")
        kvm = report["kvm"] or {}
        print(f"kvm: checked={kvm.get('checked')} kvm={kvm.get('kvm')}"
              f"{' ' + str(kvm.get('error', ''))[:120] if kvm.get('error') else ''}"
              f"{' ' + str(kvm.get('note', '')) if kvm.get('note') else ''}")
    else:
        print(f"error: {report['error']}")
        if report["hint"]:
            print(f"hint: {report['hint']}")
    if report["bad_host"] is not None:
        print(f"bad-host typed failure: {report['bad_host']['typed_failure']} "
              f"({report['bad_host']['error'][:160]})")
        ok = report["connected"] and report["bad_host"]["typed_failure"]
    else:
        ok = report["connected"]
    if isinstance(report.get("kvm"), dict) and report["kvm"].get("checked"):
        ok = ok and bool(report["kvm"].get("kvm"))
    return 0 if ok else 1


if __name__ == "__main__":
    raise SystemExit(main())
