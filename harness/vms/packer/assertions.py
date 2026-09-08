"""Golden-image build-time assertions (§12.5).

Package names are per-OS: the t64 suffix is the 24.04+ transition —
22.04 takes plain libasound2 (runbook Phase 3). Callers pass os_name;
default preserves the legacy generic list.
"""
from __future__ import annotations


REQUIRED_PACKAGES = ["libgomp1", "libasound2t64", "libpulse0", "libsndfile1",
                     "tesseract-ocr", "ffmpeg", "openssh-server"]

OS_PACKAGES: dict[str, list[str]] = {
    "ubuntu-22.04": ["libgomp1", "libasound2", "libpulse0", "libsndfile1",
                     "tesseract-ocr", "ffmpeg", "openssh-server",
                     "qemu-guest-agent"],
    "ubuntu-24.04": ["libgomp1", "libasound2t64", "libpulse0", "libsndfile1",
                     "tesseract-ocr", "ffmpeg", "openssh-server",
                     "qemu-guest-agent"],
}


def packages_for(os_name: str | None) -> list[str]:
    if os_name in OS_PACKAGES:
        return OS_PACKAGES[os_name]
    return REQUIRED_PACKAGES


def assert_golden_image(spec: dict, os_name: str | None = None) -> list[str]:
    """spec keys: qga(bool), ssh(bool), resolution(str), tablet(bool),
    packages(list), run_sh_lf(bool), run_sh_executable(bool)."""
    failures: list[str] = []
    if not spec.get("qga"):
        failures.append("QGA installed and responding required (assert at Packer build)")
    if not spec.get("ssh"):
        failures.append("SSH up required")
    if spec.get("resolution") != "1920x1080":
        failures.append(f"resolution {spec.get('resolution')!r} != 1920x1080")
    if not spec.get("tablet"):
        failures.append("USB tablet present required")
    required = packages_for(os_name)
    missing = [p for p in required if p not in (spec.get("packages") or [])]
    if missing:
        failures.append(f"missing packages: {missing}")
    if not spec.get("run_sh_lf"):
        failures.append("run.sh must be LF line endings")
    if not spec.get("run_sh_executable"):
        failures.append("run.sh must be chmod +x")
    return failures
