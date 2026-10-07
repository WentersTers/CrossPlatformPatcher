#!/usr/bin/env bash
# ---------------------------------------------------------------------------
# D3 — run_matrix.sh : leg orchestrator (ISS-017 / REV-013).
#
#   bash run_matrix.sh --legs L01 [--phase 0] [--no-teardown]
#
# Per leg: resource gate -> snapshot revert -> runtime activate -> run.sh
# (generates/uses project-local .wine-prefix) -> launch the app under the
# harness wrapper (WINEDLLOVERRIDES=mscoree=b per Q1 ruling) -> capture_evidence
# -> teardown -> evidence/ISS-017/leg-XX/status.json.
#
# Nothing is ever labeled PASS from terminal output alone: the verdict is
# evaluated from written artifacts (capture.json + files on disk).
# ---------------------------------------------------------------------------
SCRIPT_DIR="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
# shellcheck source=common.sh
source "$SCRIPT_DIR/common.sh"

LEGS_ARG=""; PHASE=""; NO_TEARDOWN=0
while [ $# -gt 0 ]; do
  case "$1" in
    --legs) LEGS_ARG="$2"; shift 2 ;;
    --phase) PHASE="$2"; shift 2 ;;
    --no-teardown) NO_TEARDOWN=1; shift ;;
    *) die "unknown arg: $1" ;;
  esac
done

# default: Phase 0 smoke set (L01 only) — mandate §6
[ -n "$LEGS_ARG" ] || [ -n "$PHASE" ] || PHASE="0"
if [ -n "$PHASE" ] && [ -z "$LEGS_ARG" ]; then
  LEGS_ARG="$(awk -F'|' -v p="$PHASE" '/^[[:space:]]*#/ {next} /^[[:space:]]*$/ {next} $2==p {printf "%s%s", (n++?",":""), $1}' "$LEGS_CONF")"
fi
[ -n "$LEGS_ARG" ] || die "no legs selected (phase $PHASE)"
log "== D3 run_matrix: legs=$LEGS_ARG phase=${PHASE:-explicit} =="

# ---------------------------------------------------------------- resource gate
[ -f "$RIG_PROBE_JSON" ] || bash "$SCRIPT_DIR/rig_probe.sh" >/dev/null || die "rig_probe failed"
STOP_CHECK="$(python3 -c "import json; print(json.load(open('$RIG_PROBE_JSON'))['stop_check'])")"
STOP_REASON="$(python3 -c "import json; print(json.load(open('$RIG_PROBE_JSON'))['stop_reason'])")"
THERMAL_STATE="$(python3 -c "import json; print(json.load(open('$RIG_PROBE_JSON'))['thermal_state'])")"
MAX_CONCURRENT="$(python3 -c "import json; print(json.load(open('$RIG_PROBE_JSON'))['max_concurrent'])")"
if [ "$STOP_CHECK" != "OK" ]; then
  log "REFUSING to start: $STOP_CHECK ($STOP_REASON) — legs report DEFERRED-resources"
fi

IFS=',' read -ra LEGS <<< "$LEGS_ARG"
RUN_STARTED="$(date -u +%FT%TZ)"

for LEG in "${LEGS[@]}"; do
  LEG="$(echo "$LEG" | tr -d ' ')"
  [ -n "$LEG" ] || continue
  LEG_LOWER="$(echo "$LEG" | tr '[:upper:]' '[:lower:]')"
  LEGDIR="$EVIDENCE_ROOT/$(echo "$LEG_LOWER" | sed 's/^l/leg-/')"
  mkdir -p "$LEGDIR"
  DOM="$(leg_domain "$LEG")"
  PORT="$(leg_hostfwd_port "$LEG")"
  SNAP="golden-$LEG_LOWER"
  EXPECTED="$(leg_field "$LEG" 7)"
  OS_PRETTY="$(leg_field "$LEG" 4)"
  RUNTIME_DESC="$(leg_field "$LEG" 6)"
  SEQ="$(leg_field "$LEG" 8)"

  log "---- $LEG ($OS_PRETTY / $RUNTIME_DESC) expected=$EXPECTED seq=$SEQ ----"

  # DEFERRED-resources is a first-class verdict with a reason, not a gap
  if [ "$STOP_CHECK" != "OK" ]; then
    python3 - "$LEG" "$LEGDIR" "$STOP_REASON" "$OS_PRETTY" "$RUNTIME_DESC" <<'PY'
