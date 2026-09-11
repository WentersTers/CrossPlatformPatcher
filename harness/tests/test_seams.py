"""Session-7 seam checks: hop slots into the runner, input stays explicit,
artifacts pull two legs (preflight/pool/qmp/shots over ssh-hop)."""
import hashlib

import pytest

from harness.live.qmp_transport import QmpTransport
from harness.live.shots import ScreenshotError, pull_screenshot
from harness.live.virsh_argv import prefix_for
from harness.vms.pool import LibvirtConnectionError, VmPool

HOP = ["ssh", "-o", "BatchMode=yes", "-o", "ConnectTimeout=10",
       "-o", "StrictHostKeyChecking=accept-new",
       "sage@h", "virsh", "-c", "qemu:///system"]


def test_prefix_for_local_and_hop():
    assert prefix_for("qemu:///system") == ["virsh", "-c", "qemu:///system"]
    assert prefix_for("qemu+ssh://sage@h/system", ssh_hop=True) == HOP
    p = prefix_for("qemu+ssh://sage@h:2222/system", ssh_hop=True)
    assert "-p" in p and "2222" in p
    with pytest.raises(LibvirtConnectionError, match="remote URI"):
        prefix_for("qemu:///system", ssh_hop=True)
    with pytest.raises(LibvirtConnectionError, match="bad URI"):
        prefix_for("hyperv://nope", ssh_hop=True)


def test_session_snapshot_verbs_and_typing():
    from harness.preflight import _VirshSession
    seen = []

    def runner(cmd):
        seen.append(cmd)
        if cmd[-1] == "--name":
            return 0, "base-v4-clean\n", ""
        if "snapshot-revert" in cmd:
            if "base-v4-clean" in cmd:
                return 0, "reverted\n", ""
            return 1, "", "error: no such snapshot"

    s = _VirshSession(HOP, runner)
    assert s.snapshot_list("d") == ["base-v4-clean"]
    assert seen[0] == HOP + ["snapshot-list", "d", "--name"]
    assert s.snapshot_revert("d", "base-v4-clean") == "reverted"
    with pytest.raises(LibvirtConnectionError, match="snapshot-revert failed"):
        s.snapshot_revert("d", "nope")


def test_snapshot_create_addresses_disk_explicitly():
    from harness.preflight import _VirshSession
    seen = []

    def runner(cmd):
        seen.append(cmd)
        return 0, "created\n", ""

    s = _VirshSession(HOP, runner)
    assert s.snapshot_create("d", "v5") == "created"
    assert seen[0] == HOP + ["snapshot-create-as", "d", "v5",
                             "--diskspec", "vda,snapshot=internal"]


class _VerbSession:
    """Fake connected session WITH virsh snapshot verbs."""

    def __init__(self):
        self.calls = []

    def list_domains(self):
        return ["d"]

    def snapshot_list(self, domain):
        self.calls.append(("list", domain))
        return ["base-v4-app"]

    def snapshot_revert(self, domain, snapshot):
        self.calls.append(("revert", domain, snapshot))
        return "ok"

    def snapshot_create(self, domain, snapshot):
        self.calls.append(("create", domain, snapshot))
        return "ok"


class _VerbTransport:
    def __init__(self, session):
        self.session = session

    def connect(self, uri):
        return self.session


def test_pool_revert_really_reverts_when_connected():
    sess = _VerbSession()
    pool = VmPool(transport=_VerbTransport(sess))
    pool.connect("qemu+ssh://s@h/system")
    r = pool.revert("d", "base-v4-app")
    assert r["revert_n"] == 1 and r["via"] == "session"
    assert ("revert", "d", "base-v4-app") in sess.calls
    # unknown snapshot refused BEFORE touching the host
    with pytest.raises(LibvirtConnectionError, match="unknown snapshot"):
        pool.revert("d", "nope")
    assert not [c for c in sess.calls if c[-1] == "nope"]


def test_pool_refuses_stub_revert_against_live_host():
    class Bare:
        def list_domains(self):
            return ["d"]

    pool = VmPool(transport=_VerbTransport(Bare()))
    pool.connect("qemu+ssh://s@h/system")
    with pytest.raises(LibvirtConnectionError, match="no snapshot verbs"):
        pool.revert("d", "base-v4-app")


