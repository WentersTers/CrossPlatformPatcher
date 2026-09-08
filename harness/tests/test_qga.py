"""QGA-over-hop backend: quoting, pid/status mapping, env prepend, loud errors."""
import base64

import pytest

from harness.live.guest_run import GuestRunFactory
from harness.live.qga import QgaBackend, QgaError
from harness.vms.pool import LibvirtConnectionError

PREFIX = ["ssh", "h", "virsh", "-c", "qemu:///system"]


def _ok(payload_return):
    import json as j
    return lambda cmd: (0, j.dumps({"return": payload_return}), "")


def test_exec_single_quoted_payload_and_pid():
    seen = []
    be = QgaBackend("d", PREFIX,
                    run=lambda cmd: (seen.append(cmd), (0, '{"return":{"pid":4242}}', ""))[1])
    assert be.exec(["/bin/echo", "hi there"]) == 4242
    cmd = seen[0]
    assert cmd[:5] == PREFIX
    assert cmd[5:8] == ["qemu-agent-command", "d", "--cmd"]
    assert len(cmd) == 9  # payload is ONE argv element (quoting discipline)
    import json as j
    assert cmd[8].startswith("'") and cmd[8].endswith("'")
    payload = j.loads(cmd[8][1:-1])
    assert payload["arguments"]["path"] == "/bin/echo"
    assert payload["arguments"]["arg"] == ["hi there"]


def test_env_prepended_as_env_argv():
    seen = []
    be = QgaBackend("d", PREFIX, env={"DISPLAY": ":0"},
                    run=lambda cmd: (seen.append(cmd), (0, '{"return":{"pid":1}}', ""))[1])
    be.exec(["bash", "run.sh"])
    import json as j
    payload = j.loads(seen[0][8][1:-1])
    assert payload["arguments"]["path"] == "env"
    assert payload["arguments"]["arg"][:2] == ["DISPLAY=:0", "bash"]


def test_status_decodes_base64():
    out = base64.b64encode("hello\n".encode()).decode()
    be = QgaBackend("d", PREFIX, run=_ok({"exited": True, "exitcode": 3,
                                          "out-data": out}))
    assert be.status(9) == {"exited": True, "exitcode": 3,
                            "out": "hello\n", "err": ""}
    be2 = QgaBackend("d", PREFIX, run=_ok({"exited": False}))
    assert be2.status(9) == {"exited": False, "exitcode": None,
                             "out": "", "err": ""}


def test_failures_typed_and_kill_never_raises():
    be = QgaBackend("d", PREFIX, run=lambda cmd: (1, "", "no agent"))
    with pytest.raises(LibvirtConnectionError, match="qemu-agent-command failed"):
        be.exec(["/bin/true"])
    with pytest.raises(QgaError, match="non-empty argv"):
        be.exec([])
    be2 = QgaBackend("d", PREFIX, run=lambda cmd: (0, "not json", ""))
    with pytest.raises(QgaError, match="unparseable"):
        be2.status(1)
    be3 = QgaBackend("d", PREFIX, run=lambda cmd: (_ for _ in ()).throw(
        LibvirtConnectionError("down")))
    be3.kill(99)  # backstop never raises


def test_factory_shape_behind_launch():
    be = QgaBackend("d", PREFIX, run=_ok({"exited": True, "exitcode": 0}))
    fac = GuestRunFactory(lambda argv: 7, lambda pid: be.status(pid))
    proc = fac(["echo"])
    assert proc.poll() == 0 and proc.communicate() == ("", "")
