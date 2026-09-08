#!/bin/sh
# 03-assert: build-time assertions — FAILS THE BUILD on what is assertable here.
# Mirrors harness.vms.packer.assert_golden_image (ubuntu-22.04 list).
# Resolution (xrandr 1920x1080) and guest-ping QGA are Session-1 post-boot
# checks: no X session is guaranteed during provisioning, so they are NOT
# asserted here — asserting them here would fail a good build.
set -eu
fail=0
check() { # check <label> <command...>
  label="$1"; shift
  if "$@" > /dev/null 2>&1; then echo "ok: $label"; else echo "FAIL: $label"; fail=1; fi
}
for pkg in qemu-guest-agent openssh-server libgomp1 libasound2 libpulse0 \
           libsndfile1 tesseract-ocr ffmpeg; do
  check "package $pkg" dpkg -s "$pkg"
done
# NOTE: ubuntu-desktop-minimal is deliberately NOT asserted — purging the
# updater UI removes the metapackage marker itself (v4 build proved it).
# Every real package above is asserted individually instead.
check "qga agent present" test -x /usr/sbin/qemu-ga
check "sshd enabled" systemctl is-enabled ssh
check "qga enabled" systemctl is-enabled qemu-guest-agent
check "gdm autologin" grep -q '^AutomaticLoginEnable=true' /etc/gdm3/custom.conf
check "gdm autologin user" grep -q "^AutomaticLogin=${GOLDEN_USER:-sage}" /etc/gdm3/custom.conf
check "x11 forced" grep -q '^WaylandEnable=false' /etc/gdm3/custom.conf
check "wizard suppressed" test -f "/home/${GOLDEN_USER:-sage}/.config/gnome-initial-setup-done"
check "xorg fixed mode" grep -q 'Modes "1920x1080"' /etc/X11/xorg.conf.d/10-virtio.conf
check "no dead autostart" test ! -f "/home/${GOLDEN_USER:-sage}/.config/autostart/stage0-resolution.desktop"
check "rename-proof netplan" grep -q 'name: "en\*"' /etc/netplan/99-stage0-net.yaml
check "pinned wallpaper file" test -f /usr/share/backgrounds/warty-final-ubuntu.png
check "release upgrades off" grep -q '^Prompt=never' /etc/update-manager/release-upgrades
check "no updater UI binaries" sh -c "! test -e /usr/bin/update-notifier && ! test -e /usr/bin/update-manager"
check "noblank read-back" sh -c "sudo -u '${GOLDEN_USER:-sage}' dbus-run-session -- /usr/bin/gsettings get org.gnome.desktop.session idle-delay | grep -q 0"
if dpkg -l | grep -q libasound2t64; then echo "FAIL: 24.04 t64 lib on 22.04"; fail=1; fi
echo "SKIP (Session 1 post-boot): xrandr 1920x1080, QGA guest-ping, tablet in domain XML"
exit $fail
