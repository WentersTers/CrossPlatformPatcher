#!/bin/sh
# 06-visual-pin: compositing inputs deterministic by construction (Session 4).
# The negative band held by 0.008 of wallpaper luck (dock ROI 0.242 vs 0.25
# ceiling on the stock setup). An unattended-upgrade swapping an icon theme
# between runs would silently move the band under the ceiling — pin wallpaper,
# GTK/icon theme, and stop upgrade churn on test goldens.
set -eu
WP=file:///usr/share/backgrounds/warty-final-ubuntu.png
test -f /usr/share/backgrounds/warty-final-ubuntu.png
sudo -u "${GOLDEN_USER:-sage}" dbus-run-session -- /usr/bin/gsettings set org.gnome.desktop.background picture-uri "$WP"
sudo -u "${GOLDEN_USER:-sage}" dbus-run-session -- /usr/bin/gsettings set org.gnome.desktop.background picture-uri-dark "$WP"
sudo -u "${GOLDEN_USER:-sage}" dbus-run-session -- /usr/bin/gsettings set org.gnome.desktop.interface gtk-theme 'Yaru'
sudo -u "${GOLDEN_USER:-sage}" dbus-run-session -- /usr/bin/gsettings set org.gnome.desktop.interface icon-theme 'Yaru'
if systemctl list-unit-files 2>/dev/null | grep -q unattended-upgrades; then
  sudo systemctl disable --now unattended-upgrades
fi
# Release-upgrader modal (Session-4 finding: "Ubuntu 24.04.4 LTS Upgrade
# Available" dialog appeared unprompted on first boot AND its modal grab ate
# every pointer input event while its backend churned — QMP acked, guest
# ignored. Pin release checks off entirely, not just package updates.)
if [ -f /etc/update-manager/release-upgrades ]; then
  sudo sed -i 's/^Prompt=.*/Prompt=never/' /etc/update-manager/release-upgrades
fi
# Updater UI itself (Session-4 finding: the release-upgrader MODAL ate every
# pointer input event while its backend churned, and the package updater window
# opens unprompted on first boot. Simulate-first showed exactly 4 removals
# with no cascade beyond the ubuntu-desktop-minimal metapackage marker, which
# is post-install harmless — nothing runs autoremove on goldens).
sudo apt-get purge -y update-notifier update-manager ubuntu-release-upgrader-gtk
sudo apt-get autoremove -y