def test_qmp_prefix_is_explicit():
    seen = {}

    def runner(cmd):
        seen["cmd"] = cmd
        return '{"return":{}}'

    t = QmpTransport(domain="d", runner=runner, argv_prefix=HOP)
    t.press(0, 0)
    assert seen["cmd"][:len(HOP)] == HOP
    assert seen["cmd"][len(HOP):3 + len(HOP)] == ["qemu-monitor-command", "d", "--cmd"]
    # default unchanged: local virsh
    t2 = QmpTransport(domain="d", uri="qemu:///system", runner=runner)
    t2.press(0, 0)
    assert seen["cmd"][:3] == ["virsh", "-c", "qemu:///system"]


def _ppm_bytes():
    return b"P6\n2 2\n255\n" + bytes([10, 20, 30] * 4)


def test_click_transport_builder_picks_prefix():
    from harness.live.click_driver import _build_transport
    t = _build_transport("d", "qemu+ssh://s@h/system", True)
    assert t.argv_prefix == ["ssh", "-o", "BatchMode=yes",
                             "-o", "ConnectTimeout=10",
                             "-o", "StrictHostKeyChecking=accept-new",
                             "s@h", "virsh", "-c", "qemu:///system"]
    t2 = _build_transport("d", "qemu:///system", False)
    assert t2.argv_prefix is None


def test_watchdog_hop_refuses_single_leg_default(tmp_path):
    from harness.live.watchdog_run import run_watchdog
    with pytest.raises(ValueError, match="two legs"):
        run_watchdog(1, 5.0, tmp_path, virsh_prefix=HOP)
    out = run_watchdog(2, 5.0, tmp_path, virsh_prefix=HOP,
                       ping_fn=lambda: "ok",
                       shot_fn=lambda seq, path: open(path, "wb").write(b"P6\n1 1\n255\n\x00\x00\x00"),
                       log_fn=lambda: {"present": False, "bytes": 0},
                       sleep_fn=lambda s: None)
    assert out["summary"]["qga"] == {"ok": 2, "unknown": 0}


def test_two_leg_pull_verifies_magic_and_md5():
    raw = _ppm_bytes()
    seen = []
    pulled = {}

    def run(cmd):
        seen.append(cmd)
        if "screenshot" in cmd:
            return 0, "", ""
        if "md5sum" in cmd:
            return 0, hashlib.md5(raw).hexdigest() + "  /tmp/x.ppm\n", ""
        raise AssertionError(cmd)

    def fetch(target, remote, local):
        pulled["args"] = (target, remote, local)
        open(local, "wb").write(raw)

    import tempfile
    with tempfile.TemporaryDirectory() as d:
        import os
        local = os.path.join(d, "shot.ppm")
        res = pull_screenshot(virsh_prefix=HOP, ssh_prefix=HOP[:7],
                              ssh_target="sage@h", domain="d",
                              remote_path="/tmp/x.ppm", local_path=local,
                              run=run, fetch=fetch)
    assert res["fmt"] == "ppm" and res["md5"] == hashlib.md5(raw).hexdigest()
    assert pulled["args"][0] == "sage@h" and pulled["args"][1] == "/tmp/x.ppm"
    assert seen[0] == HOP + ["screenshot", "d", "/tmp/x.ppm"]


def test_pull_fails_loud_on_mismatch_and_bad_magic():
    def run_ok(cmd):
        if "md5sum" in cmd:
            return 0, "0" * 32 + "  /tmp/x.ppm\n", ""
        return 0, "", ""

    def fetch_ppm(target, remote, local):
        open(local, "wb").write(_ppm_bytes())

    def fetch_junk(target, remote, local):
        open(local, "wb").write(b"not an image at all........")

    import tempfile
    with tempfile.TemporaryDirectory() as d:
        import os
        with pytest.raises(ScreenshotError, match="md5 mismatch"):
            pull_screenshot(virsh_prefix=HOP, ssh_prefix=HOP[:7],
                            ssh_target="s", domain="d", remote_path="/tmp/x.ppm",
                            local_path=os.path.join(d, "a.ppm"),
                            run=run_ok, fetch=fetch_ppm)
        with pytest.raises(ScreenshotError, match="not PPM/PNG"):
            pull_screenshot(virsh_prefix=HOP, ssh_prefix=HOP[:7],
                            ssh_target="s", domain="d", remote_path="/tmp/x.ppm",
                            local_path=os.path.join(d, "b.ppm"),
                            run=run_ok, fetch=fetch_junk)
        with pytest.raises(ScreenshotError, match="leg-1"):
            pull_screenshot(virsh_prefix=HOP, ssh_prefix=HOP[:7],
                            ssh_target="s", domain="d", remote_path="/tmp/x.ppm",
                            local_path=os.path.join(d, "c.ppm"),
                            run=lambda cmd: (1, "", "no domain"),
                            fetch=fetch_ppm)
