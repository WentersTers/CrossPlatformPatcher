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

## Post-Publish Verification

It is critical to ensure that publishing the project as a self-contained executable does not break application logic or dependency resolution. 

The CrossPlatformPatcher includes a unit test suite (dotnet test) that currently contains 184 passing tests. These tests cover components such as the assembly patcher, CLI parsing, fuzzy matching, and command injection.

### Validating Self-Contained Binaries

While unit tests (dotnet test) ensure the core logic remains intact in generic build outputs, to validate the actual self-contained published artifacts, you should perform integration testing against the compiled binaries:

1. **Publish the binaries:** Run scripts\publish-all.bat (Windows) or bash scripts/publish-all.sh (macOS/Linux) to generate the publish/CrossPlatformPatcher/ outputs.
2. **Run integration tests:** If you have an integration test script that performs file path substitution or injects commands into the compiled executable, ensure it targets the published binaries. You can set the RUN_PATCH_WORKFLOW=1 environment variable to trigger integration testing within your CI or local scripts.

When RUN_PATCH_WORKFLOW=1 is provided, the test suite will run end-to-end patching against a blank test assembly, confirming that the isolated dependencies (like the bundled onnxruntime libraries or Vosk models) can be located resolving to the final executable directory instead of the traditional in/Release folder.
