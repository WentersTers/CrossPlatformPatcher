# Voice battery — protocol and scoring

The voice battery is what "voice certified" means in this repository. It is
run against a patched bundle on a live runtime, after the audio path has been
proven to deliver audio to the app (see "Preconditions").

## Sets

### A. Positive combos (12)

Each file is one utterance containing the wake phrase followed by a command
that exists in the deployed `custom-commands/commands.txt`. The correct
outcome is a wake plus a dispatch of that command with a successful result.

```
combo-Open_Disney_Plus.wav
combo-Open_HBO.wav
combo-Open_Hulu.wav
combo-Open_Spotify.wav
combo-Open_The_Browser.wav
combo-Open_The_Points_Shop.wav
combo-Open_Twitch.wav
combo-Open_Twitter.wav
combo-Open_YouTube.wav
combo-Show_Hiking_Places_Near_Me.wav
combo-Show_Me_A_Meme.wav
combo-mumble.wav
```

### B. Expected-silent (11)

Ten speech negatives plus one command that **does not exist in the manifest**.
For all eleven, the correct outcome is no matched command and no dispatch.
A wake may or may not fire; acting on one is the failure.

```
Negative #1 .. #10 (room noise / ordinary speech, no wake phrase)
combo-Please_Flip_a_Coin.wav   <- no "flip a coin" command exists
```

`combo-Please_Flip_a_Coin` is deliberately *not* a positive case: there is
nothing to dispatch to. Signal that it belongs here: the manifest has no coin
command, so a dispatch is by definition a misroute. It used to route to
"why are you calling me a researcher" and now matches nothing — silence is the
pass condition.

### C. Wake alone (reported, not scored)

Five takes of "Hey PAIcom" plus one take repeated three times with silence
between. This measures wake sensitivity on short utterances and is **reported
as data**, not pass/fail: the take length sits near the feature-window edge on
some runtimes, and a single missed take is not a defect on its own. A wake
ratio of zero across all takes while combos work points at clip length.

### D. URL handoff (reported, not scored)

For commands that open a site, a new browser window or tab is the evidence
that the dispatch reached the desktop. Page load success is environment
dependent and is not scored.

## Scoring

| set | pass condition |
|---|---|
| A. positive combos | 12 of 12 wake, command matched, dispatch SUCCESS |
| B. expected-silent | 11 of 11 no command and no dispatch |
| C. wake alone | reported (wake count per take) |
| D. URL handoff | reported (new window or tab per URL command) |

A runtime is **voice certified** when A and B pass and C/D are reported.

## Running it

`harness/voice/run_battery.py` implements this protocol. It checks the
preconditions, plays each set through the guest's virtual microphone, reads the
app log between clips, and writes `battery.jsonl` plus `summary.json`:

```sh
python3 harness/voice/run_battery.py \
    --guest user@guest-ip --key ~/vms/vm_key \
    --voice-dir /home/user/voice \
    --log /home/user/game/launcher-runtime.log \
    --out ~/work/battery-$(date +%s)
```

Exit status is 0 only when set A and set B both pass.

### Transports

`--transport fifo` (default) writes each clip to the feeder's `/tmp/vmic.play`.
`--transport paplay --sink NAME` plays the clip into a null sink and uses
`NAME.monitor` as the capture source, which survives a pipe-source reload but
was seen crashing the Wine app on Ubuntu 24.04 / Wine 11.21; prefer the FIFO
there.

## Preconditions

- Arrival is proven before judging anything: a tone or a voice clip must show a
  non-zero peak on the capture device, and the app's own stream must be bound
  to that device.
- If the capture module restarts, the app silently rebinds to the null monitor
  and every clip looks like a program failure. Re-check the source list, the
  source outputs and the capture counter before re-running.
- Each clip is played through the virtual microphone and the app log is read
  between clips; verdicts come from the log, not from the absence of sound.
