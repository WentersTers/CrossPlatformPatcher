"""Packer template pins (runbook Phase 3): the HCL/scripts must encode the
22.04 requirements. This pins CONTENT, not HCL semantics — `packer init`,
`packer validate`, and the first build adjudicate semantics in Session 1.
"""
import pathlib

PACKER = pathlib.Path(__file__).resolve().parent.parent / "vms" / "packer"


def _read(*parts: str) -> str:
    return (PACKER.joinpath(*parts)).read_text(encoding="utf-8")


def test_builder_tablet_qga_and_kvm():
    hcl = _read("ubuntu-22.04.pkr.hcl")
    assert '"-device", "usb-tablet"' in hcl  # click() absolute coords
    assert "org.qemu.guest_agent.0" in hcl and "virtio-serial" in hcl
    assert 'accelerator = "kvm"' in hcl
    assert "shutdown_command" in hcl


def _code_lines(text: str) -> str:
    """Strip full-line comments: prose may name t64 to forbid it; only
    dependency positions count."""
    return "\n".join(ln for ln in text.splitlines()
                     if not ln.lstrip().startswith("#"))


def test_2204_package_names_not_2404():
    base = _read("scripts", "01-base.sh")
    assert "libasound2 " in base or "libasound2\n" in base or "libasound2 \\" in base
    for f in ("ubuntu-22.04.pkr.hcl", "http/user-data.pkrtpl.hcl",
              "scripts/01-base.sh"):
        code = _code_lines(_read(*f.split("/")))
        assert "libasound2t64" not in code, f"t64 dependency in {f}"
    # 03-assert.sh references t64 only as a negative guard (pinned below)
    user_data = _read("http", "user-data.pkrtpl.hcl")
    assert "autoinstall" in user_data and "qemu-guest-agent" in user_data
    assert "NOPASSWD" in user_data  # provisioners run passwordless sudo


def test_gdm_autologin_and_forced_x11():
    desktop = _read("scripts", "02-desktop.sh")
    assert "AutomaticLoginEnable=true" in desktop
    assert "WaylandEnable=false" in desktop  # stage 0 is X11
    assert "ubuntu-desktop" in desktop


def test_assert_provisioner_mirrors_checklist():
    assert_ = _read("scripts", "03-assert.sh")
    for token in ("libasound2", "qemu-guest-agent", "AutomaticLoginEnable",
                  "WaylandEnable=false", "libasound2t64"):
        assert token in assert_
    assert "Session 1 post-boot" in assert_  # xrandr/QGA-ping deferred honestly
    assert "exit $fail" in assert_


def test_assertions_module_os_aware():
    from harness.vms.packer import assert_golden_image, packages_for
    assert "libasound2" in packages_for("ubuntu-22.04")
    assert "libasound2t64" not in packages_for("ubuntu-22.04")
    assert "libasound2t64" in packages_for("ubuntu-24.04")
    spec = {"qga": True, "ssh": True, "resolution": "1920x1080", "tablet": True,
            "packages": packages_for("ubuntu-22.04"),
            "run_sh_lf": True, "run_sh_executable": True}
    assert assert_golden_image(spec, "ubuntu-22.04") == []
    assert assert_golden_image({}, "ubuntu-22.04") != []


def test_secrets_hygiene():
    root = PACKER.parents[2]  # harness/vms/packer -> repo root
    gi = (root / ".gitignore").read_text(encoding="utf-8")
    assert "*.pkrvars.hcl" in gi  # real secrets never committed ...
    assert "harness/runs/" in gi  # ... nor rehearsal outputs
    example = (PACKER / "secrets.pkrvars.hcl.example").read_text(
        encoding="utf-8")
    assert "CHANGEME" in example and "password_hash" in example
    # the example itself must not match the ignore pattern (else it vanishes)
    assert not PACKER.joinpath("secrets.pkrvars.hcl.example").match(
        "*.pkrvars.hcl")


def test_session1_hardening_folded_in():
    """Every manual guest fix from the first adjudication must live in the
    template, or golden-v2 rebuilds the same broken image:
    - first-login wizard suppress (else runs stall on it)
    - autostart 1920x1080 (virtio boots 1024x768)
    - rename-proof netplan (build NIC ens5 != runtime NIC ens3 killed DHCP)
    - headless noblank via throwaway bus (a blanked display lies pet_absent)
    """
    desktop = _read("scripts", "02-desktop.sh")
    assert "gnome-initial-setup-done" in desktop
    # Xorg fixed mode (NOT session autostart: proven dead across two boots)
    assert "10-virtio.conf" in desktop and 'Modes "1920x1080"' in desktop
    assert "stage0-resolution.desktop" not in desktop
    assert 'name: "en*"' in desktop  # pattern, never a concrete NIC or MAC
    code = "\n".join(ln for ln in desktop.splitlines()
                     if not ln.lstrip().startswith("#"))
    assert "ens5" not in code and "52:54:00" not in code  # prose may explain
    noblank = _read("scripts", "05-noblank.sh")
    assert "dbus-run-session" in noblank  # headless-safe; plain gsettings
    # needs a session bus that provisioning does not have
    assert "idle-delay 0" in noblank and "lock-enabled false" in noblank
    hcl = _read("ubuntu-22.04.pkr.hcl")
    assert "05-noblank.sh" in hcl
    # assert runs after configure so it checks the final image incl. read-back
    block = hcl[hcl.index("scripts = ["):]
    assert block.index("05-noblank.sh") < block.index("03-assert.sh")
    # visual pinning rides the same configure phase, before cleanup+assert
    assert "06-visual-pin.sh" in block
    assert block.index("05-noblank.sh") < block.index("06-visual-pin.sh")
    assert block.index("06-visual-pin.sh") < block.index("04-cleanup.sh")
    assert_ = _read("scripts", "03-assert.sh")
    assert "gnome-initial-setup-done" in assert_
    assert "10-virtio.conf" in assert_
    assert "99-stage0-net.yaml" in assert_
    pin = _read("scripts", "06-visual-pin.sh")
    assert "warty-final-ubuntu" in pin and "Yaru" in pin
    assert "unattended-upgrades" in pin
    assert "dbus-run-session" in pin  # headless-safe gsettings like 05
    assert "Prompt=never" in pin  # release-upgrader modal ate all input live
    assert "purge -y update-notifier update-manager" in pin
    assert "warty-final-ubuntu.png" in _read("scripts", "03-assert.sh")
    assert "Prompt=never" in _read("scripts", "03-assert.sh")
    assert "update-notifier" in _read("scripts", "03-assert.sh")
