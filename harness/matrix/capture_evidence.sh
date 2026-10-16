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
# Targeting: skip framework exception dialogs (CONTROL run showed the
# gesture right-clicking the dialog instead of the menu) — prefer the
# largest non-framework window, fall back to first visible.
MENU_ATTEMPT_NOTE="captured root screen as-is"
MENU_GESTURE="$(gssh "$GUEST_IP" "export DISPLAY=$DISPLAY_NUM; \
  W=''; \
  for w in \$(xdotool search --onlyvisible --name '.' 2>/dev/null); do \
    n=\$(xdotool getwindowname \"\$w\" 2>/dev/null); \
    case \"\$n\" in *[Mm]icrosoft*[Nn][Ee][Tt]*[Ff]ramework*|*nhandled*) continue ;; esac; \
    W=\"\$w\"; break; \
  done; \
  [ -z \"\$W\" ] && W=\$(xdotool search --onlyvisible --name '.' 2>/dev/null | head -1); \
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
# close any open context menu so the pet frames show the pet, not the menu —
# BUT skip the Escape when a framework dialog is up (REV-031 ladder owns
# dismissal then; the early Escape was nondeterministically dismissing the
# dialog before the Details/Continue sequence could run).
MENU_FW_PRESENT="$(gssh "$GUEST_IP" "export DISPLAY=$DISPLAY_NUM; \
  for w in \$(xdotool search --name '.' 2>/dev/null); do \
    n=\$(xdotool getwindowname \"\$w\" 2>/dev/null); \
    case \"\$n\" in *[Mm]icrosoft*[Nn][Ee][Tt]*[Ff]ramework*|*nhandled*|*xception*) echo yes; break ;; \
    esac; \
  done" 2>/dev/null | tail -1)"
if [ "$MENU_FW_PRESENT" = "yes" ]; then
  log "  framework dialog up at menu time — early Escape SKIPPED (ladder owns dismissal)"
else
  gssh "$GUEST_IP" "DISPLAY=$DISPLAY_NUM xdotool key Escape 2>/dev/null; true" 2>/dev/null || true
  sleep 2
fi

PET0_TOOL="$(qga_shot pet_t0.png)"
log "  pet_t0.png tool=$PET0_TOOL"
PET_T0_TS="$(date -u +%FT%TZ)"
sleep 2
PET1_TOOL="$(qga_shot pet_t1.png)"
log "  pet_t1.png tool=$PET1_TOOL"
PET_T1_TS="$(date -u +%FT%TZ)"

# ---------------------------------------------------------------- window-stack dump
# Timestamped window list + geometry per window (settles ISS-029 family:
# framework window in xdotool list but absent from pixels/logs).
dump_window_stack() {
  local tag="$1" ts
  ts="$(date -u +%FT%TZ)"
  {
    echo "== window-stack $tag @ $ts =="
    gssh "$GUEST_IP" "export DISPLAY=$DISPLAY_NUM; \
      for w in \$(xdotool search --onlyvisible --name '.' 2>/dev/null); do \
        n=\$(DISPLAY=$DISPLAY_NUM xdotool getwindowname \"\$w\" 2>/dev/null); \
        g=\$(DISPLAY=$DISPLAY_NUM xdotool getwindowgeometry \"\$w\" 2>/dev/null | tr '\n' ' '); \
        echo \"[$ts] [\$w] \$n :: \$g\"; \
      done; \
      echo \"total-visible: \$(DISPLAY=$DISPLAY_NUM xdotool search --onlyvisible --name '.' 2>/dev/null | wc -l)\"" 2>/dev/null || echo "window-stack $tag FAILED @ $ts"
  } | tee -a "$LEGDIR/window-stack.log"
}
: > "$LEGDIR/window-stack.log" 2>/dev/null || true
dump_window_stack "pre-data"

# frame-diff helper for dialog pixel-compare (0 = identical = click ineffective)
frame_frac() { # frame_frac <png-a> <png-b> -> changed fraction
  PYBIN2="python3"
  [ -x "${MATRIX_VENV:-$MATRIX_ROOT/venv}/bin/python3" ] && PYBIN2="${MATRIX_VENV:-$MATRIX_ROOT/venv}/bin/python3"
  "$PYBIN2" - "$1" "$2" <<'PY'
import sys
try:
    from PIL import Image, ImageChops
except ImportError:
    print(-1.0); sys.exit(0)
try:
    a = Image.open(sys.argv[1]).convert("RGB")
    b = Image.open(sys.argv[2]).convert("RGB")
except Exception:
    print(-1.0); sys.exit(0)
if a.size != b.size:
    b = b.resize(a.size)
diff = ImageChops.difference(a, b)
hist = diff.convert("L").histogram()
total = a.size[0] * a.size[1]
changed = total - hist[0]
print(round(changed / float(total), 6) if total else 0.0)
PY
}

find_fw_dialog() { # echo window id of framework dialog, or empty
  gssh "$GUEST_IP" "export DISPLAY=$DISPLAY_NUM; \
    for w in \$(xdotool search --name '.' 2>/dev/null); do \
      n=\$(xdotool getwindowname \"\$w\" 2>/dev/null); \
      case \"\$n\" in \
        *[Mm]icrosoft*[Nn][Ee][Tt]*[Ff]ramework*|*nhandled*|*xception*) echo \"\$w\"; break ;; \
      esac; \
    done" 2>/dev/null | tail -1
}

# ---------------------------------------------------------------- Details + Continue
# Revised contract (REV-031 §6): Details click is read-only evidence (expands
# the stack text; zero state change). Then ONE mouse Continue click -> 5s ->
# capture + pixel-compare (ineffectiveness is a per-run recorded fact); if the
# dialog persists, ONE keyboard Return -> 5s -> capture; if still persistent,
# Escape as logged last resort. Quit NEVER. Resulting state tagged.
CONTINUE_ACTION="not-present"
DISMISSAL_METHOD="none"
CLICK_T0_EPOCH=""; CLICK_T0=""; RETURN_T0_EPOCH=""; RETURN_T0=""; ESCAPE_T0_EPOCH=""; ESCAPE_T0=""
FW_WIN="$(find_fw_dialog)"
if [ -n "$FW_WIN" ]; then
  CONTINUE_TS="$(date -u +%FT%TZ)"
  log "  framework dialog present: window $FW_WIN @ $CONTINUE_TS — Details first (read-only), then Continue sequence"
  # Details expansion: bottom-left button of the dialog (read-only, no state
  # change). Capture the expansion LIVE (V5.6: dialog-bearing runs must):
  # pre-click frame, expand attempt, pixel-change verification, accelerator-key
  # fallback only (no tabbing - Quit is never risked).
  COLLAPSED_TOOL="$(qga_shot dialog-collapsed.png)"
  log "  dialog-collapsed.png tool=$COLLAPSED_TOOL (pre-Details reference)"
  gssh "$GUEST_IP" "export DISPLAY=$DISPLAY_NUM; \
    xdotool windowactivate --sync '$FW_WIN' 2>/dev/null; sleep 1; \
    eval \"\$(xdotool getwindowgeometry --shell '$FW_WIN' 2>/dev/null)\"; \
    echo \"geom X=\$X Y=\$Y WIDTH=\$WIDTH HEIGHT=\$HEIGHT\"; \
    xdotool mousemove \$((X + 55)) \$((Y + HEIGHT - 25)) sleep 1 click 1" 2>/dev/null | tee -a "$LEGDIR/window-stack.log" || true
  sleep 2
  DETAILS_TOOL="$(qga_shot details-expanded.png)"
  DETAILS_FRAC="$(frame_frac "$LEGDIR/dialog-collapsed.png" "$LEGDIR/details-expanded.png")"
  log "  details-expanded.png tool=$DETAILS_TOOL (mouse attempt) expansion-frac=$DETAILS_FRAC"
  if [ "$DETAILS_FRAC" = "0.0" ] || [ "$DETAILS_FRAC" = "0" ]; then
    log "  details did not expand on mouse click - keyboard fallback (alt+d)"
    gssh "$GUEST_IP" "export DISPLAY=$DISPLAY_NUM; xdotool windowactivate --sync '$FW_WIN' 2>/dev/null; sleep 1; \
      xdotool key alt+d 2>/dev/null; true" 2>/dev/null || true
    sleep 2
    DETAILS_TOOL="$(qga_shot details-expanded.png)"
    DETAILS_FRAC="$(frame_frac "$LEGDIR/dialog-collapsed.png" "$LEGDIR/details-expanded.png")"
    log "  details-expanded.png tool=$DETAILS_TOOL (alt+d attempt) expansion-frac=$DETAILS_FRAC"
    if [ "$DETAILS_FRAC" = "0.0" ] || [ "$DETAILS_FRAC" = "0" ]; then
      log "  details still unexpanded - second keyboard attempt (d)"
      gssh "$GUEST_IP" "export DISPLAY=$DISPLAY_NUM; xdotool windowactivate --sync '$FW_WIN' 2>/dev/null; sleep 1; \
        xdotool key d 2>/dev/null; true" 2>/dev/null || true
      sleep 2
      DETAILS_TOOL="$(qga_shot details-expanded.png)"
      DETAILS_FRAC="$(frame_frac "$LEGDIR/dialog-collapsed.png" "$LEGDIR/details-expanded.png")"
      log "  details-expanded.png tool=$DETAILS_TOOL (d attempt) expansion-frac=$DETAILS_FRAC"
    fi
  fi
  # mouse Continue: middle button of the bottom row (corrected H-25; the H-45
  # click in the CONTROL leg demonstrably missed/did nothing — logged in-campaign)
  PRE_TOOL="$(qga_shot dialog-pre.png)"
  gssh "$GUEST_IP" "export DISPLAY=$DISPLAY_NUM; \
    xdotool windowactivate --sync '$FW_WIN' 2>/dev/null; sleep 1; \
    eval \"\$(xdotool getwindowgeometry --shell '$FW_WIN' 2>/dev/null)\"; \
    xdotool mousemove \$((X + WIDTH / 2)) \$((Y + HEIGHT - 25)) sleep 1 click 1" 2>/dev/null || true
  CLICK_T0_EPOCH="$(date +%s.%N)"; CLICK_T0="$(date -u +%FT%TZ)"
  CONTINUE_ACTION="clicked-continue-once@$CONTINUE_TS window=$FW_WIN (Quit never touched)"
  log "  Continue clicked once; fixed 5s delay then compare capture"
  sleep 5
  POST_TOOL="$(qga_shot post-continue.png)"
  log "  post-continue.png tool=$POST_TOOL"
  MOUSE_FRAC="$(frame_frac "$LEGDIR/dialog-pre.png" "$LEGDIR/post-continue.png")"
  log "  mouse-Continue pixel change fraction: $MOUSE_FRAC (0 = ineffective)"
  if [ "$MOUSE_FRAC" = "0.0" ] || [ "$MOUSE_FRAC" = "0" ]; then
    STILL="$(find_fw_dialog)"
    if [ -n "$STILL" ]; then
      log "  dialog persists after mouse click — ONE keyboard Return (approved fallback)"
      gssh "$GUEST_IP" "export DISPLAY=$DISPLAY_NUM; xdotool windowactivate --sync '$STILL' 2>/dev/null; sleep 1; \
        xdotool key --window '$STILL' Return 2>/dev/null || xdotool key Return 2>/dev/null; true" 2>/dev/null || true
      RETURN_T0_EPOCH="$(date +%s.%N)"; RETURN_T0="$(date -u +%FT%TZ)"
      CONTINUE_ACTION="$CONTINUE_ACTION + keyboard-Return@$ (dialog persisted after mouse)"
      sleep 5
      RET_TOOL="$(qga_shot post-return.png)"
      log "  post-return.png tool=$RET_TOOL"
      RET_FRAC="$(frame_frac "$LEGDIR/post-continue.png" "$LEGDIR/post-return.png")"
      log "  keyboard-Return pixel change fraction: $RET_FRAC"
      STILL2="$(find_fw_dialog)"
      if [ -n "$STILL2" ]; then
        log "  dialog persists after Return — Escape as logged last resort"
        gssh "$GUEST_IP" "DISPLAY=$DISPLAY_NUM xdotool key Escape 2>/dev/null; true" 2>/dev/null || true
        ESCAPE_T0_EPOCH="$(date +%s.%N)"; ESCAPE_T0="$(date -u +%FT%TZ)"
        DISMISSAL_METHOD="escape-last-resort"
        CONTINUE_ACTION="$CONTINUE_ACTION + Escape-last-resort (mouse+Return ineffective)"
        sleep 3
      else
        DISMISSAL_METHOD="keyboard-return"
      fi
    else
      DISMISSAL_METHOD="mouse-continue"
    fi
  else
    DISMISSAL_METHOD="mouse-continue"
  fi
  dump_window_stack "post-continue"
else
  log "  no framework dialog in window list — Details/Continue steps skipped (logged)"
fi
echo "$CONTINUE_ACTION" > "$LEGDIR/continue-action.txt"
echo "$DISMISSAL_METHOD" > "$LEGDIR/dismissal-method.txt"
log "  dismissal method: $DISMISSAL_METHOD"

# ---------------------------------------------------------------- ISS-029 probe (D-ISS-029-5S)
# Routed N3b (plan addendum V4.3): capture at EXACTLY t0+5s post-dismissal -
# one timestamped frame plus an xdotool/wmctrl window-stack dump at the same
# instant (single guest command, so frame and stack share one display state).
# The diagnostic is the post-dismissal state, not the settled menu. t0 is the
# dismissal action recorded in DISMISSAL_METHOD (Continue click / keyboard
# Return / Escape last resort). DIAGNOSTIC RECORD ONLY: artifacts land in
# $WORK/iss029-probe-*, never in the evidence bundle (no bundle contact), and
# never substitute for bundle evidence. Fails loud, never silent.
ISS029_PROBE_DIR="${ISS029_PROBE_DIR:-$WORK/iss029-probe-$(date -u +%Y%m%dT%H%M%SZ)}"
mkdir -p "$ISS029_PROBE_DIR"
PROBE_REPORT="$ISS029_PROBE_DIR/iss029-probe-report.txt"
T0_EPOCH=""; T0_ISO=""
case "$DISMISSAL_METHOD" in
  mouse-continue)     T0_EPOCH="$CLICK_T0_EPOCH";  T0_ISO="$CLICK_T0" ;;
  keyboard-return)    T0_EPOCH="$RETURN_T0_EPOCH"; T0_ISO="$RETURN_T0" ;;
  escape-last-resort) T0_EPOCH="$ESCAPE_T0_EPOCH"; T0_ISO="$ESCAPE_T0" ;;
