#!/usr/bin/env bash
# overlay_tree.sh SRC DST — stage a fresh base tree over an installed tree.
#
# Never deletes: overlay adds new files and overwrites changed ones with
# source bytes; removals (if ever needed) are explicit operations, not a
# side effect. Staged additions already living in DST (wine bottle, models,
# generated launchers/outputs, logs, patcher scripts) are untouched.
#
# Operational context for anyone running this tree (documented here so the
# staging procedure carries its runtime contract, not just files):
#   DISPLAY=:0
#   XDG_RUNTIME_DIR=/run/user/1000
#   PULSE_SOURCE=TestMic.monitor
#   Audio topology: TestMic + ResponseBus null sinks; default sink carries
#   responses, TestMic.monitor carries injections. See run.sh and the
#   bring-up sequence, not this script, for launch details.
#
# Usage: overlay_tree.sh /tmp/fresh-base /home/sage/paicom/patched
set -euo pipefail
SRC="${1:?usage: overlay_tree.sh SRC DST}"
DST="${2:?usage: overlay_tree.sh SRC DST}"
if [ ! -d "$SRC" ]; then echo "SRC missing: $SRC" >&2; exit 2; fi
mkdir -p "$DST"
cp -rf "$SRC/." "$DST/"
echo "overlay done: $SRC -> $DST"
