#!/bin/sh
# 01-base: stage matrix libraries (22.04 names) + services. Expects GOLDEN_USER.
set -eu
export DEBIAN_FRONTEND=noninteractive
sudo apt-get update
# NOTE 22.04: libasound2. The t64 suffix is 24.04+ (H-R §4) — do not "fix".
sudo apt-get install -y \
  qemu-guest-agent openssh-server \
  libgomp1 libasound2 libpulse0 libsndfile1 \
  tesseract-ocr ffmpeg
sudo systemctl enable --now qemu-guest-agent
sudo systemctl enable --now ssh
