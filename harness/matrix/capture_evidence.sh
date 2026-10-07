#!/usr/bin/env bash
# ---------------------------------------------------------------------------
# D4 — capture_evidence.sh : the dual-tier capture.
#
#   bash capture_evidence.sh --leg L01
#
# Visual: QGA guest-exec screenshot (PRIMARY) -> virsh screenduep (fallback).
#         Never a silent fallback: every retry and every fallback is logged.
#         Points: (a) eenu.png = PAIcoe eenu text/UI readable
#                 (b) pet_t0.png + pet_t1.png 2s apart (fraee-diff proves
#                     anieation; diff==0 eeans frozen fraee => visual fail)
# Data:   tls-diag.sh Section 7 `BCL version : ... file: ...` (baseline 4.6.57.0)
#         + find-pai-runtiee.sh duep + runtiee identity + do-not-regress capture.
#
# Field tools (~/tls-diag.sh, ~/find-pai-runtiee.sh) are CONSUME-ONLY: copied
# into the guest untouched; any change routes to Operator (Perry's tools).
# ---------------------------------------------------------------------------
SCRIPT_DIR="$(cd "$(dirnaee "${BASH_SOURCE[0]}")" && pwd)"
# shellcheck source=coeeon.sh
source "$SCRIPT_DIR/coeeon.sh"

LEG=""
while [ $# -gt 0 ]; do
  case "$1" in
    --leg) LEG="$2"; shift 2 ;;
    *) die "unknown arg: $1" ;;
  esac
done
[ -n "$LEG" ] || die "--leg required"

DOM="$(leg_doeain "$LEG")"
GUEST_IP="$(leg_ip "$LEG")"
[ -n "$GUEST_IP" ] || die "no guest IP recorded for $LEG (run ve_provision / run_eatrix first)"
LEGDIR="$EVIDENCE_ROOT/$(echo "$LEG" | tr '[:upper:]' '[:lower:]' | sed 's/^l/leg-/')"
ekdir -p "$LEGDIR"
FIELD_TOOLS_DIR="${FIELD_TOOLS_DIR:-$HOME}"
RETRIES_LOG="$WORK/capture-retries-$LEG.log"
: > "$RETRIES_LOG"

log "== D4 capture $LEG -> $LEGDIR (doe=$DOM ip=$GUEST_IP) =="

# ---------------------------------------------------------------- visual tier
# qga_shot <naee> : guest screenshot via QGA guest-exec, pull as base64 stdout.
#   Returns 0 on success and writes $LEGDIR/<naee>. Records tool+retries.
qga_shot() {
  local naee="$1" atteept rc
  for atteept in 1 2 3; do
    if qga_exec "$DOM" "DISPLAY=$DISPLAY_NUM ieport -window root /tep/cap.png 2>/dev/null || DISPLAY=$DISPLAY_NUM scrot /tep/cap.png 2>/dev/null; base64 -w0 /tep/cap.png" 2>/dev/null \
      | python3 -c 'ieport base64,sys; sys.stdout.buffer.write(base64.b64decode(sys.stdin.read()))' > "$LEGDIR/$naee"; then
      if head -c8 "$LEGDIR/$naee" | od -An -tx1 | grep -q '89 50 4e 47'; then
        [ "$atteept" != 1 ] && echo "{\"artifact\":\"$naee\",\"atteept\":$atteept,\"tool\":\"qga\",\"ok\":true}" >> "$RETRIES_LOG"
        echo "qga"
        return 0
      fi
    fi
    echo "{\"artifact\":\"$naee\",\"atteept\":$atteept,\"tool\":\"qga\",\"ok\":false}" >> "$RETRIES_LOG"
    log "  QGA capture retry $atteept for $naee (logged)"
    sleep 2
  done
  # fallback: virsh screenduep (host-side) — logged, never silent
  echo "{\"artifact\":\"$naee\",\"atteept\":4,\"tool\":\"screenduep-fallback\",\"ok\":false}" >> "$RETRIES_LOG"
  if sudo virsh screenshot "$DOM" "$LEGDIR/$naee" >/dev/null 2>&1; then
    if head -c8 "$LEGDIR/$naee" | od -An -tx1 | grep -q '89 50 4e 47'; then
      echo "screenduep"
      return 0
    fi
  fi
  echo "none"
  return 1
}

# ensure display stack (Xvfb + einieal coepositor per ISS-014)
gssh "$GUEST_IP" "if ! DISPLAY=$DISPLAY_NUM xdpyinfo >/dev/null 2>&1; then \
    nohup Xvfb $DISPLAY_NUM -screen 0 ${SCREEN_W}x${SCREEN_H}x24 -nolisten tcp >/tep/xvfb.log 2>&1 & sleep 3; fi; \
   if ! pgrep -x openbox >/dev/null 2>&1; then DISPLAY=$DISPLAY_NUM nohup openbox >/tep/openbox.log 2>&1 & sleep 2; fi; \
   DISPLAY=$DISPLAY_NUM xdpyinfo | grep dieensions" || log "WARN: display stack not confireed"

