#!/usr/bin/env python3
"""
D5 — report_gen.py : assemble evidence/ISS-017/leg-XX/ into the final matrix bundle.

Per test leg one row (REV-013 §7):
  Leg ID | OS | Runtime | Visual evidence (menu + pet, file refs) |
  BCL line vs 4.6.57.0 | find-pai-runtime.sh summary | Verdict | Notes/deviations

Plus: aggregate PASS/FAIL counts, deviations table, ISS draft index, and a
bundle index with sha256 per artifact. Outputs under evidence/ISS-017/report/.

  python3 report_gen.py [--evidence-root DIR]
"""
import argparse
import glob
import hashlib
import json
import os
import re
import sys

BASELINE_BCL = os.environ.get("BASELINE_BCL", "4.6.57.0")
LEG_ORDER = [f"L{i:02d}" for i in range(1, 13)]


def load(path):
    try:
        with open(path, encoding="utf-8") as f:
            return json.load(f)
    except Exception:
        return None


def parse_legs_conf(conf_path):
    rows = {}
    if not os.path.isfile(conf_path):
        return rows
    with open(conf_path, encoding="utf-8") as f:
        for line in f:
            line = line.strip()
            if not line or line.startswith("#"):
                continue
            parts = line.split("|")
            if len(parts) >= 9:
                rows[parts[0]] = {
                    "phase": parts[1], "os_id": parts[2], "os_pretty": parts[3],
                    "runtime_id": parts[4], "runtime_desc": parts[5],
                    "expected": parts[6], "sequential": parts[7], "notes": parts[8],
                }
    return rows


def find_pai_summary(legdir):
    """One-line digest of find-pai-runtime.sh: usable/empty prefix counts."""
    path = os.path.join(legdir, "find-pai-runtime.log")
    if not os.path.isfile(path):
        return "(absent)"
    try:
        text = open(path, encoding="utf-8", errors="replace").read()
    except Exception:
        return "(unreadable)"
    usable = len(re.findall(r"^\s*USABLE\s", text, re.M))
    empty = len(re.findall(r"^\s*empty\s", text, re.M))
    runtimes = []
    for name in ("wine", "wine64", "proton", "steam", "flatpak", "winetricks"):
        if re.search(rf"{name}\s+/", text):
            runtimes.append(name)
    return f"prefixes usable={usable} empty={empty}; runtimes-on-path=[{','.join(runtimes) or '-'}]"


def sha256_of(path):
    try:
        h = hashlib.sha256()
        with open(path, "rb") as f:
            for chunk in iter(lambda: f.read(1 << 20), b""):
                h.update(chunk)
        return h.hexdigest()
    except Exception:
        return "-"


