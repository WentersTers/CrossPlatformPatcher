"""VMs/packer/gui/artifacts: labels, assertions, read-only mosaic, verdict flags."""
from harness.artifacts.layout import build_run_dir, retention_days, write_verdict
from harness.gui.mosaic import MosaicTile
from harness.vms.domain_xml import validate_domain_xml
from harness.vms.packer import assert_golden_image
from harness.vms.pool import VmPool


def test_domain_xml_requires_tablet_qga_video():
    # Session-1 correction: resolution is guest-side (xrandr), not XML.
    # A correct XML carries tablet + QGA + video and no resolution element.
    good = ("<domain><devices><input type='tablet' bus='usb'/>"
            "<channel type='unix'><target name='org.qemu.guest_agent.0'/></channel>"
            "<video><model type='virtio' heads='1' primary='yes'/></video>"
            "</devices></domain>")
    assert validate_domain_xml(good) == []
    bad = "<domain><devices/></domain>"
    failures = validate_domain_xml(bad)
    assert len(failures) == 3
    assert validate_domain_xml("not xml") != []


def test_stage0_domain_xml_validates():
    """The real Session-1 domain file passes the real validator."""
    import pathlib
    base = pathlib.Path(__file__).resolve().parent.parent / "vms" / "domain_xml"
    for name in ("ubuntu-2204-stage0.xml", "ubuntu-2204-stage.xml",
                 "ubuntu-2204-v4check.xml"):
        xml = (base / name).read_text(encoding="utf-8")
        assert validate_domain_xml(xml) == [], name


def test_packer_assertions():
    spec = {"qga": True, "ssh": True, "resolution": "1920x1080", "tablet": True,
            "packages": ["libgomp1", "libasound2t64", "libpulse0", "libsndfile1",
                         "tesseract-ocr", "ffmpeg", "openssh-server"],
            "run_sh_lf": True, "run_sh_executable": True}
    assert assert_golden_image(spec) == []
    bad = dict(spec, qga=False, resolution="1280x720", run_sh_lf=False)
    assert len(assert_golden_image(bad)) >= 3


def test_pool_stub_without_libvirt():
    pool = VmPool()
    assert pool.lifecycle("vm1", "boot")["action"] == "boot"
    assert pool.snapshot("vm1", "pre-test")["snapshot"] == "pre-test"


def test_mosaic_read_only_default_and_override():
    tile = MosaicTile("vm1", worker_badge="w1", task="wake", progress=0.5,
                      status_color="green", pet_state="listening")
    assert tile.to_dict()["read_only"] is True
    tile.manual_override = True  # locks agent out of input while active
    assert tile.to_dict()["read_only"] is False


def test_verdict_flags_tier_b_and_runtime_label(tmp_path):
    d = build_run_dir(tmp_path, "ubuntu", "22.04", "test_wake_word_seq",
                      ts="20260101T000000Z")
    assert "ubuntu-22.04" in str(d) and "test_wake_word_seq" in str(d)
    v = write_verdict(d, "pass", {"1": True, "2": True}, state_method="vlm",
                      runtime="native-linux-x64")
    assert v["state_verification_non_deterministic"] is True
    v2 = write_verdict(d, "pass", {"1": True}, state_method="template")
    assert v2["state_verification_non_deterministic"] is False
    assert retention_days(True) == 30 and retention_days(False) is None
