# Test Runner Guide - CrossPlatformPatcher

## Quick Start

### Option 1: Test ALL 99 Commands Automatically
```bash
./quick-test-all.sh
```
This will:
- Build the patcher
- Launch PAIcom.exe
- Inject all 99 commands with 1 second intervals
- Show results in real-time
- Press Ctrl+C to stop

### Option 2: Interactive Mode (Manual Command Entry)
```bash
./interactive-test.sh
```
This will:
- Launch PAIcom.exe
- Let you type commands manually
- Best for testing specific commands or debugging

### Option 3: Test Specific Commands
```bash
./test-all-commands.sh --command "hey paicom open the browser" "hey paicom play some music" --interval 2000
```

### Option 4: Test with Time Limit
```bash
./quick-test-all.sh
# Or with duration limit:
./test-all-commands.sh --duration 60  # Tests for 60 seconds then stops
```

---

## Test Modes Explained

### Auto Mode (Default)
Commands are injected automatically at fixed intervals.

**Usage:**
```bash
# Test all commands with 1s interval
./quick-test-all.sh

# Test all commands with 2s interval
./test-all-commands.sh --interval 2000

# Test all commands for 30 seconds
./test-all-commands.sh --duration 30
```

### Interactive Mode
You type commands manually at the prompt.

**Usage:**
```bash
./interactive-test.sh
```

**Example Session:**
```
>> hey paicom open the browser
[TEST]   -> Dispatch: QUEUED

>> hey paicom play some music
[TEST]   -> Dispatch: QUEUED

>> help
[Shows available commands]

>> exit
[Test completed]
```

---

## Testing by Category

### Test Web Browsers (28 commands)
```bash
./test-all-commands.sh --command \
  "hey paicom open the browser" \
  "hey paicom open redit" \
  "hey paicom open twitch" \
  "hey paicom open twiter" \
  "hey paicom open the viarchat website" \
  "hey paicom open spotify" \
  "hey paicom open youtube" \
  "hey paicom open discord" \
  "hey paicom open gmail" \
  "hey paicom open roblox" \
  --interval 1500
```

### Test Music Controls (6 commands)
```bash
./test-all-commands.sh --command \
  "hey paicom play some music" \
  "hey paicom play relaxing music" \
  "hey paicom pause the music" \
  "hey paicom resume the music" \
  "hey paicom play the next song" \
  "hey paicom play the previous song" \
  --interval 1500
```

### Test Steam Integration (7 commands)
```bash
./test-all-commands.sh --command \
  "hey paicom hide my online status on steam" \
  "hey paicom put my steam status online" \
  "hey paicom open the steam chat" \
  "hey paicom open my steam library" \
  "hey paicom show my steam friends" \
  "hey paicom start the steam vr mode" \
  --interval 1500
```

### Test Volume Controls (2 commands)
```bash
./test-all-commands.sh --command \
  "hey paicom volume up" \
  "hey paicom volume down" \
  --interval 2000
```

### Test Location Search (19 commands)
```bash
./test-all-commands.sh --command \
  "hey paicom show restaurants near me" \
  "hey paicom show cafes near me" \
  "hey paicom show pizza places near me" \
  "hey paicom show bars near me" \
  "hey paicom show cool places near me" \
  "hey paicom show museums near me" \
  "hey paicom show parks near me" \
  "hey paicom show cinemas near me" \
  "hey paicom show malls near me" \
  --interval 1500
```

### Test System Utilities (6 commands)
```bash
./test-all-commands.sh --command \
  "hey paicom open task manager" \
  "hey paicom stop chrome" \
  "hey paicom shut down" \
  "hey paicom calibrate my trackers" \
  "hey paicom switch to the vr headset microphone" \
  "hey paicom become a background process" \
  --interval 2000
```

---

## Understanding Test Output

### Success Output
```
[TEST] (00:00.12) Injected #1: "hey paicom open the browser"
[TEST]   -> Dispatch: QUEUED, sent to game IPC queue '.../test-command-queue.txt'.
```

