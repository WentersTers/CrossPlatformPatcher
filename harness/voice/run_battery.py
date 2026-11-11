#!/usr/bin/env python3
"""Voice battery runner — see docs/VOICE_BATTERY.md for the protocol.

Plays the battery through a guest's virtual microphone and scores it from the
app log. It never judges from terminal silence: every verdict comes from log
markers read between clips.

Preconditions (checked before the first clip):
  * the guest has a `vmic` pulse source and a live feeder writing to it,
  * the app's stream is bound to that source,
  * the feeder consumes /tmp/vmic.play (the transport).

Usage:
  python3 run_battery.py --guest sage@192.168.122.10 --key ~/vms/vm_key \
      --voice-dir /home/sage/voice --log /home/sage/game/launcher-runtime.log \
      --out /home/sage/work/battery-$(date +%s)

Exit codes: 0 all scored sets pass, 3 precondition/transport failure,
4 a scored set failed.
"""
from __future__ import annotations

import argparse
import json
import os
import re
import subprocess
import sys
import time

POSITIVE = [
    "combo-Open_Disney_Plus.wav",
    "combo-Open_HBO.wav",
    "combo-Open_Hulu.wav",
    "combo-Open_Spotify.wav",
    "combo-Open_The_Browser.wav",
    "combo-Open_The_Points_Shop.wav",
    "combo-Open_Twitch.wav",
    "combo-Open_Twitter.wav",
    "combo-Open_YouTube.wav",
    "combo-Show_Hiking_Places_Near_Me.wav",
    "combo-Show_Me_A_Meme.wav",
    "combo-mumble.wav",
]

# No "flip a coin" command exists in the deployed manifest, so a dispatch here
# would be a misroute. Silence is the pass condition.
EXPECTED_SILENT = [
    "combo-Please_Flip_a_Coin.wav",
] + [f"Negative #{i} " for i in range(1, 11)]

WAKE_TAKES = [
    "Hey PAIcom.wav",
    "Hey PAIcom #2.wav",
    "Hey PAIcom #3.wav",
    "Hey PAIcom #4.wav",
    "Hey PAIcom #5.wav",
]

URL_HINTS = ("browser", "disney", "youtube", "twitch", "twitter", "spotify")

WAKE_RE = re.compile(r"Wake word detected! Confidence: ([0-9.]+)")
CMD_RE = re.compile(r"\[oww-command\] command='([^']*)'")
TEXT_RE = re.compile(r"\[vosk-speech\] Transcript: ([^\n]*)")
DISPATCH_RE = re.compile(r"\[oww-dispatch-final\] Result: ([A-Z]+)")


def ssh_base(args):
    return [
        "ssh", "-i", args.key, "-o", "BatchMode=yes",
        "-o", "StrictHostKeyChecking=no", "-o", "UserKnownHostsFile=/dev/null",
        "-o", "ConnectTimeout=8", args.guest,
    ]


def run(args, cmd: str) -> str:
    return subprocess.run(ssh_base(args) + [cmd], capture_output=True, text=True).stdout


def lines(args, log: str) -> int:
    out = run(args, f'grep -c "" {log}')
    digits = "".join(ch for ch in out if ch.isdigit())
    return int(digits or 0)


def windows(args) -> str:
    return run(
        args,
        'DISPLAY=${DISPLAY_NUM:-:77} xdotool search --onlyvisible --name . 2>/dev/null | '
        'while read -r w; do DISPLAY=${DISPLAY_NUM:-:77} xdotool getwindowname "$w" 2>/dev/null; done | '
        "sort -u | tr '\\n' '|'",
    ).strip()


def plumbing(args):
    src = run(args, "export XDG_RUNTIME_DIR=/run/user/$(id -u); pactl list short sources")
    out = run(args, "export XDG_RUNTIME_DIR=/run/user/$(id -u); pactl list short source-outputs")
    expected = args.sink + ".monitor" if args.transport == "paplay" else "vmic"
    return (expected in src, out.strip())


def find_negative(args, prefix: str, voice_dir: str) -> str | None:
    out = run(args, f"ls -1 {voice_dir} | grep -F '{prefix}' | head -1").strip()
    return out or None


def play(args, path: str, wait: float, log: str):
    before = lines(args, log)
    if args.transport == "paplay":
        # Play into the null sink; its monitor is the capture source. No FIFO,
        # so a pipe-source reload cannot strand the feeder mid-battery.
        run(args, f"paplay --device={args.sink} '{path}' 2>/dev/null")
    else:
        run(args, f"printf '%s' '{path}' > /tmp/vmic.play")
    time.sleep(wait)
    after = lines(args, log)
    new = run(args, f"tail -n +{before + 1} {log} 2>/dev/null")
    return {
        "play": after - before,
        "confs": WAKE_RE.findall(new),
        "cmds": CMD_RE.findall(new),
        "texts": TEXT_RE.findall(new),
        "dispatch": DISPATCH_RE.findall(new),
    }


