#!/bin/sh
# 04-cleanup: generalize the image so first boot is clean (snapshot point).
set -eu
sudo cloud-init clean --logs || true
sudo truncate -s 0 /etc/machine-id
sudo rm -f /etc/ssh/ssh_host_*
sudo apt-get clean
sudo rm -rf /tmp/* /var/tmp/* /var/log/*.log || true
history -c || true