def main():
    ap = argparse.ArgumentParser()
    default_root = os.path.join(os.path.expanduser("~"), "rev013", "evidence", "ISS-017")
    ap.add_argument("--evidence-root", default=os.environ.get("EVIDENCE_ROOT", default_root))
    ap.add_argument("--legs-conf", default=None)
    args = ap.parse_args()

    root = args.evidence_root
    conf_path = args.legs_conf or os.path.join(
        os.path.dirname(os.path.abspath(__file__)), "legs.conf")
    conf = parse_legs_conf(conf_path)
    report_dir = os.path.join(root, "report")
    os.makedirs(report_dir, exist_ok=True)

    leg_dirs = sorted(glob.glob(os.path.join(root, "leg-*")))
    rows, bundle_index, deviations, counts = [], [], [], {}
    seen = set()

    for legdir in leg_dirs:
        if not os.path.isdir(legdir):
            continue
        status = load(os.path.join(legdir, "status.json"))
        if not status:
            continue
        leg = status.get("leg", os.path.basename(legdir))
        seen.add(leg)
        c = conf.get(leg, {})
        v = status.get("visual", {})
        d = status.get("data", {})
        visual = (f"menu.png + pet_t0.png/pet_t1.png "
                  f"(frame_diff={v.get('frame_diff', '?')}"
                  f"{'' if v.get('frame_diff_pass') else ', FROZEN'}; "
                  f"tool={v.get('capture_tool', '?')})")
        bcl = (f"{d.get('bcl_line', '(absent)')} | {d.get('bcl_compare', '?')} "
               f"vs {BASELINE_BCL}")
        fp = find_pai_summary(legdir)
        verdict = status.get("verdict", "?")
        counts[verdict] = counts.get(verdict, 0) + 1
        notes = "; ".join(status.get("deviation_notes", [])) or "-"
        rows.append({
            "leg": leg,
            "os": status.get("os_guest", c.get("os_pretty", "?")),
            "runtime": status.get("runtime", c.get("runtime_desc", "?")),
            "visual": visual,
            "bcl": bcl,
            "find_pai": fp,
            "verdict": verdict,
            "notes": notes,
            "expected": status.get("expected", c.get("expected", "?")),
            "failing_gate": status.get("failing_gate"),
        })
        for note in status.get("deviation_notes", []):
            deviations.append(f"| {leg} | {note} |")
        for name, sha in (status.get("artifacts_sha256") or {}).items():
            bundle_index.append(f"{legdir}/{name}  {sha}")
        for name in sorted(os.listdir(legdir)):
            p = os.path.join(legdir, name)
            if os.path.isfile(p) and f"{legdir}/{name}" not in " ".join(bundle_index):
                bundle_index.append(f"{p}  {sha256_of(p)}")

        # per-leg verdict line (§7 column format)
        line = (f"{leg} | {rows[-1]['os']} | {rows[-1]['runtime']} | {visual} | {bcl} | "
                f"{fp} | {verdict} | {notes}")
        with open(os.path.join(report_dir, f"{leg}-verdict.txt"), "w", encoding="utf-8") as f:
            f.write(line + "\n")

    # ------------------------------------------------------------ matrix report
    def sort_key(r):
        return LEG_ORDER.index(r["leg"]) if r["leg"] in LEG_ORDER else 99

    rows.sort(key=sort_key)
    lines = ["# ISS-017 / REV-013 matrix report", "",
             f"Baseline BCL: `{BASELINE_BCL}` (wine-mono mscorlib FileVersion). "
             "BCL mismatch is REVIEW, never auto-fail.", ""]
    lines.append("| Leg ID | OS | Runtime | Visual evidence | BCL line vs baseline | "
                 "find-pai-runtime.sh | Verdict | Notes/deviations |")
    lines.append("|---|---|---|---|---|---|---|---|")
    for r in rows:
        lines.append(f"| {r['leg']} | {r['os']} | {r['runtime']} | {r['visual']} | "
                     f"{r['bcl']} | {r['find_pai']} | **{r['verdict']}** | {r['notes']} |")
    lines.append("")
    lines.append("## Aggregate")
    total = sum(counts.values())
    lines.append(f"- legs adjudicated: {total}/12")
    for k in sorted(counts):
        lines.append(f"- {k}: {counts[k]}")
    lines.append("")
    lines.append("## Deviations")
    if deviations:
        lines.append("| Leg | Deviation |")
        lines.append("|---|---|")
        lines.extend(deviations)
    else:
        lines.append("(none)")
    lines.append("")
    lines.append("## ISS drafts opened from findings")
    lines.append("- ISS-DRAFT-01 (patch lane): shipped/generated run.sh sets no WINEDLLOVERRIDES; "
                 "add `WINEDLLOVERRIDES=mscoree=b` to the generated launcher. Do NOT copy the "
                 "`mscoree,mshtml=` disable-both trick from LauncherGenerator.cs:1003,1015 into the launch path.")
    lines.append("- ISS-DRAFT-02 (patch lane / ISS-013 doc pass): mscoree.dll shadowing family "
                 "(Microsoft half-install shadows wine-mono) — add a find-pai-runtime.sh check + guide note.")
    lines.append("")
    lines.append("## Not yet adjudicated")
    pending = [l for l in LEG_ORDER if l not in seen]
    lines.append(", ".join(pending) if pending else "(none)")
    lines.append("")

    report_path = os.path.join(report_dir, "matrix-report.md")
    with open(report_path, "w", encoding="utf-8") as f:
        f.write("\n".join(lines))

    with open(os.path.join(report_dir, "bundle-index.txt"), "w", encoding="utf-8") as f:
        f.write("\n".join(sorted(bundle_index)) + "\n")

    print(f"report written: {report_path}")
    print(f"bundle index:   {os.path.join(report_dir, 'bundle-index.txt')}")
    for r in rows:
        vfile = os.path.join(report_dir, f"{r['leg']}-verdict.txt")
        print(f"verdict line:   {vfile}")
    return 0


if __name__ == "__main__":
    sys.exit(main())