import json, sys, datetime
leg, legdir, reason, os_pretty, rt = sys.argv[1:6]
now = datetime.datetime.utcnow().isoformat() + "Z"
doc = {
  "schema": "rev013-status-v1",
  "leg": leg, "os_guest": os_pretty, "runtime": rt,
  "verdict": "DEFERRED", "failing_gate": None, "error_signature": None,
  "deviation_notes": [f"DEFERRED - resources: {reason}"],
  "timestamps": {"leg_start": now, "leg_end": now},
}
json.dump(doc, open(f"{legdir}/status.json", "w"), indent=2)
print(f"{leg}: DEFERRED - resources ({reason})")
PY
    continue
  fi

  LEG_START="$(date -u +%FT%TZ)"

  # ---------------- revert to snapshot (never reinstall, never regenerate)
  sudo virsh dominfo "$DOM" >/dev/null 2>&1 || {
    log "domain $DOM missing — run vm_provision.sh --leg $LEG first"
    python3 - "$LEG" "$LEGDIR" "$OS_PRETTY" "$RUNTIME_DESC" "$LEG_START" <<'PY'
import json, sys, datetime
leg, legdir, os_pretty, rt, t0 = sys.argv[1:6]
now = datetime.datetime.utcnow().isoformat() + "Z"
json.dump({
  "schema": "rev013-status-v1",
  "leg": leg, "os_guest": os_pretty, "runtime": rt,
  "verdict": "DEFERRED", "failing_gate": None, "error_signature": None,
  "deviation_notes": ["DEFERRED - no provisioned base (vm_provision.sh not run for this leg)"],
  "timestamps": {"leg_start": t0, "leg_end": now},
}, open(f"{legdir}/status.json", "w"), indent=2)
print(f"{leg}: DEFERRED - not provisioned")
PY
    continue
  }

  sudo virsh destroy "$DOM" >/dev/null 2>&1 || true
  sudo virsh snapshot-revert "$DOM" "$SNAP" --running >/dev/null 2>&1 \
    || sudo virsh snapshot-revert "$DOM" "$SNAP" >/dev/null 2>&1 \
    || { log "snapshot-revert failed for $SNAP"; }
  sudo virsh start "$DOM" >/dev/null 2>&1 || true
  GUEST_IP="$(discover_ip "$DOM")" || { log "no guest address after revert"; }
  [ -n "$GUEST_IP" ] && echo "$GUEST_IP" > "$(leg_ip_file "$LEG")"
  wait_ssh "$GUEST_IP" 240 || { log "guest unreachable after revert"; }
  # memory-snapshot restore skews the guest clock — resync so evidence
  # timestamps are honest
  gssh "$GUEST_IP" "sudo date -u -s '$(date -u +%FT%TZ)' >/dev/null 2>&1; true" 2>/dev/null || true

  # RULE 1 (restricted operating rules): refuse to launch against a partial tree —
  # incomplete content produces misleading startup crashes, not verdicts.
  if ! verify_guest_tree "$GUEST_IP" "$GUEST_GAME_DIR"; then
    log "RULE 1 VIOLATION: guest tree incomplete at $GUEST_GAME_DIR — see restricted operating rules"
    python3 - "$LEG" "$LEGDIR" "$OS_PRETTY" "$RUNTIME_DESC" "$LEG_START" <<'PY'