def main() -> int:
    ap = argparse.ArgumentParser()
    ap.add_argument("--guest", required=True, help="user@host of the guest")
    ap.add_argument("--key", required=True, help="ssh private key")
    ap.add_argument("--voice-dir", default="/home/sage/voice")
    ap.add_argument("--log", default="/home/sage/game/launcher-runtime.log")
    ap.add_argument("--out", required=True, help="directory for the jsonl + summary")
    ap.add_argument("--wait-positive", type=float, default=15.0)
    ap.add_argument("--wait-silent", type=float, default=10.0)
    ap.add_argument("--wait-wake", type=float, default=10.0)
    ap.add_argument("--transport", choices=("fifo", "paplay"), default="fifo",
                    help="fifo: feeder writes /tmp/vmic.play; paplay: play into a null sink")
    ap.add_argument("--sink", default="vmicsink",
                    help="null sink name for --transport paplay (source is <sink>.monitor)")
    args = ap.parse_args()

    os.makedirs(args.out, exist_ok=True)
    jsonl = os.path.join(args.out, "battery.jsonl")
    open(jsonl, "w").close()

    ok, outputs = plumbing(args)
    ready = "READY" if args.transport == "paplay" else run(
        args, "cat /tmp/vmic.done 2>/dev/null").strip()
    if not ok or not outputs or ready != "READY":
        print(f"PRECONDITION FAIL: source={ok} outputs={outputs!r} transport={ready!r}")
        return 3

    rows = []

    def record(kind, label, path, wait, url=False):
        before = windows(args)
        r = play(args, path, wait, args.log)
        new_windows = []
        if url:
            for _ in range(8):
                time.sleep(2)
                now = windows(args)
                new_windows = [x for x in now.split("|") if x and x not in before.split("|")]
                if new_windows:
                    break
        row = dict(idx=len(rows) + 1, kind=kind, label=label, **r,
                   wins_before=before, wins_after=windows(args), new_windows=new_windows)
        rows.append(row)
        with open(jsonl, "a", encoding="utf-8") as fh:
            fh.write(json.dumps(row) + "\n")
        print(f"{row['idx']:02d} {kind:14s} {label[:38]:38s} wake={len(r['confs'])} "
              f"cmd={r['cmds'][:1]} disp={r['dispatch'][:1]}", flush=True)
        return row

    for name in WAKE_TAKES:
        record("wake", name, f"{args.voice_dir}/{name}", args.wait_wake)

    for name in POSITIVE:
        record("positive", name, f"{args.voice_dir}/combo/{name}", args.wait_positive,
               url=any(h in name.lower() for h in URL_HINTS))

    record("expected-silent", "combo-Please_Flip_a_Coin.wav",
           f"{args.voice_dir}/combo/combo-Please_Flip_a_Coin.wav", args.wait_silent)
    for i in range(1, 11):
        prefix = f"Negative #{i} "
        path = find_negative(args, prefix, args.voice_dir)
        if path:
            record("expected-silent", os.path.basename(path), f"{args.voice_dir}/{path}",
                   args.wait_silent)

    positives = [r for r in rows if r["kind"] == "positive"]
    positives_ok = [r for r in positives if r["dispatch"] and r["dispatch"][0] == "SUCCESS"]
    silent = [r for r in rows if r["kind"] == "expected-silent"]
    silent_ok = [r for r in silent if not r["cmds"] and not r["dispatch"]]
    wakes = [r for r in rows if r["kind"] == "wake"]
    wake_hits = [r for r in wakes if r["confs"]]
    urls = [r for r in positives if r["new_windows"]]

    summary = {
        "positive": f"{len(positives_ok)}/{len(positives)} dispatched",
        "expected_silent": f"{len(silent_ok)}/{len(silent)} silent",
        "wake_alone": f"{len(wake_hits)}/{len(wakes)} woke",
        "url_handoff": f"{len(urls)} window(s) opened",
        "pass": len(positives_ok) == len(positives) and len(silent_ok) == len(silent),
    }
    with open(os.path.join(args.out, "summary.json"), "w", encoding="utf-8") as fh:
        json.dump(summary, fh, indent=2)

    print("\n== summary ==")
    for k, v in summary.items():
        print(f"{k}: {v}")
    return 0 if summary["pass"] else 4


if __name__ == "__main__":
    sys.exit(main())
