# Command Injection Guide

Bypass Vosk speech recognition and inject voice commands directly into PAIcom for testing and manual control without voice input.

## Quick Start

### Why Animations/Audio Now Work For File Input

**Previous behavior:** File-based commands were dispatched but animations and audio didn't play.

**What changed:** The file monitoring system now queues animation script execution after successful dispatch, matching exactly how voice recognition works. This means both methods now:
- Execute through the dispatcher pipeline (UI simulation → process fallback → game reflection)
- Trigger corresponding animations and audio clips
- Provide identical behavior

### File-Based Command Input (Recommended for Scripting)

The easiest way to inject commands: write to a text file and PAIcom reads it automatically.

**Launch with file-based input enabled:**
```bash
./build-patch-and-launch.sh --file-command-input
```

This:
1. Builds and patches PAIcom
2. Launches the patched game
3. Creates `PAIcom_Player_Folder/input-command.txt` (auto-created if missing)
4. Monitors the file for commands in real-time

**To send a command:**
Write a command to the file and the game will process it through the animation dispatch pipeline automatically:

```bash
# Terminal 1: Launch the game with file input enabled
./build-patch-and-launch.sh --file-command-input

# Terminal 2 (while game is running): Send commands
echo "hey paicom open the browser" > PAIcom_Player_Folder/input-command.txt
sleep 1
echo "hey paicom volume up" > PAIcom_Player_Folder/input-command.txt
sleep 1
echo "hey paicom play some music" > PAIcom_Player_Folder/input-command.txt
```

**How it works:**
1. You write a command phrase to `input-command.txt`
2. PAIcom detects the new file content
3. Command goes through the **same animation and dispatch pipeline** as voice would
4. Animations, scripts, and handlers execute normally
5. File is automatically cleared, ready for the next command

### Interactive Mode (Deprecated - Use File Input Instead)

For testing individual commands interactively:

```bash
dotnet run --test-commands /path/to/PAIcom.exe --interactive
```

Type commands like:
```
>> hey paicom open the browser
>> hey paicom volume up
>> help
>> exit
```

> **Note:** This older interactive method has limitations. Use **file-based input** (`--file-command-input`) instead for better automation and reliability.

## File-Based Input Reference

### Basic Usage

```bash
# Launch game with file-based command input enabled
./build-patch-and-launch.sh --file-command-input

# (In another terminal, send commands)
echo "hey paicom open the browser" > PAIcom_Player_Folder/input-command.txt
```

### How Commands Flow

```
input-command.txt (you write here)
  ↓
[File monitor detects new content]
  ↓
ResolveCommandAction() [matches phrase to command token]
  ↓
DispatchCommandAction() [runs animations/scripts/handlers]
  ↓
Animation or script executes
  ↓
File is cleared [ready for next command]
```

### Command Format

Commands should use the full voice phrase format:

| Command Type | Format |
|---|---|
| **Full phrase** | `hey paicom open the browser` ← Preferred |
| **Short form** | `open the browser` ← Also works |
| **Whitespace** | Trimmed automatically |
| **Case** | Case-insensitive |

Valid examples:
```
hey paicom open the browser
hey paicom play some music
hey paicom volume up
hey paicom show my steam friends
```

### Bash Script Example

```bash
#!/bin/bash
CMDFILE="PAIcom_Player_Folder/input-command.txt"

# Function to send a command
send_command() {
    local cmd="$1"
    echo "Sending: $cmd"
    echo "$cmd" > "$CMDFILE"
    sleep 0.5  # Brief delay for file system
}

# Send sequence of commands
send_command "hey paicom open the browser"
sleep 2
send_command "hey paicom volume up"
sleep 1
send_command "hey paicom play some music"
```

Run it alongside your game:
```bash
# Terminal 1
./build-patch-and-launch.sh --file-command-input

# Terminal 2
bash my-commands.sh
```

### Monitoring in Real-Time

Watch the game's log file while sending commands:

```bash
tail -f PAIcom_Player_Folder/launcher-runtime.log | grep fileinput
```

You'll see:
```
[fileinput] File-based command input ENABLED
[fileinput] Input file path: /path/to/PAIcom_Player_Folder/input-command.txt
[fileinput] Received command: hey paicom open the browser
[fileinput] Dispatch result: SUCCESS - ...
[fileinput] Input file cleared, ready for next command
```

## Available Voice Commands

Here are example voice commands PAIcom recognizes:

| Command | Trigger Phrase |
|---|---|
| **Browser** | `hey paicom open the browser` |
| **Music** | `hey paicom play some music` |
| **Sleep** | `hey paicom please hide` |
| **Mute Chrome** | `hey paicom stop chrome` |
| **Steam Chat** | `hey paicom open the steam chat` |
| **Steam Library** | `hey paicom open my steam library` |
| **Steam Friends** | `hey paicom show my steam friends` |
| **Steam Invisible** | `hey paicom hide my online status on steam` |
| **Steam Online** | `hey paicom put my steam status online` |
| **Task Manager** | `hey paicom open task manager` |
| **SteamVR** | `hey paicom start the steam vr mode` |
| **Volume Up** | `hey paicom volume up` |
| **Volume Down** | `hey paicom volume down` |
| **Discord** | `hey paicom open discord` |
| **YouTube** | `hey paicom open youtube` |

For the complete list, check [custom-commands/commands.txt](PAIcom_Player_Folder/custom-commands/commands.txt).

