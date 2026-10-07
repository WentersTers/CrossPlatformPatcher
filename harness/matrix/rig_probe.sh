#!/usr/bin/env bash
# ---------------------------------------------------------------------------
# D1 — rig_probe.sh : host resource inventory + concurrency decision.
#
# Runs on the rig host (the laptop). Self-measures everything; no hardcoded
# guesses. Output: JSON header that every leg report inherits.
#
#   bash rig_probe.sh                 # writes $MATRIX_ROOT/rig_probe.json
#   bash rig_probe.sh --print         # also prints JSON to stdout
#
# Resource policy (REV-013 §3):
#   max_concurrent = clamp(floor((free_RAM_GB - 4_host_reserve) / 4), 1, 3)
#   thermal throttle  -> max_concurrent forced to 1 (reason logged per leg)
#   stop condition    -> projected free RAM < 4 GB or free disk < 20 GB
#                        reports DEFERRED-resources instead of half-running
# ---------------------------------------------------------------------------
SCRIPT_DIR="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
# shellcheck source=common.sh
source "$SCRIPT_DIR/common.sh"

PRINT=0
[ "${1:-}" = "--print" ] && PRINT=1

# ---------------------------------------------------------------- measure
NPROC="$(nproc)"
MEM_TOTAL_KB="$(awk '/^MemTotal:/{print $2}' /proc/meminfo)"
MEM_AVAIL_KB="$(awk '/^MemAvailable:/{print $2}' /proc/meminfo)"
MEM_TOTAL_GB="$(python3 -c "print(round($MEM_TOTAL_KB/1048576.0, 2))")"
MEM_AVAIL_GB="$(python3 -c "print(round($MEM_AVAIL_KB/1048576.0, 2))")"

DISK_PATH="$POOL"
[ -d "$DISK_PATH" ] || DISK_PATH="$MATRIX_ROOT"
DISK_FREE_GB="$(df -BG --output=avail "$DISK_PATH" 2>/dev/null | tail -1 | tr -dc '0-9')"
DISK_FREE_GB="${DISK_FREE_GB:-0}"

KVM_PRESENT=false
[ -e /dev/kvm ] && KVM_PRESENT=true

LIBVIRT_OK=false
if sudo virsh -c qemu:///system list >/dev/null 2>&1; then LIBVIRT_OK=true; fi

# thermal: max cooling-zone temperature in millidegrees C
THERMAL_MILLIC=0
for tz in /sys/class/thermal/thermal_zone*/temp; do
  [ -r "$tz" ] || continue
  t="$(cat "$tz" 2>/dev/null || echo 0)"
  [ "$t" -gt "$THERMAL_MILLIC" ] 2>/dev/null && THERMAL_MILLIC="$t"
done
THERMAL_STATE="ok"
THROTTLE=false
if [ "$THERMAL_MILLIC" -ge "$THROTTLE_MILLIC" ] 2>/dev/null; then
  THERMAL_STATE="throttle"
  THROTTLE=true
fi
SENSORS_LINE=""
command -v sensors >/dev/null 2>&1 && SENSORS_LINE="$(sensors 2>/dev/null | grep -m1 -E 'Package id|Tctl' | tr -s ' ' | sed 's/^ *//')"

DOTNET_SDKS="$(dotnet --list-sdks 2>/dev/null | tr '\n' ';' | sed 's/;$//')"
QEMU_IMG="$(command -v qemu-img || true)"
MKFS_EXT2="$(command -v mkfs.ext2 || true)"

# ---------------------------------------------------------------- policy math
read -r MAX_CONCURRENT STOP_CHECK STOP_REASON < <(python3 - "$MEM_AVAIL_GB" "$DISK_FREE_GB" "$THROTTLE" <<'PY'
import math, sys
mem_gb = float(sys.argv[1]); disk_gb = float(sys.argv[2]); throttle = sys.argv[3] == "true"
mc = max(1, min(3, math.floor((mem_gb - 4) / 4)))
reasons = []
if mem_gb < 4: reasons.append(f"free RAM {mem_gb}GB < 4GB")
if disk_gb < 20: reasons.append(f"free disk {disk_gb}GB < 20GB")
if reasons:
    print(1, "DEFERRED-resources", ";".join(reasons))
else:
    print(mc, "OK", "-" if not throttle else "thermal-throttle-cap-1")
PY
)
if [ "$THROTTLE" = true ] && [ "$STOP_CHECK" = "OK" ]; then
  MAX_CONCURRENT=1
  STOP_REASON="thermal-throttle-cap-1"