esac
{
  echo "iss029 probe (D-ISS-029-5S) - routed N3b - DIAGNOSTIC RECORD ONLY (not certification evidence)"
  echo "leg=$LEG dom=$DOM guest_ip=$GUEST_IP"
  echo "dismissal_method=$DISMISSAL_METHOD"
  echo "dismissal_t0=$T0_ISO epoch=$T0_EPOCH (t0 = the dismissal action)"
  echo "-- driver revision (md5) --"
  md5sum "$SCRIPT_DIR/run_matrix.sh" "$SCRIPT_DIR/capture_evidence.sh" "$SCRIPT_DIR/common.sh" 2>/dev/null
} > "$PROBE_REPORT"
if [ -n "$T0_EPOCH" ]; then
  TARGET_EPOCH="$(awk -v t0="$T0_EPOCH" 'BEGIN{printf "%.3f", t0+5.0}')"
  NOW_EPOCH="$(date +%s.%N)"
  REMAINING="$(awk -v n="$NOW_EPOCH" -v t="$TARGET_EPOCH" 'BEGIN{printf "%.3f", t-n}')"
  if awk -v r="$REMAINING" 'BEGIN{exit !(r>0)}'; then sleep "$REMAINING"; fi
  PROBE_TS="$(date -u +%FT%TZ)"
  PROBE_EPOCH="$(date +%s.%N)"
  PROBE_OFFSET="$(awk -v a="$PROBE_EPOCH" -v t="$TARGET_EPOCH" 'BEGIN{printf "%.3f", a-t}')"
  log "  ISS-029 probe: t0+5s capture @ $PROBE_TS (offset from target ${PROBE_OFFSET}s)"
  if gssh "$GUEST_IP" "export DISPLAY=$DISPLAY_NUM; \
      import -window root /tmp/iss029-t5.png 2>/dev/null || scrot /tmp/iss029-t5.png 2>/dev/null; \
      { echo \"== window-stack iss029-t5 @ $PROBE_TS ==\"; \
        for w in \$(xdotool search --onlyvisible --name '.' 2>/dev/null); do \
          n=\$(xdotool getwindowname \"\$w\" 2>/dev/null); \
          g=\$(xdotool getwindowgeometry \"\$w\" 2>/dev/null | tr '\n' ' '); \
          echo \"[$PROBE_TS] [\$w] \$n :: \$g\"; \
        done; \
        echo \"total-visible: \$(xdotool search --onlyvisible --name '.' 2>/dev/null | wc -l)\"; \
        echo \"== wmctrl -l ==\"; wmctrl -l 2>/dev/null; true; \
      } > /tmp/iss029-t5-stack.txt" 2>/dev/null; then
    gscp_pull "$GUEST_IP" "/tmp/iss029-t5.png" "$ISS029_PROBE_DIR/iss029-t5.png" 2>/dev/null \
      || echo "frame-pull=FAILED" >> "$PROBE_REPORT"
    gscp_pull "$GUEST_IP" "/tmp/iss029-t5-stack.txt" "$ISS029_PROBE_DIR/iss029-t5-stack.txt" 2>/dev/null \
      || echo "stack-pull=FAILED" >> "$PROBE_REPORT"
  else
    echo "probe_capture=FAILED (guest command nonzero)" >> "$PROBE_REPORT"
    log "  ISS-029 probe: capture FAILED (recorded in $PROBE_REPORT)"
  fi
  GUEST_BIN_HASHES="$(gssh "$GUEST_IP" "sha256sum '$GUEST_GAME_DIR/uninstall.exe.patched.exe' '$GUEST_GAME_DIR/uninstall.exe' 2>/dev/null" 2>/dev/null || echo '(hash unavailable)')"
  {
    echo "probe_ts=$PROBE_TS"
    echo "target_epoch=$TARGET_EPOCH (t0+5.000)"
    echo "actual_epoch=$PROBE_EPOCH offset_s=$PROBE_OFFSET"
    echo "-- guest subject hashes (post-revert; same golden snapshot the S4 run used) --"
    printf '%s\n' "$GUEST_BIN_HASHES"
    echo "-- probe artifact sha256 --"
    sha256sum "$ISS029_PROBE_DIR/iss029-t5.png" "$ISS029_PROBE_DIR/iss029-t5-stack.txt" 2>/dev/null
  } >> "$PROBE_REPORT"
