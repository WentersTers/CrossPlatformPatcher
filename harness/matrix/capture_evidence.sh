#!/usr/bin/env bash
# ---------------------------------------------------------------------------
# D4 - capture_evidence.sh : the dual-tier capture.
#
#   bash capture_evidence.sh --leg L01
#
# Visual: QGA guest-exec screenshot (PRIMARY) -> virsh screendump (fallback).
#         Never a silent fallback: every retry and every fallback is logged.
#         Points: (a) menu.png = product menu text/UI readable
#                 (b) pet_t0.png + pet_t1.png 2s apart (frame-diff proves
#                     animation; diff==0 means frozen frame => visual fail)
# Data:   field-tool Section 7 BCL line (baseline 4.6.57.0) + identity (mono/
#         native) + mscorlib path + runtime identity + do-not-regress capture.
#
# Field tools are CONSUME-ONLY: copied into the guest untouched; any change
# routes to their owner (Operator).
# ---------------------------------------------------------------------------
SCRIPT_DIR="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
# shellcheck source=common.sh
source "$SCRIPT_DIR/common.sh" || { echo "FATAL: common.sh failed to source"; exit 1; }
type log >/dev/null 2>&1 || { echo "FATAL: common.sh functions unavailable"; exit 1; }

LEG=""
while [ $# -gt 0 ]; do
  case "$1" in
    --leg) LEG="$2"; shift 2 ;;
    *) die "unknown arg: $1" ;;
  esac
done
[ -n "$LEG" ] || die "--leg required"

DOM="$(leg_domain "$LEG")"
GUEST_IP="$(leg_ip "$LEG")"
[ -n "$GUEST_IP" ] || die "no guest IP recorded for $LEG (run vm_provision / run_matrix first)"
LEGDIR="$EVIDENCE_ROOT/$(echo "$LEG" | tr '[:upper:]' '[:lower:]' | sed 's/^l/leg-/')"
mkdir -p "$LEGDIR"
FIELD_TOOLS_DIR="${FIELD_TOOLS_DIR:-$HOME}"
RETRIES_LOG="$WORK/capture-retries-$LEG.log"
: > "$RETRIES_LOG"

log "== D4 capture $LEG -> $LEGDIR (dom=$DOM ip=$GUEST_IP) =="

# ---------------------------------------------------------------- visual tier
# qga_shot <name> : guest screenshot via QGA guest-exec, pull as base64 stdout.
qga_shot() {
  local name="$1" attempt rc
  for attempt in 1 2 3; do
    if qga_exec "$DOM" "DISPLAY=$DISPLAY_NUM import -window root /tmp/cap.png 2>/dev/null || DISPLAY=$DISPLAY_NUM scrot /tmp/cap.png 2>/dev/null; base64 -w0 /tmp/cap.png" 2>/dev/null \
      | python3 -c 'import base64,sys; sys.stdout.buffer.write(base64.b64decode(sys.stdin.read()))' > "$LEGDIR/$name"; then
      if head -c8 "$LEGDIR/$name" | od -An -tx1 | grep -q '89 50 4e 47'; then
        [ "$attempt" != 1 ] && echo "{\"artifact\":\"$name\",\"attempt\":$attempt,\"tool\":\"qga\",\"ok\":true}" >> "$RETRIES_LOG"
        echo "qga"
        return 0
      fi
    fi
    echo "{\"artifact\":\"$name\",\"attempt\":$attempt,\"tool\":\"qga\",\"ok\":false}" >> "$RETRIES_LOG"
    log "  QGA capture retry $attempt for $name (logged)"
    sleep 2
  done
  # fallback: virsh screendump (host-side) - logged, never silent
  echo "{\"artifact\":\"$name\",\"attempt\":4,\"tool\":\"screendump-fallback\",\"ok\":false}" >> "$RETRIES_LOG"
  if sudo virsh screenshot "$DOM" "$LEGDIR/$name" >/dev/null 2>&1; then
    if head -c8 "$LEGDIR/$name" | od -An -tx1 | grep -q '89 50 4e 47'; then
      echo "screendump"
      return 0
    fi
  fi
  echo "none"
  return 1
}

# ensure display stack (Xvfb + minimal compositor per ISS-014)
gssh "$GUEST_IP" "if ! DISPLAY=$DISPLAY_NUM xdpyinfo >/dev/null 2>&1; then \
    nohup Xvfb $DISPLAY_NUM -screen 0 ${SCREEN_W}x${SCREEN_H}x24 -nolisten tcp >/tmp/xvfb.log 2>&1 & sleep 3; fi; \
   if ! pgrep -x openbox >/dev/null 2>&1; then DISPLAY=$DISPLAY_NUM nohup openbox >/tmp/openbox.log 2>&1 & sleep 2; fi; \
   DISPLAY=$DISPLAY_NUM xdpyinfo | grep dimensions" || log "WARN: display stack not confirmed"

