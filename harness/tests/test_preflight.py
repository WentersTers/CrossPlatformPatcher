"""Session-0 acceptance paths: success, typed failures, bad-host probe, virsh CLI."""
import pytest

from harness.preflight import SshHopTransport, VirshTransport, check, main
from harness.vms.pool import LibvirtConnectionError


def _fake_transport(domains=("ubuntu-22.04",), fail=None):
    class Session:
        def list_domains(self):
            return list(domains)

    class Transport:
        def connect(self, uri):
            if fail is not None:
                raise fail
            return Session()
    return Transport()


def test_check_success_reports_domains():
    rep = check("qemu+ssh://op@linux-host/system",
                transport=_fake_transport(("ubuntu-22.04", "windows-11")))
    assert rep["uri_ok"] is True and rep["connected"] is True
    assert rep["domains"] == ["ubuntu-22.04", "windows-11"]
    assert rep["error"] == "" and rep["remote"] is True
    assert rep["bad_host"] is None
    # stub session has no capabilities probe: reported, not failed
    assert rep["kvm"] == {"checked": False, "kvm": False,
                          "note": "transport has no capabilities probe"}
    assert set(rep["latency"]) == {"connect_s", "list_s"}


def test_latency_recorded_with_fake_clock_and_samples():
    from harness.measurements import Samples
    t = [100.0]

    class TimedTransport:
        def connect(self, uri):
            t[0] += 0.4
            class S:
                def list_domains(self):
                    t[0] += 0.1
                    return ["vm1"]
            return S()
    s = Samples()
    rep = check("qemu:///system", transport=TimedTransport(),
                time_fn=lambda: t[0], samples=s)
    assert rep["latency"] == pytest.approx({"connect_s": 0.4, "list_s": 0.1})
    summ = s.summary()
    assert summ["preflight.connect_s"]["p50"] == pytest.approx(0.4)
    assert summ["preflight.list_s"]["unit"] == "s"


def test_host_key_hint_fires():
    rep = check("qemu+ssh://op@new-host/system",
                transport=_fake_transport(
                    fail=OSError("Host key verification failed.")))
    assert rep["connected"] is False
    assert "known_hosts" in rep["hint"] and "ssh-keyscan" in rep["hint"]
    rep2 = check("qemu+ssh://op@h/system",
                 transport=_fake_transport(fail=OSError("no route to host")))
    assert rep2["hint"] == ""


CAPS_KVM = """<capabilities>
  <host><cpu><arch>x86_64</arch></cpu></host>
  <guest><os_type>hvm</os_type><arch name='x86_64'>
    <domain type='kvm'/><domain type='qemu'/>
  </arch></guest></capabilities>"""
CAPS_NO_KVM = """<capabilities><host/><guest><os_type>hvm</os_type>
  <arch name='x86_64'><domain type='qemu'/></arch></guest></capabilities>"""


def test_kvm_probe_parses_capabilities():
    from harness.preflight import check_kvm, has_kvm
    assert has_kvm(CAPS_KVM) is True
    assert has_kvm(CAPS_NO_KVM) is False
    assert has_kvm("not xml") is False

    class CapsSession:
        def __init__(self, xml):
            self._xml = xml

        def list_domains(self):
            return []

        def capabilities(self):
            return self._xml

    class CapsTransport:
        def __init__(self, xml):
            self.xml = xml

        def connect(self, uri):
            return CapsSession(self.xml)

    rep = check("qemu+ssh://op@h/system", transport=CapsTransport(CAPS_KVM))
    assert rep["kvm"] == {"checked": True, "kvm": True}
    rep2 = check("qemu+ssh://op@h/system", transport=CapsTransport(CAPS_NO_KVM))
    assert rep2["kvm"] == {"checked": True, "kvm": False}
    assert check_kvm(object())["checked"] is False


def test_check_bad_uri_never_dials():
    rep = check("hyperv://nope/vms", transport=_fake_transport())
    assert rep["uri_ok"] is False and rep["connected"] is False
    assert "bad URI" in rep["error"]


def test_check_connection_failure_is_typed():
    rep = check("qemu+ssh://op@dead-host/system",
                transport=_fake_transport(fail=OSError("no route to host")))
    assert rep["connected"] is False and "no route" in rep["error"]


