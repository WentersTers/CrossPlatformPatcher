# Comprehensive Command Testing Guide

## Test Execution
Run with file-based input during long launch:
```bash
./build-patch-and-launch.sh --migration-mode full --file-command-input --test-commands --test-duration 120
```

Or manually by typing in `input-command.txt`:
```
echo "hey paicom open the steam chat" > PAIcom_Player_Folder/input-command.txt
```

## Category 1: Steam URI Commands
✓ Should translate `explorer "steam://..."` to `open '...'` on macOS  
✗ **Currently just opening Steam without URI**

| Command | File | Expected | Status |
|---------|------|----------|--------|
| `"hey paicom open the steam chat"` | `files/steam-chat.bat` | `steam://open/friends` | ❌ |
| `"hey paicom hide my online status"` | `files/invisible.bat` | `steam://friends/status/invisible` | ❌ |
| `"hey paicom put my steam status online"` | `files/online.bat` | `steam://friends/status/online` | ❌ |

**Debug**: Check if `files/steam-chat.bat` contains `explorer "steam://open/friends/"`
- If YES: BatchFileTranslator should convert it
- If NO: Add the correct URI to the .bat file

## Category 2: Browser & App Launchers  
✓ Should play animation THEN execute .bat  
✗ **Some .bat files may be missing or have wrong paths**

| Command | File | Expected | Status |
|---------|------|----------|--------|
| `"hey paicom open the browser"` | `files/internet.bat` | Opens default browser | ❌ |
| `"hey paicom open discord"` | `files/discord.bat` | Launches Discord | ❌ |
| `"hey paicom open reddit"` | `files/reddit.bat` | Opens Reddit | ❌ |

**Debug**: Check if `files/reddit.bat` exists
```bash
ls -la PAIcom_Player_Folder/files/ | grep -E "(internet|discord|reddit)\.bat"
```

## Category 3: Games (KNOWN FAILURES - System.Speech not on Wine)
✗ **Will crash with NullReferenceException in System.Speech.Recognition.SpeechRecognitionEngine**

| Command | Expected | Root Cause | Fix |
|---------|----------|-----------|-----|
| `"hey paicom lets play rock paper scissors"` | Shows game UI | System.Speech unavailable on Wine | Patch game to use UI instead of System.Speech |
| `"hey paicom lets play tic tac toe"` | Shows game UI | ^Same | ^Same |

The error is in the embedded game binary calling `SpeechRecognitionEngine.InstalledRecognizers()`, which doesn't exist on Wine.

## Category 4: Interactive Prompts (UI-based, should work)
✓ Should show dialog instead of speech input

| Command | Expected | Status |
|---------|----------|--------|
| `"hey paicom do you like chatgpt"` | Shows yes/no dialog | ❌ |
| `"hey paicom can you hear me"` | Shows response | ❓ |

## Category 5: Animation & Audio
✓ Should show animation and play audio

| Command | Expected | Status |
|---------|----------|--------|
| `"hey paicom show me a cool magic trick"` | Magic animation plays | ❓ |
| `"hey paicom play some music"` | Music plays | ❓ |

---

## Verification Steps

### 1. Verify .bat Files Have Correct Content
```bash
cat PAIcom_Player_Folder/files/steam-chat.bat      # Should be: explorer "steam://open/friends/"
cat PAIcom_Player_Folder/files/invisible.bat        # Should be: explorer "steam://friends/status/invisible"
cat PAIcom_Player_Folder/files/online.bat           # Should be: explorer "steam://friends/status/online"
```

### 2. Verify .bat Files Can Be Translated
The patcher's `BatchFileTranslator` should convert them to shell commands. Check logs for:
```
[oww] [batch-translator] Translating explorer "steam://..." to open '...'
```

### 3. Check for Missing .bat Files
```bash
comm -23 <(grep -oP '\(\K[^)]+\.bat' PAIcom_Player_Folder/custom-commands/commands.txt | sort -u) \
         <(ls PAIcom_Player_Folder/files/*.bat 2>/dev/null | xargs -n1 basename | sort -u)
```

### 4. Enable Debug Logging
Set environment variables before launch:
```bash
export PAICOM_BATCH_TO_SHELL_MODE=generate-and-test   # Generates .sh files alongside .bat
export PAICOM_MIGRATION_MODE=full                      # Ensures 64-bit Wine execution
./build-patch-and-launch.sh --file-command-input --no-launch
```

Then check generated `.sh` files:
```bash
ls -la PAIcom_Player_Folder/files/*.sh
cat PAIcom_Player_Folder/files/steam-chat.sh
```

---

## Known Issues Summary

| Issue | Commands Affected | Status | Workaround |
|-------|------------------|--------|-----------|
| System.Speech not on Wine | All games with speech input | ❌ CRITICAL | Switch games to UI-based input or use speech recognition outside game |
| .bat → .sh translation | Steam URIs not being used | ⚠️ UNKNOWN | Check if BatchFileTranslator is being invoked |
| Missing .bat files | Browser/app launchers | ? | Create missing files with proper URLs |
| Game embedded binary errors | Rock/Paper/Scissors, Tic-Tac-Toe | ❌ REQUIRES PATCH | Patch game binary to use System.Windows.Forms dialogs instead of System.Speech |

