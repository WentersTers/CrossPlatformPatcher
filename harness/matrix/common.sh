#!/usr/bin/env bash
# Shared configuration + helpers for the REV-013 matrix rig (D1-D5).
# Sourced by rig_probe.sh / vm_provision.sh / run_matrix.sh / capture_evidence.sh.
# Runs on the RIG HOST (the laptop). Never touches patch-core files.

set -uo pipefail

# ---------------------------------------------------------------- paths
MATRIX_ROOT="${MATRIX_ROOT:-$HOME/rev013}"
EVIDENCE_ROOT="${EVIDENCE_ROOT:-$MATRIX_ROOT/evidence/ISS-017}"
WORK="${WORK:-$MATRIX_ROOT/work}"
SRC_DIR="${SRC_DIR:-$MATRIX_ROOT/src}"
APP_INPUT="${APP_INPUT:-$MATRIX_ROOT/app-input}"
APP_BUNDLE="${APP_BUNDLE:-$MATRIX_ROOT/app-bundle}"
PROVISION_LOG="${PROVISION_LOG:-$WORK/provision-record.json}"
RIG_PROBE_JSON="${RIG_PROBE_JSON:-$MATRIX_ROOT/rig_probe.json}"
POOL="${POOL:-/var/lib/libvirt/images}"
SSH_KEY="${SSH_KEY:-$HOME/vms/vm_key}"
GUEST_USER="${GUEST_USER:-sage}"
GUEST_GAME_DIR="${GUEST_GAME_DIR:-/home/sage/game}"
GUEST_PREFIX="${GUEST_PREFIX:-/home/sage/game/.wine-prefix}"
DISPLAY_NUM="${DISPLAY_NUM:-:77}"
SCREEN_W="${SCREEN_W:-1920}"
SCREEN_H="${SCREEN_H:-1080}"
HOSTFWD_BASE="${HOSTFWD_BASE:-2200}"        # ssh port = HOSTFWD_BASE + leg number
VM_MEM_MIB="${VM_MEM_MIB:-4096}"            # mandate: 4 GB per VM
VM_VCPUS="${VM_VCPUS:-2}"                   # mandate: 2 vCPU per VM
VM_DISK_G="${VM_DISK_G:-32}"                # mandate: 32 GB thin qcow2
THROTTLE_MILLIC="${THROTTLE_MILLIC:-85000}" # thermal backoff threshold
BASELINE_BCL="${BASELINE_BCL:-4.6.57.0}"    # wine-mono mscorlib FileVersion
MONO_MSI_VERSION="${MONO_MSI_VERSION:-11.3.0}"

mkdir -p "$MATRIX_ROOT" "$EVIDENCE_ROOT" "$WORK"

# ---------------------------------------------------------------- logging
log() { printf '[%s] %s\n' "$(date -u +%FT%TZ)" "$*"; }
die() { log "FATAL: $*"; exit 1; }

# ---------------------------------------------------------------- legs.conf
LEGS_CONF="${LEGS_CONF:-$MATRIX_ROOT/harness/matrix/legs.conf}"
[ -f "$LEGS_CONF" ] || LEGS_CONF="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)/legs.conf"

# leg_field <LEG> <col>  (col: 1=leg 2=phase 3=os_id 4=os_pretty 5=runtime_id 6=runtime_desc 7=expected 8=sequential 9=notes)
leg_field() {
  local leg="$1" col="$2"
  awk -F'|' -v leg="$leg" -v col="$col" '
    /^[[:space:]]*#/ { next }
    /^[[:space:]]*$/ { next }
    $1 == leg { print $col; found=1; exit }
    END { if (!found) exit 1 }
  ' "$LEGS_CONF"
}

leg_enabled_for_phase() {
  local leg="$1" phase="$2"
  [ "$(leg_field "$leg" 2)" = "$phase" ]
}

leg_num() { # L01 -> 1
  echo "$((10#${1#L}))" 2>/dev/null || echo 0
}

leg_domain() { echo "matrix-$(echo "$1" | tr '[:upper:]' '[:lower:]')"; }

