"""Domain XML assertions: tablet bus, QGA channel, video device (§8, §12.5).

Session-1 correction: 1920x1080 is NOT a domain-XML property — libvirt video
models carry no resolution element; it is a guest-OS setting verified post-boot
via xrandr (adjudication step 6). Demanding it here failed a correct XML.
Resolution lives in the guest; tablet + QGA + video live here.
"""
from __future__ import annotations

import xml.etree.ElementTree as ET


def validate_domain_xml(xml_str: str) -> list[str]:
    """Returns list of failures (empty = valid)."""
    failures: list[str] = []
    try:
        root = ET.fromstring(xml_str)
    except ET.ParseError as e:
        return [f"invalid XML: {e}"]
    text = xml_str.lower()
    if "tablet" not in text:
        failures.append("USB tablet absolute coords required in domain XML")
    if ("qemu-guest-agent" not in text and "virtio-serial" not in text
            and "qga" not in text and "guest_agent" not in text
            and "guest-agent" not in text):
        failures.append("QGA channel required in domain XML")
    if "<video" not in text:
        failures.append("video device required in domain XML")
    return failures