fi

# sequential (GPU/compositor) legs from legs.conf
SEQUENTIAL_LEGS="$(awk -F'|' '/^[[:space:]]*#/ {next} /^[[:space:]]*$/ {next} $8=="yes" {printf "%s%s", (n++?",":""), $1}' "$LEGS_CONF")"

PROBED_TS="$(date -u +%FT%TZ)"
HOSTNAME_S="$(hostname)"
UNAME_S="$(uname -srm)"

# ---------------------------------------------------------------- emit json
emit_json() {
  RP_HOST="$HOSTNAME_S" RP_UNAME="$UNAME_S" RP_NPROC="$NPROC" \
  RP_MEM_T="$MEM_TOTAL_GB" RP_MEM_A="$MEM_AVAIL_GB" \
  RP_DISK_PATH="$DISK_PATH" RP_DISK="$DISK_FREE_GB" \
  RP_KVM="$KVM_PRESENT" RP_LIBVIRT="$LIBVIRT_OK" \
  RP_MC="$MAX_CONCURRENT" RP_SEQ="$SEQUENTIAL_LEGS" \
  RP_TMILLIC="$THERMAL_MILLIC" RP_TSTATE="$THERMAL_STATE" \
  RP_SENSORS="$SENSORS_LINE" \
  RP_STOP="$STOP_CHECK" RP_STOPR="$STOP_REASON" \
  RP_DOTNET="$DOTNET_SDKS" RP_QEMUIMG="$QEMU_IMG" RP_MKFS="$MKFS_EXT2" \
  RP_VCPUS="$VM_VCPUS" RP_MEM="$VM_MEM_MIB" RP_DISKG="$VM_DISK_G" \
  RP_TS="$PROBED_TS" \
  python3 - <<'PY'
import json, os
e = os.environ
doc = {
  "schema": "rev013-rig-probe-v1",
  "host": e["RP_HOST"],
  "uname": e["RP_UNAME"],
  "n_cpu": int(e["RP_NPROC"]),
  "mem_total_gb": float(e["RP_MEM_T"]),
  "mem_available_gb": float(e["RP_MEM_A"]),
  "disk_path": e["RP_DISK_PATH"],
  "disk_free_gb": int(e["RP_DISK"] or 0),
  "kvm": e["RP_KVM"] == "true",
  "libvirt_ok": e["RP_LIBVIRT"] == "true",
  "max_concurrent": int(e["RP_MC"]),
  "concurrency_formula": "clamp(floor((free_RAM_GB-4)/4),1,3)",
  "sequential_legs": [s for s in e["RP_SEQ"].split(",") if s],
  "thermal_millic": int(e["RP_TMILLIC"]),
  "thermal_state": e["RP_TSTATE"],
  "thermal_threshold_millic": int(os.environ.get("THROTTLE_MILLIC", "85000")),
  "sensors_line": e["RP_SENSORS"],
  "stop_check": e["RP_STOP"],
  "stop_reason": e["RP_STOPR"],
  "dotnet_sdks": e["RP_DOTNET"],
  "qemu_img": e["RP_QEMUIMG"],
  "mkfs_ext2": e["RP_MKFS"],
  "vm_budget": {"vcpus": int(e["RP_VCPUS"]), "mem_mib": int(e["RP_MEM"]),
                "disk_g": int(e["RP_DISKG"])},
  "probed_ts": e["RP_TS"],
}
print(json.dumps(doc, indent=2))
PY
}

JSON="$(emit_json)" || die "failed to build probe JSON"
printf '%s\n' "$JSON" > "$RIG_PROBE_JSON" || die "cannot write $RIG_PROBE_JSON"
log "rig probe written: $RIG_PROBE_JSON"
log "  cpu=$NPROC mem_avail=${MEM_AVAIL_GB}GB disk_free=${DISK_FREE_GB}GB"
log "  max_concurrent=$MAX_CONCURRENT stop_check=$STOP_CHECK ($STOP_REASON)"
log "  thermal=${THERMAL_MILLIC}mC ($THERMAL_STATE) sequential=[$SEQUENTIAL_LEGS]"

if [ "$PRINT" = 1 ]; then printf '%s\n' "$JSON"; fi