def test_bad_host_probe_must_fail_typed():
    rep = check("qemu+ssh://op@linux-host/system",
                transport=_fake_transport(),
                bad_uri="qemu+ssh://op@nonexistent/system")
    # fake transport succeeds everywhere -> probe flags unexpected success
    assert rep["bad_host"] == {"typed_failure": False,
                               "error": "UNEXPECTED SUCCESS against bad host"}

    class Refusing:
        def connect(self, uri):
            if "nonexistent" in uri:
                raise LibvirtConnectionError("ssh: Could not resolve hostname")
            class S:
                def list_domains(self):
                    return []
            return S()
    rep2 = check("qemu+ssh://op@linux-host/system", transport=Refusing(),
                 bad_uri="qemu+ssh://op@nonexistent/system")
    assert rep2["connected"] is True
    assert rep2["bad_host"]["typed_failure"] is True


def test_virsh_transport_parses_and_types():
    t = VirshTransport(runner=lambda cmd: (0, "ubuntu-22.04\n\nwindows-11\n", ""))
    s = t.connect("qemu+ssh://op@h/system")
    assert s.list_domains() == ["ubuntu-22.04", "windows-11"]
    t2 = VirshTransport(runner=lambda cmd: (1, "", "error: failed to connect"))
    with pytest.raises(LibvirtConnectionError, match="failed to connect"):
        t2.connect("qemu+ssh://op@dead/system")

    def missing(cmd):
        raise FileNotFoundError("virsh")
    with pytest.raises(LibvirtConnectionError, match="virsh executable not found"):
        VirshTransport(runner=missing).connect("qemu:///system")


def test_main_exit_codes():
    # hermetic: unparseable URI fails before any transport is touched
    assert main(["--uri", "bogus"]) == 1
    assert main(["--uri", "bogus", "--ssh-hop"]) == 1


HOP_PREFIX = ["ssh", "-o", "BatchMode=yes", "-o", "ConnectTimeout=10",
              "-o", "StrictHostKeyChecking=accept-new",
              "op@h", "virsh", "-c", "qemu:///system"]


def test_ssh_hop_builds_ssh_argv_and_lists():
    seen = []

    def runner(cmd):
        seen.append(cmd)
        if cmd[-2:] == ["--all", "--name"]:
            return 0, "ubuntu-22.04\n", ""
        if cmd[-1] == "capabilities":
            return 0, ("<capabilities><guest><os_type>hvm</os_type>"
                       "<arch name='x86_64'><domain type='kvm'/>"
                       "</arch></guest></capabilities>"), ""
        raise AssertionError(f"unexpected hop argv: {cmd}")

    s = SshHopTransport(runner=runner).connect("qemu+ssh://op@h/system")
    assert seen[0] == HOP_PREFIX + ["list", "--all", "--name"]
    assert "kvm" in s.capabilities()
    assert seen[1] == HOP_PREFIX + ["capabilities"]


def test_ssh_hop_port_user_and_local_refusal():
    seen = []
    t = SshHopTransport(runner=lambda cmd: (seen.append(cmd), (0, "a\n", ""))[1])
    t.connect("qemu+ssh://op@h:2222/system")
    assert seen[0][:2] == ["ssh", "-o"]
    assert "-p" in seen[0] and "2222" in seen[0]
    assert "op@h" in seen[0]

    seen.clear()
    t.connect("qemu+ssh://h/system")
    idx = next(i for i, x in enumerate(seen[0]) if "accept-new" in x)
    assert seen[0][idx + 1] == "h" and "-p" not in seen[0]

    with pytest.raises(LibvirtConnectionError, match="remote URI"):
        t.connect("qemu:///system")
    with pytest.raises(LibvirtConnectionError, match="bad URI"):
        t.connect("hyperv://nope/vms")


def test_ssh_hop_failures_are_typed():
    t = SshHopTransport(runner=lambda cmd: (1, "", "ssh: connect to host h port 22: no route"))
    with pytest.raises(LibvirtConnectionError, match="ssh-hop virsh list failed"):
        t.connect("qemu+ssh://op@h/system")

    def missing(cmd):
        raise FileNotFoundError("ssh")
    with pytest.raises(LibvirtConnectionError, match="ssh executable not found"):
        SshHopTransport(runner=missing).connect("qemu+ssh://op@h/system")


def test_ssh_hop_end_to_end_through_check():
    def runner(cmd):
        if cmd[-2:] == ["--all", "--name"]:
            return 0, "ubuntu-2204-stage\n", ""
        if cmd[-1] == "capabilities":
            return 0, CAPS_KVM, ""
        raise AssertionError(f"unexpected hop argv: {cmd}")

    rep = check("qemu+ssh://sage@h/system", transport=SshHopTransport(runner=runner))
    assert rep["connected"] is True
    assert rep["domains"] == ["ubuntu-2204-stage"]
    assert rep["kvm"] == {"checked": True, "kvm": True}