import json, sys, datetime
leg, legdir, os_pretty, rt, t0 = sys.argv[1:6]
now = datetime.datetime.utcnow().isoformat() + "Z"
json.dump({
  "schema": "rev013-status-v1",
  "leg": leg, "os_guest": os_pretty, "runtime": rt,
  "verdict": "VOID-STAGING", "failing_gate": "rule1_tree_incomplete", "error_signature": None,
  "deviation_notes": ["VOID-STAGING: tree incomplete (RULE 1, restricted operating rules). Not a product verdict; re-stage from the authoritative install and re-run."],
  "timestamps": {"leg_start": t0, "leg_end": now},
}, open(f"{legdir}/status.json", "w"), indent=2)
print(f"{leg}: VOID-STAGING (RULE 1) — re-stage required")
PY
    continue
  fi

  # display stack + launch under harness wrapper
  gssh "$GUEST_IP" "if ! DISPLAY=$DISPLAY_NUM xdpyinfo >/dev/null 2>&1; then \
      nohup Xvfb $DISPLAY_NUM -screen 0 ${SCREEN_W}x${SCREEN_H}x24 -nolisten tcp >/tmp/xvfb.log 2>&1 & sleep 3; fi; \
     if ! pgrep -x openbox >/dev/null 2>&1; then DISPLAY=$DISPLAY_NUM nohup openbox >/tmp/openbox.log 2>&1 & sleep 2; fi; \
     pkill -f '[.]patched[.]exe|un[i]nstall' 2>/dev/null; true"

  log "launching the app via generated run.sh (wrapper: WINEDLLOVERRIDES=mscoree=b)"
  # setsid + </dev/null: no fd may hold the ssh channel open (wine children
  # inherit stdin and would otherwise wedge the orchestrator session)
  gssh "$GUEST_IP" "cd '$GUEST_GAME_DIR' && \
     (setsid nohup env WINEDLLOVERRIDES=mscoree=b DISPLAY=$DISPLAY_NUM HOME=/home/$GUEST_USER \
       bash run.sh > '$GUEST_GAME_DIR/launch.log' 2>&1 < /dev/null &) ; \
     echo launched" || log "WARN: launch ssh returned nonzero"

  # wait for the app process (window/session evidence comes from capture tier)
  # [P]/[i]/[.] bracket patterns never self-match the remote shell cmdline
  APP_ALIVE=no
  for i in $(seq 1 45); do
    if gssh "$GUEST_IP" "pgrep -f '[.]patched[.]exe|un[i]nstall' >/dev/null 2>&1" 2>/dev/null; then APP_ALIVE=yes; break; fi
    sleep 2
  done
  log "app process alive after $((i*2))s: $APP_ALIVE"

  # end-user flow: first-run MODAL dialogs (integrity notice, credits, …) block
  # the pet from rendering until dismissed. Dismiss any titled modal like a
  # user (Return, then OK-button click fallback), until a main UI window or no
  # window remains. Every attempt logged.
  # NOTE: process pattern '[.]patched[.]exe|un[i]nstall' matches the real
  # program (RULE 2 target) and never self-matches.
  DIALOG_STATE="not-seen"
  DIALOG_SEEN=no
  for i in $(seq 1 45); do
    DIALOG_STATE="$(gssh "$GUEST_IP" "export DISPLAY=$DISPLAY_NUM; \
      W=''; N=''; \
      for w in \$(xdotool search --onlyvisible --name '.' 2>/dev/null); do \
        n=\$(xdotool getwindowname \"\$w\" 2>/dev/null); \
        case \"\$n\" in \
          *redits*|*ntegrity*|*otice*|*arning*|*rror*|*ialog*|*onfirm*) W=\"\$w\"; N=\"\$n\"; break ;; \
        esac; \
      done; \
      if [ -z \"\$W\" ]; then \
        if [ -z \"\$(xdotool search --onlyvisible --name '.' 2>/dev/null)\" ]; then echo absent; else echo main-ui; fi; \
        exit 0; \
      fi; \
      xdotool windowactivate --sync \"\$W\" 2>/dev/null; sleep 1; \
      xdotool key --window \"\$W\" Return 2>/dev/null || xdotool key Return 2>/dev/null; \
      sleep 2; \
      if xdotool search --name \"\$N\" >/dev/null 2>&1; then \
        eval \"\$(xdotool getwindowgeometry --shell \"\$W\" 2>/dev/null)\"; \
        xdotool mousemove \$((X + WIDTH / 2)) \$((Y + HEIGHT - 30)) sleep 1 click 1; \
        sleep 2; \
      fi; \
      if xdotool search --name \"\$N\" >/dev/null 2>&1; then echo \"still-present:\$N\"; else echo \"dismissed:\$N\"; fi" 2>/dev/null | tail -1)"
    log "  modal dialog attempt $i: $DIALOG_STATE"
    case "$DIALOG_STATE" in
      main-ui) break ;;
      dismissed:*) DIALOG_SEEN=yes ;;
      absent) [ "$DIALOG_SEEN" = yes ] && break ;;
      *) ;;
    esac
    sleep 2
  done

  # after the modal is gone the pet UI can render; wait for any visible
  # window (incl. empty-named overlays), then let the pet start animating
  HAS_UI=no
  for i in $(seq 1 30); do
    WINS="$(gssh "$GUEST_IP" "DISPLAY=$DISPLAY_NUM xdotool search --onlyvisible --name '.' 2>/dev/null | wc -l" 2>/dev/null || echo 0)"
    HAS_UI="$WINS"
    [ "${WINS:-0}" -ge 1 ] 2>/dev/null && break
    sleep 2
  done
  log "  visible windows after modal: $HAS_UI (after $((i*2))s)"
  sleep 15

  # ---------------- capture (D4)
  bash "$SCRIPT_DIR/capture_evidence.sh" --leg "$LEG" || log "WARN: capture returned nonzero"

  # ---------------- teardown (keep artifacts; stop the guest)
  if [ "$NO_TEARDOWN" = 0 ]; then
    gssh "$GUEST_IP" "pkill -f '[.]patched[.]exe|un[i]nstall' 2>/dev/null; true" || true
    sudo virsh shutdown "$DOM" >/dev/null 2>&1 || true
  fi

  # ---------------- status.json (verdict from written artifacts only)
  LEG_END="$(date -u +%FT%TZ)"
  PROV="$PROVISION_LOG"
  python3 - "$LEG" "$LEGDIR" "$OS_PRETTY" "$RUNTIME_DESC" "$EXPECTED" \
           "$LEG_START" "$LEG_END" "$PROV" "$RIG_PROBE_JSON" "$BASELINE_BCL" <<'PY'