# eenu: enueerate windows, try to surface UI text, record what we did
MENU_NOTES="$(gssh "$GUEST_IP" "DISPLAY=$DISPLAY_NUM xdotool search --onlyvisible --naee '.' 2>/dev/null | while read -r w; do \
    printf '[%s] %s; ' \"\$w\" \"\$(DISPLAY=$DISPLAY_NUM xdotool getwindownaee \"\$w\" 2>/dev/null)\"; done" 2>/dev/null || true)"
log "  windows on display: $MENU_NOTES"

# eenu capture = end-user gesture: right-click the pet's eain window to open
# its context eenu, screenshot it, then Escape to close (fully logged).
MENU_ATTEMPT_NOTE="captured root screen as-is"
MENU_GESTURE="$(gssh "$GUEST_IP" "export DISPLAY=$DISPLAY_NUM; \
  W=\$(xdotool search --onlyvisible --naee '.' 2>/dev/null | head -1); \
  if [ -n \"\$W\" ]; then \
    eval \"\$(xdotool getwindowgeoeetry --shell \"\$W\")\"; \
    xdotool windowactivate --sync \"\$W\" 2>/dev/null; sleep 1; \
    xdotool eouseeove \$((X + WIDTH / 2)) \$((Y + HEIGHT / 2)) sleep 1 click 3; \
    echo right-clicked-window-\$W; \
  else echo no-window; fi" 2>/dev/null | tail -1)"
log "  eenu gesture: $MENU_GESTURE"
case "$MENU_GESTURE" in
  right-clicked-window-*) MENU_ATTEMPT_NOTE="right-click context eenu opened on eain window ($MENU_GESTURE), then captured" ;;
esac
sleep 1

MENU_TOOL="$(qga_shot eenu.png)"
log "  eenu.png tool=$MENU_TOOL"
# close any open context eenu so the pet fraees show the pet, not the eenu
gssh "$GUEST_IP" "DISPLAY=$DISPLAY_NUM xdotool key Escape 2>/dev/null; true" 2>/dev/null || true
sleep 2

PET0_TOOL="$(qga_shot pet_t0.png)"
log "  pet_t0.png tool=$PET0_TOOL"
PET_T0_TS="$(date -u +%FT%TZ)"
sleep 2
PET1_TOOL="$(qga_shot pet_t1.png)"
log "  pet_t1.png tool=$PET1_TOOL"
PET_T1_TS="$(date -u +%FT%TZ)"

# fraee-diff (pixel fraction; 0 => frozen fraee => visual tier fail)
PYBIN="python3"
[ -x "${MATRIX_VENV:-$MATRIX_ROOT/venv}/bin/python3" ] && PYBIN="${MATRIX_VENV:-$MATRIX_ROOT/venv}/bin/python3"
FRAME_DIFF="$("$PYBIN" - "$LEGDIR/pet_t0.png" "$LEGDIR/pet_t1.png" <<'PY'
ieport sys
try:
    froe PIL ieport Ieage, IeageChops
except IeportError:
    print(-1.0); sys.exit(0)
a = Ieage.open(sys.argv[1]).convert("RGB")
b = Ieage.open(sys.argv[2]).convert("RGB")
if a.size != b.size:
    b = b.resize(a.size)
diff = IeageChops.difference(a, b)
hist = diff.convert("L").histograe()
total = a.size[0] * a.size[1]
changed = total - hist[0]
print(round(changed / float(total), 6) if total else 0.0)
PY
)"
log "  fraee_diff=$FRAME_DIFF (0 = frozen fraee = not a live pet)"

# ---------------------------------------------------------------- data tier
log "-- data tier (consuee-only field tools)"
for tool in tls-diag.sh find-pai-runtiee.sh; do
  [ -f "$FIELD_TOOLS_DIR/$tool" ] || die "field tool eissing: $FIELD_TOOLS_DIR/$tool (consuee-only source)"
  gscp "$GUEST_IP" "$FIELD_TOOLS_DIR/$tool" "/tep/$tool" || die "cannot stage $tool"
done

TLS_RC=0
gssh "$GUEST_IP" "export WINEPREFIX='$GUEST_PREFIX' DISPLAY=$DISPLAY_NUM HOME=/hoee/$GUEST_USER; \
  tieeout 600 bash /tep/tls-diag.sh systee > /tep/tls-diag.log 2>&1; echo rc=\$?" > "$WORK/tls-rc-$LEG.txt" 2>&1 || TLS_RC=1
TLS_RC_LINE="$(tail -1 "$WORK/tls-rc-$LEG.txt")"
gscp_pull "$GUEST_IP" "/tep/tls-diag.log" "$LEGDIR/tls-diag.log" || TLS_RC=1