else
  echo "probe=NOT-APPLICABLE (no dismissal action recorded; dismissal_method=$DISMISSAL_METHOD)" >> "$PROBE_REPORT"
  log "  ISS-029 probe: NOT-APPLICABLE (dismissal_method=$DISMISSAL_METHOD)"
fi
log "  ISS-029 probe report: $PROBE_REPORT"

# ---------------------------------------------------------------- B-enum widget tree
# Phase B (REV-031): runs AFTER the dismissal ladder so the menu is top.
# Dumps the X window tree of the menu window (+ root tree for desktop-overlay
# pets). Created-but-hidden vs never-created reads straight off the tree.
# Guards (REV-031 wedged-capture lesson): never call xwininfo with an empty
# id (it drops into interactive select mode and hangs the leg); all xwininfo
# calls wall-clock bounded with timeout.
MENU_WIN="$(gssh "$GUEST_IP" "export DISPLAY=$DISPLAY_NUM; \
  best=''; bestarea=0; \
  for w in \$(xdotool search --onlyvisible --name '.' 2>/dev/null); do \
    n=\$(xdotool getwindowname \"\$w\" 2>/dev/null); \
    case \"\$n\" in *[Mm]icrosoft*[Nn][Ee][Tt]*[Ff]ramework*|*nhandled*) continue ;; esac; \
    eval \"\$(xdotool getwindowgeometry --shell \"\$w\" 2>/dev/null)\"; \
    area=\$((WIDTH * HEIGHT)); \
    if [ \"\$area\" -gt \"\$bestarea\" ]; then bestarea=\"\$area\"; best=\"\$w\"; fi; \
  done; echo \"\$best\"" 2>/dev/null | tail -1)"