## Script-Based Testing Example

### Test Multiple Commands in Sequence

```bash
#!/bin/bash
# test-commands.sh
set -e

GAME_DIR="PAIcom_Player_Folder"
CMDFILE="$GAME_DIR/input-command.txt"
LOGFILE="$GAME_DIR/launcher-runtime.log"

echo "[*] Starting game with file input enabled..."
./build-patch-and-launch.sh --file-command-input &
GAME_PID=$!
sleep 40  # Wait for game to initialize

trap "kill $GAME_PID 2>/dev/null; exit" INT TERM

echo "[*] Sending test commands..."

commands=(
    "hey paicom open the browser"
    "hey paicom volume up"
    "hey paicom volume down"
    "hey paicom play some music"
    "hey paicom open the steam chat"
)

for cmd in "${commands[@]}"; do
    echo "[>] $cmd"
    echo "$cmd" > "$CMDFILE"
    sleep 2
done

echo "[*] Tests complete. Game PID: $GAME_PID"
echo "[*] Logs available in: $LOGFILE"
wait $GAME_PID
```

Run it:
```bash
chmod +x test-commands.sh
./test-commands.sh
```

## Troubleshooting

### "File not found" or "Permission denied"

- Ensure PAIcom.exe path is correct
- Check that `PAIcom_Player_Folder/` is writable
- Verify `input-command.txt` was created

### Commands not being recognized

1. Check the exact phrase in [custom-commands/commands.txt](PAIcom_Player_Folder/custom-commands/commands.txt)
2. Use lowercase for best matching
3. Include "hey paicom" prefix
4. Check `launcher-runtime.log` for `[fileinput]` messages:
   ```bash
   grep fileinput PAIcom_Player_Folder/launcher-runtime.log
   ```

### Game doesn't respond to file commands

1. Verify game launched successfully (check logs)
2. Ensure 35+ seconds have passed for initialization
3. Check that `launcher-runtime.log` shows `[fileinput] File-based command input ENABLED`
4. Try a simple command like `hey paicom open the browser`
5. Inspect full log: `tail -f PAIcom_Player_Folder/launcher-runtime.log`

### File is not being cleared

- Check file permissions
- Verify PAIcom process is still running
- Look for `[fileinput-error]` messages in the log

## Advanced Usage

### Custom Input File Location

By default, the file is `PAIcom_Player_Folder/input-command.txt`, but you can override it:

```bash
export PAICOM_FILE_COMMAND_INPUT_PATH="/tmp/my-commands.txt"
./build-patch-and-launch.sh --file-command-input
```

### Combining with Diagnostics

Enable runtime diagnostics while using file input:

```bash
./build-patch-and-launch.sh --file-command-input --runtime-diagnostic
```

This generates detailed diagnostic dumps in `PAIcom_Player_Folder/diagnostics/`.

### Automated Testing Pipeline

```bash
#!/bin/bash
# Full automated test with logging

set -e

LOG_DIR="test-results-$(date +%Y%m%d-%H%M%S)"
mkdir -p "$LOG_DIR"

echo "[*] Starting automated test suite..."

./build-patch-and-launch.sh \
    --file-command-input \
    --runtime-diagnostic \
    &
GAME_PID=$!

# Wait for initialization
sleep 40

# Send commands with logging
{
    echo "hey paicom open the browser"
    sleep 2
    echo "hey paicom volume up"
    sleep 1
    echo "hey paicom play some music"
} | while read cmd; do
    echo "[$(date +%H:%M:%S)] $cmd" | tee -a "$LOG_DIR/sent-commands.log"
    echo "$cmd" > PAIcom_Player_Folder/input-command.txt
    sleep 0.5
done

sleep 10
kill $GAME_PID 2>/dev/null || true

# Collect results
cp PAIcom_Player_Folder/launcher-runtime.log "$LOG_DIR/"
echo "[*] Test results saved to: $LOG_DIR"
```

## Tips

- **Command Timing**: Write files slowly (0.5–1s between commands). Rapid writes may cause filesystem race conditions.
- **File Permissions**: Ensure the script/process writing commands has write access to the folder.
- **Logging**: Always check `launcher-runtime.log` when things don't work—it has detailed `[fileinput]` diagnostic output.
- **Animations**: File-based commands go through the same animation pipeline as voice, so all animations and scripts execute normally.


Complete Animation System Analysis
Internal Architecture Found
Main Form Class: vh"lC)qD"NqJ7k>>(D},Ep<:& (obfuscated name)

196 methods
82 fields (including 25 PictureBox controls)
This is THE Form that contains ALL animations
The Monolithic Command Handler: Method ⁪‭‪‭⁯‫‍‍⁮‌⁬‮⁭⁬‍‌‎‭‏‪⁫‪‎‎‮‏⁬‫‮

69,667 IL instructions (massive!)
8,145 branch statements (giant switch/dispatch)
7,133 method calls
Token: 0x060000B1
This single method handles ALL commands and animations internally
25 PictureBox Controls - Used to display animation frames

How It Works
The game doesn’t have a separate “animation database” - everything is inside that one massive method. When you say “hey paicom show me a cool magic trick”:

✅ The patcher’s OpenWakeWordHelper matches the command
✅ Animation script (magic.txt) executes (shows frames, plays audio)
✅ The game’s internal handler method is invoked via BeginInvoke