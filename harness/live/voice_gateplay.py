"""Guest-side end-of-response gate payload.

Delivered to /tmp/gateplay.py and run under the guest python3; kept
in-repo so the instrument is versioned with its rules (voice_gate.py).
GATEPLAY_SOURCE is byte-identical in behavior to the build exercised
live (mini-smoke: 5 no-onset + 1 baseline-held closes, every raw
cross-checked against its status before any verdict).

Protocol (harness side):
  1. start:  python3 /tmp/gateplay.py ResponseBus <tag> [onset [hold [max]]]
     (begins recording immediately; budgets from gate_config_for)
  2. inject, then: touch /tmp/go-<tag>
  3. poll /tmp/gate-<tag>.json until present (status: closed_why, onset_s,
     end_s, hot_regions); the gate kills its own parec on close.
  4. check_gate_report(status, raw stat) BEFORE any verdict from it.
Close reasons: no-trigger | no-onset (expected-silence path: silent
draws close the window instead of hanging it) | baseline-held | max-total.
"""
GATEPLAY_SOURCE = '''\
import audioop
import json
import os
import subprocess
import sys
import time

BUS, TAG = sys.argv[1], sys.argv[2]
ONSET_BUDGET = float(sys.argv[3]) if len(sys.argv) > 3 else 40.0
HOLD = float(sys.argv[4]) if len(sys.argv) > 4 else 5.0
MAX_TOTAL = float(sys.argv[5]) if len(sys.argv) > 5 else 90.0
RAW = "/tmp/rb-%s.raw" % TAG
TRIG = "/tmp/go-%s" % TAG
STAT = "/tmp/gate-%s.json" % TAG
THR = 0.05
POLL = 0.5


def peak_last(path, secs=1.0):
    try:
        sz = os.path.getsize(path)
    except OSError:
        return 0.0
    n = int(secs * 32000)
    with open(path, "rb") as f:
        f.seek(max(0, sz - n))
        d = f.read()
    if len(d) < 2:
        return 0.0
    d = d[:len(d) // 2 * 2]
    return audioop.max(d, 2) / 32768.0


def done(why, onset=None, end=None, hot=()):
    json.dump({"tag": TAG, "closed_why": why, "onset_s": onset, "end_s": end,
               "hot_regions": hot},
              open(STAT, "w"))
    sys.exit(0)


rec = subprocess.Popen(["parec", "--device=%s.monitor" % BUS, "--rate=16000",
                        "--channels=1", "--format=s16le", RAW])
t0 = time.monotonic()
try:
    while time.monotonic() - t0 < 120:
        if os.path.exists(TRIG):
            break
        time.sleep(POLL)
    else:
        done("no-trigger")
    tg = time.monotonic()
    onset = None
    hot, cur = [], None
    quiet_since = None
    while time.monotonic() - t0 < MAX_TOTAL:
        p = peak_last(RAW, 0.5)
        now = time.monotonic() - tg
        if p > THR:
            if onset is None:
                onset = round(now, 2)
            if cur is None:
                cur = [round(now, 2), round(now, 2)]
            else:
                cur[1] = round(now, 2)
            quiet_since = None
        else:
            if cur is not None:
                if quiet_since is None:
                    quiet_since = time.monotonic()
                    hot.append(cur)
                    cur = None
        if onset is None and now > ONSET_BUDGET:
            done("no-onset", onset=None, end=round(now, 2), hot=[])
        if onset is not None and quiet_since is not None and \\
                time.monotonic() - quiet_since >= HOLD:
            done("baseline-held", onset=onset,
                 end=round(time.monotonic() - tg, 2), hot=hot)
        time.sleep(POLL)
    if cur is not None:
        hot.append(cur)
    done("max-total", onset=onset, end=round(time.monotonic() - tg, 2), hot=hot)
finally:
    rec.terminate()
'''