log "  enum target menu window: ${MENU_WIN:-(none)}"
{
  echo "== widget-tree menu-window ${MENU_WIN:-none} @ $(date -u +%FT%TZ) =="
  if [ -n "$MENU_WIN" ]; then
    gssh "$GUEST_IP" "DISPLAY=$DISPLAY_NUM timeout 15 xwininfo -id '$MENU_WIN' -tree 2>/dev/null || echo TREE-FAILED" 2>/dev/null
  else
    echo "ENUM-SKIPPED-no-menu-window (dialog still up or no UI; see window-stack.log)"
  fi
  echo ""
  echo "== widget-tree root @ $(date -u +%FT%TZ) =="
  gssh "$GUEST_IP" "DISPLAY=$DISPLAY_NUM timeout 15 xwininfo -root -tree 2>/dev/null | head -60 || echo ROOT-TREE-FAILED" 2>/dev/null
} > "$LEGDIR/widget-tree.log" 2>&1 || true
log "  widget-tree.log written ($(wc -l < "$LEGDIR/widget-tree.log" 2>/dev/null || echo ?) lines)"

# ---------------------------------------------------------------- Q3 trigger
# REV-036 §7: one click on the Start position + staged ~1s full-desktop
# captures. Start (per Windows reference) is leftmost of the button row, left
# of Help. If Start is absent the click lands harmlessly on menu art (logged).
# SoundPlayer/playback failures are evidence only, never leg-fatal.
TRIGGER_ACTION="not-attempted"
if [ -z "${MENU_WIN:-}" ]; then
  MENU_WIN="$(gssh "$GUEST_IP" "export DISPLAY=$DISPLAY_NUM; \
    best=''; bestarea=0; \
    for w in \$(xdotool search --onlyvisible --name '.' 2>/dev/null); do \
      n=\$(xdotool getwindowname \"\$w\" 2>/dev/null); \
      case \"\$n\" in *[Mm]icrosoft*[Nn][Ee][Tt]*[Ff]ramework*|*nhandled*) continue ;; esac; \
      eval \"\$(xdotool getwindowgeometry --shell \"\$w\" 2>/dev/null)\"; \
      area=\$((WIDTH * HEIGHT)); \
      if [ \"\$area\" -gt \"\$bestarea\" ]; then bestarea=\"\$area\"; best=\"\$w\"; fi; \
    done; echo \"\$best\"" 2>/dev/null | tail -1)"
