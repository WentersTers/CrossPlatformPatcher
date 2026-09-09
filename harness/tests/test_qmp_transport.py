"""QMP monitor transport over the ssh hop: quote-eating family guard.

Proven live (e2e): every pointer event sent unquoted over the hop arrived
with its JSON quotes eaten by the remote shell and died in libvirt's
parser — while ledgers acked. These tests pin: hop payloads survive a
remote-shell round trip byte-identical; local payloads stay unquoted;
transport failures raise (never an empty ack).
"""
import json
import shlex

import pytest

from harness.live.qmp_transport import QmpTransport, _default_runner, to_absolute
from harness.vms.pool import LibvirtConnectionError

HOP = ["ssh", "-o", "BatchMode=yes", "sage@host",
       "virsh", "-c", "qemu:///system"]


class Cap:
    def __init__(self, out='{"return":{}}'):
        self.cmds = []
        self.out = out

    def __call__(self, cmd):
        self.cmds.append(list(cmd))
        return self.out


def cmd_of(cap):
    assert len(cap.cmds) == 1
    cmd = cap.cmds[0]
    i = cmd.index("--cmd")
    return cmd, cmd[i + 1]


def test_hop_payload_survives_remote_shell():
    cap = Cap()
    t = QmpTransport(domain="d", uri="qemu+ssh://u@h/system",
                     argv_prefix=HOP, runner=cap)
    t.press(935, 810)
    cmd, payload = cmd_of(cap)
    # model the remote shell: word-split must yield exactly one word whose
    # JSON parses to the original event object (quotes are shell syntax,
    # not payload — compare post-split, not pre-split).
    words = shlex.split(payload)
    assert len(words) == 1
    obj = json.loads(words[0])
    evs = obj["arguments"]["events"]
    assert evs[0] == {"type": "abs", "data": {"axis": "x", "value": to_absolute(935, 810)[0]}}
    assert evs[2] == {"type": "btn", "data": {"down": True, "button": "left"}}


def test_local_payload_unquoted():
    cap = Cap()
    t = QmpTransport(domain="d", uri="qemu:///system", runner=cap)
    t.press(100, 100)
    cmd, payload = cmd_of(cap)
    assert cmd[:3] == ["virsh", "-c", "qemu:///system"]
    assert payload.startswith('{"execute"')
    json.loads(payload)


def test_release_quoted_over_hop():
    cap = Cap()
    t = QmpTransport(domain="d", uri="qemu+ssh://u@h/system",
                     argv_prefix=HOP, runner=cap)
    t.release(0, 0)
    _, payload = cmd_of(cap)
    words = shlex.split(payload)
    assert len(words) == 1
    assert json.loads(words[0])["arguments"]["events"] == [
        {"type": "btn", "data": {"down": False, "button": "left"}}]


def test_default_runner_raises_on_failure(monkeypatch):
    import harness.live.qmp_transport as qt

    class P:
        returncode = 1
        stdout = ""
        stderr = "error: bad command"

    monkeypatch.setattr(qt.subprocess, "run", lambda *a, **k: P())
    with pytest.raises(LibvirtConnectionError):
        _default_runner(["virsh"])