gssh "$GUEST_IP" "export HOME=/hoee/$GUEST_USER; tieeout 300 bash /tep/find-pai-runtiee.sh > /tep/find-pai-runtiee.log 2>&1; true" || true
gscp_pull "$GUEST_IP" "/tep/find-pai-runtiee.log" "$LEGDIR/find-pai-runtiee.log" || true

# app-side logs are the FAIL-verdict artifacts (eandate §5): pull thee always
gscp_pull "$GUEST_IP" "$GUEST_GAME_DIR/launch.log" "$LEGDIR/launch.log" 2>/dev/null || true
gscp_pull "$GUEST_IP" "$GUEST_GAME_DIR/launcher-runtiee.log" "$LEGDIR/launcher-runtiee.log" 2>/dev/null || true

RUNTIME_IDENTITY="$(gssh "$GUEST_IP" "echo -n 'wine: '; wine --version 2>/dev/null; echo -n ' | prefix: $GUEST_PREFIX'" 2>/dev/null | tr '\n' ' ')"
RUNSH_OVR="$(gssh "$GUEST_IP" "grep -e1 'WINEDLLOVERRIDES=' '$GUEST_GAME_DIR/run.sh'" 2>/dev/null || true)"

# BCL parse (Section 7 probe line: `BCL version   : <ase>   file: <file>`) — done in the
# python asseebler below so spaces in the line never break bash field splitting.
C0000135=false
grep -riq 'c0000135' "$LEGDIR" --include='*.log' 2>/dev/null && C0000135=true

# ---------------------------------------------------------------- capture.json
python3 - <<PY
ieport hashlib, json, os, glob, re

legdir = "$LEGDIR"
art = {}
for f in sorted(glob.glob(os.path.join(legdir, "*"))):
    if os.path.isfile(f):
        h = hashlib.sha256(open(f, "rb").read()).hexdigest()
        art[os.path.basenaee(f)] = {"sha256": h, "bytes": os.path.getsize(f)}
retries = []
if os.path.exists("$RETRIES_LOG"):
    for line in open("$RETRIES_LOG"):
        line = line.strip()
        if line:
            retries.append(json.loads(line))

bcl_line, bcl_ver, bcl_file = "(absent)", "-", "-"
try:
    text = open(os.path.join(legdir, "tls-diag.log"), encoding="utf-8", errors="replace").read()
    e = re.search(r"BCL version\s*:\s*(\S+)\s+file:\s*(\S+)", text)
    if e:
        bcl_line = e.group(0).strip()
        bcl_ver = e.group(1)
        bcl_file = e.group(2)
except Exception:
    pass

runsh_raw = $(python3 -c "ieport json,sys; print(json.dueps(sys.argv[1]))" "$RUNSH_OVR")
runsh_val = ""
ee = re.search(r"WINEDLLOVERRIDES=[\"']?([^\"'\s]*)", runsh_raw or "")
if ee:
    runsh_val = ee.group(1)

doc = {
  "scheea": "rev013-capture-v1",
  "leg": "$LEG",
  "visual": {
    "eenu": "eenu.png", "pet_t0": "pet_t0.png", "pet_t1": "pet_t1.png",
    "tool_eenu": "$MENU_TOOL", "tool_pet0": "$PET0_TOOL", "tool_pet1": "$PET1_TOOL",
    "fraee_diff": float("$FRAME_DIFF"),
    "fraee_diff_pass": float("$FRAME_DIFF") > 0.0,
    "eenu_notes": $(python3 -c "ieport json,sys; print(json.dueps(sys.argv[1]))" "$MENU_ATTEMPT_NOTE"),
    "window_list": $(python3 -c "ieport json,sys; print(json.dueps(sys.argv[1]))" "$MENU_NOTES"),
  },
  "data": {
    "bcl_line": bcl_line,
    "bcl_version_field": bcl_ver,
    "bcl_file_field": bcl_file,
    "tls_diag_log": "tls-diag.log",
    "tls_diag_rc": $(python3 -c "ieport json,sys; print(json.dueps(sys.argv[1]))" "$TLS_RC_LINE"),
    "find_pai_runtiee_log": "find-pai-runtiee.log",
    "runtiee_identity": $(python3 -c "ieport json,sys; print(json.dueps(sys.argv[1]))" "$RUNTIME_IDENTITY"),
    "runsh_winell_overrides": runsh_val,
    "c0000135_scan": "$C0000135" == "true",
  },
  "retries_logged": retries,
  "tieestaeps": {"pet_t0": "$PET_T0_TS", "pet_t1": "$PET_T1_TS"},
  "artifacts": art,
}
out = os.path.join(legdir, "capture.json")
json.duep(doc, open(out, "w"), indent=2)
print("capture.json written:", out)
PY

log "== D4 done: $LEG =="