MENU_NOTES="$(gssh "$GUEST_IP" "DISPLAY=$DISPLAY_NUM xdotool search --onlyvisible --name '.' 2>/dev/null | while read -r w; do \
    printf '[%s] %s; ' \"\$w\" \"\$(DISPLAY=$DISPLAY_NUM xdotool getwindowname \"\$w\" 2>/dev/null)\"; done" 2>/dev/null || true)"
log "  windows on display: $MENU_NOTES"

# menu capture = end-user gesture: right-click the main window to open
# its context menu, screenshot it, then Escape to close (fully logged).
MENU_ATTEMPT_NOTE="captured root screen as-is"
MENU_GESTURE="$(gssh "$GUEST_IP" "export DISPLAY=$DISPLAY_NUM; \
  W=\$(xdotool search --onlyvisible --name '.' 2>/dev/null | head -1); \
  if [ -n \"\$W\" ]; then \
    eval \"\$(xdotool getwindowgeometry --shell \"\$W\")\"; \
    xdotool windowactivate --sync \"\$W\" 2>/dev/null; sleep 1; \
    xdotool mousemove \$((X + WIDTH / 2)) \$((Y + HEIGHT / 2)) sleep 1 click 3; \
    echo right-clicked-window-\$W; \
  else echo no-window; fi" 2>/dev/null | tail -1)"
log "  menu gesture: $MENU_GESTURE"
case "$MENU_GESTURE" in
  right-clicked-window-*) MENU_ATTEMPT_NOTE="right-click context menu opened on main window ($MENU_GESTURE), then captured" ;;
esac
sleep 1

MENU_TOOL="$(qga_shot menu.png)"
log "  menu.png tool=$MENU_TOOL"
# close any open context menu so the pet frames show the pet, not the menu
gssh "$GUEST_IP" "DISPLAY=$DISPLAY_NUM xdotool key Escape 2>/dev/null; true" 2>/dev/null || true
sleep 2

PET0_TOOL="$(qga_shot pet_t0.png)"
log "  pet_t0.png tool=$PET0_TOOL"
PET_T0_TS="$(date -u +%FT%TZ)"
sleep 2
PET1_TOOL="$(qga_shot pet_t1.png)"
log "  pet_t1.png tool=$PET1_TOOL"
PET_T1_TS="$(date -u +%FT%TZ)"

# frame-diff (pixel fraction; 0 => frozen frame => visual tier fail)
PYBIN="python3"
[ -x "${MATRIX_VENV:-$MATRIX_ROOT/venv}/bin/python3" ] && PYBIN="${MATRIX_VENV:-$MATRIX_ROOT/venv}/bin/python3"
FRAME_DIFF="$("$PYBIN" - "$LEGDIR/pet_t0.png" "$LEGDIR/pet_t1.png" <<'PY'
import sys
try:
    from PIL import Image, ImageChops
except ImportError:
    print(-1.0); sys.exit(0)
a = Image.open(sys.argv[1]).convert("RGB")
b = Image.open(sys.argv[2]).convert("RGB")
if a.size != b.size:
    b = b.resize(a.size)
diff = ImageChops.difference(a, b)
hist = diff.convert("L").histogram()
total = a.size[0] * a.size[1]
changed = total - hist[0]
print(round(changed / float(total), 6) if total else 0.0)
PY
)"
log "  frame_diff=$FRAME_DIFF (0 = frozen frame = not a live subject)"

# ---------------------------------------------------------------- data tier
log "-- data tier (consume-only field tools)"
for tool in tls-diag.sh find-pai-runtime.sh; do
  [ -f "$FIELD_TOOLS_DIR/$tool" ] || die "field tool missing: $FIELD_TOOLS_DIR/$tool (consume-only source)"
  gscp "$GUEST_IP" "$FIELD_TOOLS_DIR/$tool" "/tmp/$tool" || die "cannot stage $tool"
done

TLS_RC=0
gssh "$GUEST_IP" "export WINEPREFIX='$GUEST_PREFIX' DISPLAY=$DISPLAY_NUM HOME=/home/$GUEST_USER; \
  timeout 600 bash /tmp/tls-diag.sh system > /tmp/tls-diag.log 2>&1; echo rc=\$?" > "$WORK/tls-rc-$LEG.txt" 2>&1 || TLS_RC=1
TLS_RC_LINE="$(tail -1 "$WORK/tls-rc-$LEG.txt")"
gscp_pull "$GUEST_IP" "/tmp/tls-diag.log" "$LEGDIR/tls-diag.log" || TLS_RC=1