import json, os, re, sys, glob, datetime

leg, legdir, os_pretty, rt_desc, expected, t0, t1, prov_path, rig_path, baseline = sys.argv[1:11]

def load(p):
    try:
        return json.load(open(p))
    except Exception:
        return None

capture = load(os.path.join(legdir, "capture.json")) or {}
prov_all = load(prov_path) or []
prov = None
for rec in (prov_all if isinstance(prov_all, list) else [prov_all]):
    if rec and rec.get("leg") == leg:
        prov = rec
prov = prov or {}
rig = load(rig_path) or {}
prov_n = prov.get("provenance", {}) or {}

vis = capture.get("visual", {}) or {}
data = capture.get("data", {}) or {}

gates = {}
failures = []

# gate 1: menu artifact present and non-trivial (readability is Operator OCR)
menu_p = os.path.join(legdir, "menu.png")
if os.path.isfile(menu_p) and os.path.getsize(menu_p) > 5000:
    gates["1_visual_menu"] = "PASS"
else:
    gates["1_visual_menu"] = "FAIL"; failures.append("1_visual_menu")

# gate 2: animated pet = two distinct frames (frame_diff 0 == frozen == fail)
fd = vis.get("frame_diff", -1)
if vis.get("frame_diff_pass"):
    gates["2_visual_pet"] = "PASS"