fi
# Frame 0: pre-input button-row capture (branch discriminator, REV-037).
# menu.png + pet_t0/t1 above already serve as pre-trigger frames; this extra
# frame pins the exact pre-click state for the pixel-change verification.
FRAME0_TOOL="$(qga_shot trigger-frame0.png)"
log "  trigger-frame0.png tool=$FRAME0_TOOL (pre-input branch frame)"
if [ -n "$MENU_WIN" ]; then
  TRIGGER_TS="$(date -u +%FT%TZ)"
  CLICK_XY="$(gssh "$GUEST_IP" "export DISPLAY=$DISPLAY_NUM; \
    xdotool windowactivate --sync '$MENU_WIN' 2>/dev/null; sleep 1; \
    eval \"\$(xdotool getwindowgeometry --shell '$MENU_WIN' 2>/dev/null)\"; \
    echo \"menu X=\$X Y=\$Y WIDTH=\$WIDTH HEIGHT=\$HEIGHT\"; \
    CX=\$((X + WIDTH * 16 / 100)); CY=\$((Y + HEIGHT - 38)); \
    echo \"click \$CX \$CY\"; \
    xdotool mousemove \$CX \$CY sleep 1 click 1" 2>/dev/null | tee -a "$LEGDIR/window-stack.log" || true)"
  TRIGGER_ACTION="clicked-start-position-once@$TRIGGER_TS window=$MENU_WIN ($CLICK_XY; harmless if absent)"
  log "  Start trigger clicked once; verifying click took (pixel-change)"
  sleep 2
  VERIFY_TOOL="$(qga_shot trigger-verify.png)"
  CLICK_FRAC="$(frame_frac "$LEGDIR/trigger-frame0.png" "$LEGDIR/trigger-verify.png")"
  log "  click pixel-change fraction: $CLICK_FRAC (0 = missed/ineffective)"
  TRIGGER_PATH="mouse"
  if [ "$CLICK_FRAC" = "0.0" ] || [ "$CLICK_FRAC" = "0" ]; then
    log "  mouse ineffective (0-for-4 campaign) — keyboard fallback ladder (REV-037)"
    gssh "$GUEST_IP" "export DISPLAY=$DISPLAY_NUM; \
      xdotool windowactivate --sync '$MENU_WIN' 2>/dev/null; sleep 1" 2>/dev/null || true
    for t in 1 2 3 4; do
      gssh "$GUEST_IP" "DISPLAY=$DISPLAY_NUM xdotool key Tab 2>/dev/null; true" 2>/dev/null || true
      sleep 1
    done
    TAB_TOOL="$(qga_shot trigger-tab4.png)"
    log "  trigger-tab4.png tool=$TAB_TOOL (after 4x Tab)"
    gssh "$GUEST_IP" "DISPLAY=$DISPLAY_NUM xdotool key space 2>/dev/null; true" 2>/dev/null || true
    sleep 2
    KB_TOOL="$(qga_shot trigger-kb.png)"
    KB_FRAC="$(frame_frac "$LEGDIR/trigger-tab4.png" "$LEGDIR/trigger-kb.png")"
    log "  keyboard-activate pixel-change fraction: $KB_FRAC"
    TRIGGER_PATH="mouse-ineffective+keyboard-tab4-space"
    TRIGGER_ACTION="$TRIGGER_ACTION | keyboard fallback: Tabx4+Space@$TRIGGER_TS (frac=$KB_FRAC)"
  fi
  TRIGGER_ACTION="$TRIGGER_ACTION | path=$TRIGGER_PATH"
  log "  staged 5x ~1s full-desktop captures (path=$TRIGGER_PATH)"
  for i in 0 1 2 3 4; do
    STG_TOOL="$(qga_shot trigger-stage$i.png)"
    log "  trigger-stage$i.png tool=$STG_TOOL"
    sleep 1
  done
  sleep 2
  PTR0_TOOL="$(qga_shot pet_post_t0.png)"
  sleep 2
  PTR1_TOOL="$(qga_shot pet_post_t1.png)"
  log "  post-trigger pet pair: $PTR0_TOOL / $PTR1_TOOL"
  dump_window_stack "post-trigger"