gssh "$GUEST_IP" "export HOME=/home/$GUEST_USER; timeout 300 bash /tmp/find-pai-runtime.sh > /tmp/find-pai-runtime.log 2>&1; true" || true
gscp_pull "$GUEST_IP" "/tmp/find-pai-runtime.log" "$LEGDIR/find-pai-runtime.log" || true

# app-side logs are the FAIL-verdict artifacts (mandate S5): pull them always
gscp_pull "$GUEST_IP" "$GUEST_GAME_DIR/launch.log" "$LEGDIR/launch.log" 2>/dev/null || true
gscp_pull "$GUEST_IP" "$GUEST_GAME_DIR/launcher-runtime.log" "$LEGDIR/launcher-runtime.log" 2>/dev/null || true

RUNTIME_IDENTITY="$(gssh "$GUEST_IP" "echo -n 'wine: '; wine --version 2>/dev/null; echo -n ' | prefix: $GUEST_PREFIX'" 2>/dev/null | tr '\n' ' ')"
RUNSH_OVR="$(gssh "$GUEST_IP" "grep -m1 'WINEDLLOVERRIDES=' '$GUEST_GAME_DIR/run.sh'" 2>/dev/null || true)"

C0000135=false
grep -riq 'c0000135' "$LEGDIR" --include='*.log' 2>/dev/null && C0000135=true

# ---------------------------------------------------------------- capture.json
python3 - <<PY
import hashlib, json, os, glob, re

legdir = "$LEGDIR"
art = {}
for f in sorted(glob.glob(os.path.join(legdir, "*"))):
    if os.path.isfile(f):
        h = hashlib.sha256(open(f, "rb").read()).hexdigest()
        art[os.path.basename(f)] = {"sha256": h, "bytes": os.path.getsize(f)}
retries = []
if os.path.exists("$RETRIES_LOG"):
    for line in open("$RETRIES_LOG"):
        line = line.strip()
        if line:
            retries.append(json.loads(line))

bcl_line, bcl_ver, bcl_file = "(absent)", "-", "-"
bcl_path, bcl_identity = "-", "-"
try:
    text = open(os.path.join(legdir, "tls-diag.log"), encoding="utf-8", errors="replace").read()
    m = re.search(r"BCL version\s*:\s*(\S+)\s+file:\s*(\S+)", text)
    if m:
        bcl_line = m.group(0).strip()
        bcl_ver = m.group(1)
        bcl_file = m.group(2)
    p = re.search(r"mscorlib\s*:\s*(\S+)", text)
    if p:
        bcl_path = p.group(1)
        low = bcl_path.lower()
        bcl_identity = "mono" if "mono" in low else ("native" if "microsoft.net" in low else "unknown")
except Exception:
    pass

runsh_raw = $(python3 -c "import json,sys; print(json.dumps(sys.argv[1]))" "$RUNSH_OVR")
runsh_val = ""
mm = re.search(r"WINEDLLOVERRIDES=[\"']?([^\"'\s]*)", runsh_raw or "")
if mm:
    runsh_val = mm.group(1)

doc = {
  "schema": "rev013-capture-v1",
  "leg": "$LEG",
  "visual": {
    "menu": "menu.png", "pet_t0": "pet_t0.png", "pet_t1": "pet_t1.png",
    "tool_menu": "$MENU_TOOL", "tool_pet0": "$PET0_TOOL", "tool_pet1": "$PET1_TOOL",
    "frame_diff": float("$FRAME_DIFF"),
    "frame_diff_pass": float("$FRAME_DIFF") > 0.0,
    "menu_notes": $(python3 -c "import json,sys; print(json.dumps(sys.argv[1]))" "$MENU_ATTEMPT_NOTE"),
    "window_list": $(python3 -c "import json,sys; print(json.dumps(sys.argv[1]))" "$MENU_NOTES"),
  },
  "data": {
    "bcl_line": bcl_line,
    "bcl_version_field": bcl_ver,
    "bcl_file_field": bcl_file,
    "bcl_mscorlib_path": bcl_path,
    "bcl_identity": bcl_identity,
    "tls_diag_log": "tls-diag.log",
    "tls_diag_rc": $(python3 -c "import json,sys; print(json.dumps(sys.argv[1]))" "$TLS_RC_LINE"),
    "find_pai_runtime_log": "find-pai-runtime.log",
    "runtime_identity": $(python3 -c "import json,sys; print(json.dumps(sys.argv[1]))" "$RUNTIME_IDENTITY"),
    "runsh_winell_overrides": runsh_val,
    "c0000135_scan": "$C0000135" == "true",
  },
  "retries_logged": retries,
  "timestamps": {"pet_t0": "$PET_T0_TS", "pet_t1": "$PET_T1_TS"},
  "artifacts": art,
}
out = os.path.join(legdir, "capture.json")
json.dump(doc, open(out, "w"), indent=2)
print("capture.json written:", out)
PY

log "== D4 done: $LEG =="