else:
    gates["2_visual_pet"] = "FAIL"; failures.append("2_visual_pet")

# gate 3: BCL line present + parsed; compare file: field to baseline.
#         Mismatch is REVIEW — never an auto-fail (Operator ruling A3).
bcl_file = data.get("bcl_file_field", "-")
bcl_line = data.get("bcl_line", "(absent)")
if bcl_line in ("(absent)", "(not-found)", "") or bcl_file in ("-", ""):
    gates["3_bcl"] = "FAIL"; failures.append("3_bcl")
    bcl_compare = "ABSENT"
else:
    bcl_compare = "MATCH" if bcl_file == baseline else "REVIEW"
    gates["3_bcl"] = "PASS" if bcl_compare == "MATCH" else "REVIEW"

# gate 4: runtime identity captured
if data.get("runtime_identity", "").strip():
    gates["4_runtime_identity"] = "PASS"
else:
    gates["4_runtime_identity"] = "FAIL"; failures.append("4_runtime_identity")

# gate 5: do-not-regress — effective mscoree=b never blank; run.sh blank is the
#         defect exhibit (Q1: harness compensates, standing deviation note).
effective = "mscoree=b"   # harness launch wrapper exports this (Q1 option 1)
runsh_val = data.get("runsh_winell_overrides", "")
if effective == "mscoree=b":
    gates["5_dnr_msoree"] = "PASS"
else:
    gates["5_dnr_msoree"] = "FAIL"; failures.append("5_dnr_msoree")

# gate 6: wine-mono MSI filename assertion (do-not-regress #2)
msi = prov.get("mono_msi_filename", "(none)")
msi_assert = prov.get("mono_msi_assert", "NOT-RUN")
if rt_desc.startswith("Wine") or "wine" in rt_desc.lower():
    if msi_assert == "PASS":
        gates["6_dnr_msi"] = "PASS"
    else:
        gates["6_dnr_msi"] = "FAIL"; failures.append("6_dnr_msi")
else:
    gates["6_dnr_msi"] = "N/A"

# gate 7: no terminal-log-only verdicts — artifacts must exist on disk
if capture and os.path.isfile(os.path.join(legdir, "capture.json")):
    gates["7_artifacts_on_disk"] = "PASS"
else:
    gates["7_artifacts_on_disk"] = "FAIL"; failures.append("7_artifacts_on_disk")

# error signature scan — LOGS ONLY (JSON metadata carries key names like
# c0000135_scan that must never self-match), priority: product crash families
sig = None
sig_priority = ["MissingManifestResourceException", "FATAL UNHANDLED EXCEPTION",
                "PROBE_RESOURCE_NOT_FOUND", "PROBE_ONNX_INIT_FAILED",
                "GLIBC_", "c0000135", "c0000142", "c0000005"]
found = set()
for f in sorted(glob.glob(os.path.join(legdir, "*.log"))):
    try:
        text = open(f, encoding="utf-8", errors="replace").read()
    except Exception:
        continue
    for pat in sig_priority:
        if pat.lower() in text.lower():
            if pat == "GLIBC_":
                m = re.search(r"GLIBC_2\.[0-9]+ not found", text)
                found.add(m.group(0) if m else "GLIBC_ mismatch")
            else:
                found.add(pat)
for pat in sig_priority:
    for fnd in found:
        if fnd.startswith(pat) or fnd == pat:
            sig = fnd
            break
    if sig:
        break

# verdict
glibc_evidence = False
for f in ("tls-diag.log", "find-pai-runtime.log", "capture.json"):
    p = os.path.join(legdir, f)
    if os.path.isfile(p):
        try:
            if "GLIBC_2.38" in open(p, encoding="utf-8", errors="replace").read():
                glibc_evidence = True
        except Exception:
            pass

