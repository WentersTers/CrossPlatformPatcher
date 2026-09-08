#!/bin/sh
# 05-noblank: a blanked display breaks all screenshots (pet_absent on a black
# framebuffer is a lie). Headless-safe via a throwaway bus: writes land in the
# build user's dconf db and persist. FATAL on purpose — if dbus-run-session is
# missing the build must break loudly, not ship a blanking golden.
# Behavioral proof is Session 3's 5-minute idle fb-hash test.
set -eu
test -x /usr/bin/dbus-run-session || { echo "FAIL: dbus-run-session missing"; exit 1; }
sudo -u "${GOLDEN_USER:-sage}" dbus-run-session -- /usr/bin/gsettings set org.gnome.desktop.session idle-delay 0
sudo -u "${GOLDEN_USER:-sage}" dbus-run-session -- /usr/bin/gsettings set org.gnome.desktop.screensaver lock-enabled false
sudo -u "${GOLDEN_USER:-sage}" dbus-run-session -- /usr/bin/gsettings set org.gnome.desktop.screensaver idle-activation-enabled false
sudo -u "${GOLDEN_USER:-sage}" dbus-run-session -- /usr/bin/gsettings set org.gnome.settings-daemon.plugins.power sleep-inactive-ac-type nothing
sudo -u "${GOLDEN_USER:-sage}" dbus-run-session -- /usr/bin/gsettings set org.gnome.settings-daemon.plugins.power sleep-inactive-ac-timeout 0
sudo -u "${GOLDEN_USER:-sage}" dbus-run-session -- /usr/bin/gsettings set org.gnome.settings-daemon.plugins.power sleep-inactive-battery-type nothing
sudo -u "${GOLDEN_USER:-sage}" dbus-run-session -- /usr/bin/gsettings get org.gnome.desktop.session idle-delay | grep -q 0
