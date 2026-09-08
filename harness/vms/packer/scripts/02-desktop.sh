#!/bin/sh
# 02-desktop: X11 GNOME + autologin (else every boot parks at GDM and runs stall).
set -eu
export DEBIAN_FRONTEND=noninteractive
# Swap to full `ubuntu-desktop` with one line if the minimal set misses anything.
sudo apt-get install -y ubuntu-desktop-minimal
sudo mkdir -p /etc/gdm3
sudo tee /etc/gdm3/custom.conf > /dev/null <<EOF
[daemon]
AutomaticLoginEnable=true
AutomaticLogin=${GOLDEN_USER:-sage}
# Stage 0 is X11: GDM under virtio-gpu is not deterministic enough to chance.
WaylandEnable=false
EOF
sudo systemctl set-default graphical.target

# First-login wizard must never appear (else every run stalls on it).
sudo -u "${GOLDEN_USER:-sage}" mkdir -p "/home/${GOLDEN_USER:-sage}/.config"
sudo -u "${GOLDEN_USER:-sage}" touch "/home/${GOLDEN_USER:-sage}/.config/gnome-initial-setup-done"

# Fixed 1920x1080 from first light via Xorg (Session-1 finding: a session
# autostart .desktop NEVER ran across two boots — Xorg reads this before any
# session exists, no EDID guessing, no login timing involved).
sudo mkdir -p /etc/X11/xorg.conf.d
sudo tee /etc/X11/xorg.conf.d/10-virtio.conf > /dev/null <<'EOF'
Section "Device"
    Identifier  "Card0"
    Driver      "modesetting"
EndSection
Section "Monitor"
    Identifier  "Virtual-1"
EndSection
Section "Screen"
    Identifier  "Screen0"
    Device      "Card0"
    Monitor     "Virtual-1"
    DefaultDepth 24
    SubSection "Display"
        Depth 24
        Modes "1920x1080"
    EndSubSection
EndSection
EOF

# Rename-proof networking: build-time NIC (ens5) != runtime NIC (ens3) broke
# DHCP on first boot (Session-1 finding). Match by name pattern, not MAC —
# the MAC is per-domain, the pattern survives it.
sudo tee /etc/netplan/99-stage0-net.yaml > /dev/null <<'EOF'
network:
  version: 2
  renderer: networkd
  ethernets:
    primary:
      match:
        name: "en*"
      dhcp4: true
EOF
sudo netplan apply || echo "WARN: netplan apply deferred to first boot"