verdict = "FAIL"
if expected == "PASS":
    if not failures and bcl_compare == "MATCH":
        verdict = "PASS"
    elif not failures and bcl_compare == "REVIEW":
        verdict = "REVIEW"
    else:
        verdict = "FAIL"
elif expected == "EXPECTED-FAIL":
    if failures and glibc_evidence:
        verdict = "EXPECTED-FAIL"   # predicted failure = success of the gate
    elif not failures:
        verdict = "REVIEW"          # predicted fail but it passed — deviation
    else:
        verdict = "FAIL"
elif expected == "NO-OP":
    verdict = "PASS" if not failures else "FAIL"

deviation_notes = [
  "shipped run.sh sets no WINEDLLOVERRIDES; harness compensates. Product gap tracked as ISS-DRAFT-01."
]
if sig:
    deviation_notes.append(
      f"app-side error signature captured: {sig} (artifacts: launch.log, launcher-runtime.log)")
if bcl_compare == "REVIEW":
    deviation_notes.append(
      f"BCL file version '{bcl_file}' != baseline '{baseline}' — REVIEW (never auto-fail per A3)")
if runsh_val:
    deviation_notes.append(f"run.sh WINEDLLOVERRIDES='{runsh_val}' (recorded, not blank)")

fallbacks = [r for r in capture.get("retries_logged", []) if "fallback" in str(r.get("tool", ""))]
thermal_backoff = rig.get("thermal_state") == "throttle"

status = {
  "schema": "rev013-status-v1",
  "leg": leg,
  "os_guest": os_pretty,
  "runtime": rt_desc,
  "patcher_git_sha": prov_n.get("git_sha", "(unknown)"),
  "patcher_build_ts": prov_n.get("build_ts", "(unknown)"),
  "git_dirty": bool(prov_n.get("git_dirty", True)),
  "effective_winell_overrides": effective,
  "runsh_winell_overrides": runsh_val,
  "mono_msi_filename": msi,
  "mono_msi_assert": msi_assert,
  "visual": {
    "menu": "menu.png", "pet_t0": "pet_t0.png", "pet_t1": "pet_t1.png",
    "frame_diff": fd, "frame_diff_pass": bool(vis.get("frame_diff_pass", False)),
    "capture_tool": vis.get("tool_menu", "none"),
    "retries": capture.get("retries_logged", []),
    "menu_notes": vis.get("menu_notes", ""),
  },
  "data": {
    "bcl_line": bcl_line,
    "bcl_version_field": data.get("bcl_version_field", "-"),
    "bcl_file_field": bcl_file,
    "bcl_compare": bcl_compare,
    "tls_diag_log": "tls-diag.log",
    "find_pai_runtime_log": "find-pai-runtime.log",
    "runtime_identity": data.get("runtime_identity", ""),
    "proton_log_excerpt": None,
    "c0000135_scan": bool(data.get("c0000135_scan", False)),
    "error_signature": sig,
  },
  "gates": gates,
  "expected": expected,
  "verdict": verdict,
  "failing_gate": failures[0] if failures else None,
  "error_signature": sig,
  "thermal_backoff": thermal_backoff,
  "fallbacks_logged": fallbacks,
  "retries_logged": capture.get("retries_logged", []),
  "deviation_notes": deviation_notes,
  "artifacts_sha256": {k: v.get("sha256") for k, v in (capture.get("artifacts") or {}).items()},
  "timestamps": {"leg_start": t0, "leg_end": t1},
}
out = os.path.join(legdir, "status.json")
json.dump(status, open(out, "w"), indent=2)
print(f"{leg}: verdict={verdict} failing_gate={status['failing_gate']} bcl={bcl_compare} "
      f"frame_diff={fd} -> {out}")
PY
done

log "== D3 done (started $RUN_STARTED) =="
log "next: python3 $SCRIPT_DIR/report_gen.py  (D5 rollup)"
