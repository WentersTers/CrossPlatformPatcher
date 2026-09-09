"""Guest-side end-of-response gate payload.

Delivered to /tmp/gateplay.py and run under the guest python3; kept
in-repo so the instrument is versioned with its rules (voice_gate.py).
GATEPLAY_SOURCE tracks the exercised build; behavior changes are
re-exercised before formal use (mini-smoke, block runs).

Protocol (harness side):
  1. start:  python3 /tmp/gateplay.py ResponseBus <tag> [onset [hold [max]]]
     (begins recording immediately; budgets from gate_config_for)
  2. wait for /tmp/flow-<tag> (capture flowing; fail loud + retry on timeout)
  3. inject, then: touch /tmp/go-<tag>
  4. poll /tmp/gate-<tag>.json until present, then check_gate_report
     against the raw before any verdict from it.
Close reasons: no-flow | no-trigger | no-onset (expected-silence path:
silent draws close the window instead of hanging it) | baseline-held |
max-total. Statuses also carry trigger_offset_s, pre_hot (pre-trigger
peaks the onset budget cannot see: fast dispatch-during-injection
responses and device-open pops), and post_peak (peak over the
post-trigger portion only: the silence claim's actual subject).
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
FLOW = "/tmp/flow-%s" % TAG
THR = 0.05
POLL = 0.5

_t0 = None
_off = 0.0
_pre = []


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


def peak_from(path, start_byte):
    try:
        d = open(path, "rb").read()[start_byte:]
    except OSError:
        return 0.0
    if len(d) < 2:
        return 0.0
    m = 0.0
    for o in range(0, len(d) - 1600, 1600):
        m = max(m, audioop.max(d[o:o + 1600], 2) / 32768.0)
    return round(m, 4)


def done(rec, why, onset=None, end=None, hot=()):
    try:
        post = peak_from(RAW, int(_off * 32000))
    except Exception:
        post = -1.0
    try:
        rec.terminate()
    except Exception:
        pass
    try:
        rec.wait(timeout=3)
    except Exception:
        try:
            rec.kill()
        except Exception:
            pass
    json.dump({"tag": TAG, "closed_why": why, "onset_s": onset, "end_s": end,
               "hot_regions": hot, "trigger_offset_s": round(_off, 2),
               "pre_hot": _pre, "post_peak": post},
              open(STAT, "w"))
    sys.exit(0)


rec = subprocess.Popen(["parec", "--device=%s.monitor" % BUS, "--rate=16000",
                        "--channels=1", "--format=s16le", RAW])
t0 = time.monotonic()
_t0 = t0
try:
    ft0 = time.monotonic()
    while time.monotonic() - ft0 < 10.0:
        try:
            if os.path.getsize(RAW) > 64000:
                break
        except OSError:
            pass
        time.sleep(POLL)
    else:
        done(rec, "no-flow")
    open(FLOW, "w").write("flow")
    while time.monotonic() - t0 < 120:
        if os.path.exists(TRIG):
            break
        p = peak_last(RAW, 0.5)
        now0 = time.monotonic() - t0
        if p > THR:
            if _pre and now0 - _pre[-1][1] <= 1.0:
                _pre[-1][1] = round(now0, 2)
            else:
                _pre.append([round(now0, 2), round(now0, 2)])
        time.sleep(POLL)
    else:
        done(rec, "no-trigger")
    tg = time.monotonic()
    _off = tg - t0
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
            done(rec, "no-onset", onset=None, end=round(now, 2), hot=[])
        if onset is not None and quiet_since is not None and \\
                time.monotonic() - quiet_since >= HOLD:
            done(rec, "baseline-held", onset=onset,
                 end=round(time.monotonic() - tg, 2), hot=hot)
        time.sleep(POLL)
    if cur is not None:
        hot.append(cur)
    done(rec, "max-total", onset=onset, end=round(time.monotonic() - tg, 2),
         hot=hot)
finally:
    try:
        rec.terminate()
    except Exception:
        pass
'''