This means:
- ✅ Command was queued to the game
- ✅ Game should process it
- ✅ Check the game window for response

### What to Watch For

**In Console:**
- `QUEUED` = Command sent to game successfully
- `FAILED` = Command could not be dispatched

**In Game Window:**
- Character animation plays
- Audio/TTS response
- Action executed (browser opens, etc.)

---

## Troubleshooting

### "PAIcom.exe not found"
Make sure the game is installed in `PAIcom_Player_Folder/`:
```bash
ls -la PAIcom_Player_Folder/PAIcom.exe
```

### "Could not load commands.txt"
Verify commands file exists:
```bash
ls -la PAIcom_Player_Folder/custom-commands/commands.txt
```

### Game doesn't respond to commands
1. Check if game launched successfully
2. Wait 35 seconds for game to initialize
3. Verify migration mode is `full`:
   ```bash
   ./test-all-commands.sh --migration-mode full
   ```
4. Check game logs in `PAIcom_Player_Folder/`

### Commands inject too fast/slow
Adjust interval:
```bash
# Slower (2 seconds between commands)
./test-all-commands.sh --interval 2000

# Faster (500ms between commands)
./test-all-commands.sh --interval 500
```

### Test runs forever
Press `Ctrl+C` to stop, or add duration limit:
```bash
./test-all-commands.sh --duration 60  # Auto-stop after 60 seconds
```

---

## Advanced Testing

### Test with Runtime Diagnostics
```bash
./test-all-commands.sh --runtime-diagnostic --runtime-diagnostic-duration 120
```

This enables detailed logging in the game about:
- Method discovery
- Handler resolution
- Dispatch pipeline
- Animation triggers

### Test with Verbose OWW Logging
First, patch with verbose logging:
```bash
./build-patch-and-launch.sh --migration-mode full --oww-verbose-log --no-launch
```

Then run test:
```bash
./quick-test-all.sh
```

Check logs:
```bash
cat PAIcom_Player_Folder/launcher-runtime.log | grep "\[oww\]"
```

### Test Different Migration Modes
```bash
# Stable mode (no Vosk)
./test-all-commands.sh --migration-mode stable

# Probe mode (64-bit trial)
./test-all-commands.sh --migration-mode probe

# Full mode (64-bit committed)
./test-all-commands.sh --migration-mode full
```

---

## Performance Testing

### Measure Latency
```bash
# Run test and measure command-to-response time
./test-all-commands.sh --interval 3000 --duration 120

# In another terminal, monitor response times:
tail -f PAIcom_Player_Folder/launcher-runtime.log | grep -E "Injected|Dispatch"
```

### Stress Test
```bash
# Fast injection (500ms intervals) for 5 minutes
./test-all-commands.sh --interval 500 --duration 300
```

### Accuracy Test
```bash
# Slow injection (3s intervals) to verify each command works
./test-all-commands.sh --interval 3000
```

---

## Test Results Tracking

Create a test results file to track which commands work:

```bash
# Run test and save output
./quick-test-all.sh 2>&1 | tee test-results.log

# Analyze results
grep "QUEUED" test-results.log | wc -l    # Count successful
grep "FAILED" test-results.log | wc -l    # Count failed
grep "FAILED" test-results.log             # Show failed commands
```

---

## Complete Command List (99 total)

See `COMMAND_CATEGORIES.md` for full breakdown by category.

Quick reference:
- 28 web browsers & apps
- 7 Steam integration
- 6 music control
- 2 volume control
- 19 location search
- 18 conversation/chat
- 4 games/activities
- 6 system utilities
- 3 personality/show
- 2 information

---

## Next Steps After Testing

1. **Identify failing commands** - Check which ones don't trigger responses
2. **Check handler discovery** - Look for "Found handler" in logs
3. **Verify token files** - Ensure `.txt` files exist in `custom-commands/`
4. **Test with voice** - Use live testing for real voice pipeline:
   ```bash
   # See LIVE-TESTING.md for voice testing
   ```

5. **Optimize settings** - See `OPTIMIZATION_GUIDE.md` for accuracy/speed improvements