else
  log "  no menu window for trigger — Start click skipped (logged)"
  TRIGGER_ACTION="skipped-no-menu-window"
fi
echo "$TRIGGER_ACTION" > "$LEGDIR/trigger-action.txt"

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

# GUEST-subject hash (evidence protocol integrity standard: as observed in the
# guest, never the host bundle slot - run_matrix never re-stages per leg)
GUEST_SUBJECT_SHA="$(gssh "$GUEST_IP" "sha256sum '$GUEST_GAME_DIR/uninstall.exe.patched.exe' 2>/dev/null | awk '{print \$1}'" 2>/dev/null | tr -dc '0-9a-f')"
[ -n "$GUEST_SUBJECT_SHA" ] || GUEST_SUBJECT_SHA="unavailable"
log "  guest-subject sha256 (observed in guest): $GUEST_SUBJECT_SHA"

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

continue_action = "(absent)"
try:
    continue_action = open(os.path.join(legdir, "continue-action.txt"), encoding="utf-8", errors="replace").read().strip()
except Exception:
    pass
post_continue_present = os.path.isfile(os.path.join(legdir, "post-continue.png"))
window_stack_present = os.path.isfile(os.path.join(legdir, "window-stack.log"))
def _read1(name):
    try:
        return open(os.path.join(legdir, name), encoding="utf-8", errors="replace").read().strip()
    except Exception:
        return "(absent)"
trigger_action = _read1("trigger-action.txt")
dismissal_method = _read1("dismissal-method.txt")
details_present = os.path.isfile(os.path.join(legdir, "details-expanded.png"))
widget_tree_present = os.path.isfile(os.path.join(legdir, "widget-tree.log"))

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
    "guest_subject_sha256": $(python3 -c "import json,sys; print(json.dumps(sys.argv[1]))" "$GUEST_SUBJECT_SHA"),
    "c0000135_scan": "$C0000135" == "true",
  },
  "retries_logged": retries,
  "continue_action": continue_action,
  "trigger_action": trigger_action,
  "dismissal_method": dismissal_method,
  "post_continue_png": post_continue_present,
  "details_expanded_png": details_present,
  "widget_tree_log": widget_tree_present,
  "window_stack_log": window_stack_present,
  "timestamps": {"pet_t0": "$PET_T0_TS", "pet_t1": "$PET_T1_TS"},
  "artifacts": art,
}
out = os.path.join(legdir, "capture.json")
json.dump(doc, open(out, "w"), indent=2)
print("capture.json written:", out)
PY

log "== D4 done: $LEG =="