leg_hostfwd_port() {
  local n; n="$(leg_num "$1")"
  echo "$((HOSTFWD_BASE + n))"
}

# ---------------------------------------------------------------- guest comms
GSSH_OPTS=(-i "$SSH_KEY" -o StrictHostKeyChecking=no -o UserKnownHostsFile=/dev/null
           -o ConnectTimeout=5 -o BatchMode=yes -o LogLevel=ERROR
           -o ServerAliveInterval=5 -o ServerAliveCountMax=3)

leg_ip_file() { echo "$WORK/$(leg_domain "$1").ip"; }

leg_ip() { cat "$(leg_ip_file "$1")" 2>/dev/null; }

discover_ip() { # discover_ip <domain>  -> guest IPv4 on the default NAT net
  local dom="$1" ip i
  for i in $(seq 1 60); do
    ip="$(sudo virsh domifaddr "$dom" 2>/dev/null | awk '/ipv4/{print $NF}' | cut -d/ -f1 | head -1)"
    if [ -n "$ip" ]; then echo "$ip"; return 0; fi
    sleep 5
  done
  return 1
}

gssh() { # gssh <guest-ip> <command...>
  local target="$1"; shift
  ssh "${GSSH_OPTS[@]}" "${GUEST_USER}@${target}" "$@"
}

gscp() { # gscp <guest-ip> <local-src> <remote-dst-path>
  local target="$1" src="$2" dst="$3"
  scp "${GSSH_OPTS[@]}" "$src" "${GUEST_USER}@${target}:${dst}"
}

gscp_pull() { # gscp_pull <guest-ip> <remote-src-path> <local-dst>
  local target="$1" src="$2" dst="$3"
  scp "${GSSH_OPTS[@]}" "${GUEST_USER}@${target}:${src}" "$dst"
}

qga() { # qga <domain> <json-payload>
  sudo virsh qemu-agent-command "$1" "$2" --timeout 30
}

# qga_exec <domain> <shell command>  -> stdout of guest command (python for JSON/base64)
# Wall-clock bounded: unresponsive QGA must never hang the rig.
qga_exec() {
  local dom="$1" cmd="$2"
  QGA_DOM="$dom" QGA_CMD="$cmd" python3 - <<'PY'
import base64, json, os, subprocess, sys, time

dom, cmd = os.environ["QGA_DOM"], os.environ["QGA_CMD"]
DEADLINE = time.time() + 120  # hard wall clock for the whole command

def qga(payload):
    try:
        p = subprocess.run(["sudo", "virsh", "qemu-agent-command", dom,
                            json.dumps(payload), "--timeout", "30"],
                           capture_output=True, text=True, timeout=40)
    except subprocess.TimeoutExpired:
        return None
    if p.returncode != 0:
        return None
    try:
        return json.loads(p.stdout)["return"]
    except Exception:
        return None

r = qga({"execute": "guest-exec", "arguments": {
    "path": "/bin/sh", "arg": ["-c", cmd], "capture-output": True}})
if r is None:
    sys.stderr.write("QGA_CALL_FAILED\n"); sys.exit(2)
pid = r.get("pid")
fails = 0
while time.time() < DEADLINE:
    time.sleep(0.5)
    s = qga({"execute": "guest-exec-status", "arguments": {"pid": pid}})
    if s is None:
        fails += 1
        if fails >= 5:
            sys.stderr.write("QGA_UNRESPONSIVE\n"); sys.exit(4)
        continue
    fails = 0
    if not s.get("running"):
        out = base64.b64decode(s.get("out-data", "") or "")
        err = base64.b64decode(s.get("err-data", "") or "")
        sys.stdout.buffer.write(out)
        sys.stderr.buffer.write(err)
        sys.exit(int(s.get("exitcode") or 0))
sys.stderr.write("QGA_TIMEOUT\n"); sys.exit(3)
PY
}

wait_ssh() { # wait_ssh <guest-ip> <timeout-s>
  local target="$1" timeout="${2:-300}" waited=0
  while [ "$waited" -lt "$timeout" ]; do
    if gssh "$target" true 2>/dev/null; then
      log "ssh up on $target after ${waited}s"
      return 0
    fi
    sleep 5; waited=$((waited + 5))
  done
  return 1
}

