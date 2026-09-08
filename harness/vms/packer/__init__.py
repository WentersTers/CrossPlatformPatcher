"""Packer golden-image package: HCL template + build-time assertion scripts.

Import surface unchanged: `from harness.vms.packer import assert_golden_image`.
"""
from harness.vms.packer.assertions import (OS_PACKAGES, REQUIRED_PACKAGES,
                                           assert_golden_image, packages_for)

__all__ = ["OS_PACKAGES", "REQUIRED_PACKAGES", "assert_golden_image",
           "packages_for"]