# ---------------------------------------------------------------- json bits
json_escape() { python3 -c 'import json,sys; print(json.dumps(sys.stdin.read())[1:-1])' <<<"$1"; }

sha256_of() { sha256sum "$1" 2>/dev/null | awk '{print $1}'; }

# ---------------------------------------------------------------- RULE 1/2 enforcement
# Full game tree + real-program verification. Product-specific inventory data
# lives in the PRIVATE research repo; the public repo ships only the loader and
# required-tree.example.conf (docs/REPO-PUBLISHING.md rule 2). Load order:
#   1. $REQUIRED_TREE_CONF (explicit override)
#   2. $MATRIX_ROOT/required-tree.conf (local deployment)
#   3. script-dir/required-tree.conf (local copy, gitignored)
_required_tree_conf() {
  if [ -n "${REQUIRED_TREE_CONF:-}" ] && [ -f "$REQUIRED_TREE_CONF" ]; then
    echo "$REQUIRED_TREE_CONF"; return 0
  fi
  local conf="$MATRIX_ROOT/required-tree.conf"
  [ -f "$conf" ] || conf="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)/required-tree.conf"
  echo "$conf"
}

verify_app_tree() { # verify_app_tree <local-root>
  local root="$1" conf missing=0 n sz
  conf="$(_required_tree_conf)"
  [ -f "$conf" ] || { log "required-tree.conf missing — cannot verify (restricted operating rules)"; return 1; }
  [ -d "$root" ] || { log "tree root missing: $root"; return 1; }
  while IFS='|' read -r kind path min; do
    case "$kind" in ''|'#'*) continue ;; esac
    if [ "$kind" = "dir" ]; then
      n="$(find "$root/$path" -type f 2>/dev/null | wc -l)"
      if [ "$n" -lt "$min" ] 2>/dev/null; then
        log "  INCOMPLETE dir '$path': $n files < $min required"
        missing=1
      fi
    else  # file| and target| both verify a real file entry
      sz="$(stat -c%s "$root/$path" 2>/dev/null || echo 0)"
      if [ "$sz" -lt "$min" ] 2>/dev/null; then
        log "  INCOMPLETE file '$path': $sz bytes < $min required"
        missing=1
      fi
    fi
  done < "$conf"
  if [ "$missing" != 0 ]; then
    log "APP TREE INCOMPLETE at $root — RULE 1 VIOLATION (restricted operating rules)"
    return 1
  fi
  log "app tree verified: $root (required-tree.conf floors met; real-program entry present)"
  return 0
}

tree_target() { # patch target name, from the private input (target|<name>|...)
  local conf path
  conf="$(_required_tree_conf)"
  [ -f "$conf" ] || return 1
  awk -F'|' '$1=="target" {print $2; exit}' "$conf"
}

verify_guest_tree() { # verify_guest_tree <guest-ip> <guest-root>
  local target="$1" root="$2" conf missing=0 n sz
  conf="$(_required_tree_conf)"
  [ -f "$conf" ] || return 1
  while IFS='|' read -r kind path min; do
    case "$kind" in ''|'#'*) continue ;; esac
    if [ "$kind" = "dir" ]; then
      n="$(gssh "$target" "find '$root/$path' -type f 2>/dev/null | wc -l" 2>/dev/null | tr -dc '0-9')"
      if [ "${n:-0}" -lt "$min" ] 2>/dev/null; then
        log "  GUEST INCOMPLETE dir '$path': ${n:-0} < $min"
        missing=1
      fi
    elif [ "$kind" = "file" ]; then
      sz="$(gssh "$target" "stat -c%s '$root/$path' 2>/dev/null || echo 0" 2>/dev/null | tr -dc '0-9')"
      if [ "${sz:-0}" -lt "$min" ] 2>/dev/null; then
        log "  GUEST INCOMPLETE file '$path': ${sz:-0} < $min"
        missing=1
      fi
    fi
  done < "$conf"
  return $missing
}
