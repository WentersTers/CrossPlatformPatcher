# Command Injection Testing

Quick-start guide for testing command dispatching with the running game.

## Usage

The patcher includes a test mode (`--test-commands`) that:
- **Automatically launches PAIcom.exe** so you can see animations/audio in real-time
- **Directly injects `CommandAction` objects** bypassing Vosk (voice recognition)
- **Loads real commands** from `commands.txt` for testing against actual game handlers
- **Auto-stops** with Ctrl+C, duration limit, or manual shutdown


The patcher will:
1. Launch PAIcom.exe automatically
2. Wait 3 seconds for the game to start
3. Begin injecting commands every 1 second
4. Show results in the console
5. Clean up the process when done

./build-patch-and-launch.sh --migration-mode full --test-commands --test-duration 30

### Stop The Test

Press **Ctrl+C** at any time to stop testing and kill the game process.

## How It Works

1. **Game Launch**: Uses the system's launch script or Wine to start PAIcom.exe as a subprocess
2. **Command Loading**: Reads `PAIcom_Player_Folder/custom-commands/commands.txt` and extracts real command phrases
3. **CommandAction Creation**: Uses reflection to create `CommandAction` objects with:
   - `transcript`: Full phrase "hey paicom open the browser"
   - `matchPhrase`: Command without wake word "open the browser"
   - `dispatchPhrase`: Full phrase sent to game handler
   - `confidence`: 0.95 (high confidence to trigger handler matching)
4. **Dispatch**: Calls the normal dispatcher pipeline:
   - **ReflectionCommandDispatcher**: Finds the game's command handler and invokes it
   - **ProcessFallbackCommandDispatcher**: Falls back to shell script execution if reflection fails
5. **Game Reaction**: You see the results immediately - does the game animate? Play audio? Respond?

## Example Output

```
[TEST] Command Injection Test Mode
==================================

[TEST] PAIcom Path: /Users/user/PAIcom_Player_Folder/PAIcom.exe
[TEST] Interval: 1000ms
[TEST] Duration: infinite (Ctrl+C to stop)
[TEST] Test phrases (99):
  - hey paicom play some music
  - hey paicom open the browser
  - hey paicom open redit
  - hey paicom open twitch
  - hey paicom open twiter
  ... and 94 more

[TEST] Launching PAIcom.exe...
[TEST] Waiting 3 seconds for game to initialize...
[TEST] Starting command injection...
[TEST] Press Ctrl+C to stop.

[oww] [oww-test] Dispatching test command: MatchPhrase='open the browser', DispatchPhrase='hey paicom open the browser'
[oww] [oww-command] Dispatcher 'game-reflection' skipped: System.Windows.Forms.Application is unavailable.
[oww] [oww-command] Dispatcher 'process-fallback' skipped: No fallback script found for token 'test-token'.
[TEST] (00:00.12) Injected #1: "hey paicom open the browser"
[TEST]   -> Dispatch: FAILED, No dispatcher could execute the command.

[oww] [oww-test] Dispatching test command: MatchPhrase='play some music', DispatchPhrase='hey paicom play some music'
[TEST] (00:01.14) Injected #2: "hey paicom play some music"
[TEST]   -> Dispatch: SUCCESS, Selected handler ...
```

## Typical Workflow

1. **Build the patcher**: `dotnet build CrossPlatformPatcher.csproj -c Release`
2. **Find PAIcom.exe**: Locate it in your PAIcom_Player_Folder or installation
3. **Start testing**: `dotnet run --test-commands "/path/to/PAIcom.exe"`
4. **Watch the console**: See which handlers are found and what text is dispatched
5. **Watch the game**: Observe if animations/audio play in response
6. **Adjust if needed**: Try different intervals (`--interval`) or commands (`--command`)
7. **Stop**: Press Ctrl+C when done

## What This Tests

✅ **With Game Running:**
- Does the game respond to injected commands?
- Are animations triggered?
- Is audio played?
- What handler methods are being found?
- Is the reflected method actually executing?

✅ **Debug Info:**
- Handler discovery details (method name, score, confidence)
- What exact phrase is being sent to the game
- Success/failure of dispatch
- Game process lifecycle

❌ **Does NOT Test:**
- Voice wake word detection (OpenWakeWord)
- Speech recognition (Vosk)
- Audio capture from microphone
- Real-time voice pipeline

## Troubleshooting

**"System.Windows.Forms.Application is unavailable"**
- Game isn't running yet or hasn't loaded System.Windows.Forms
- Wait longer for game startup, or use `--interval 2000` to slow down injections

**"No fallback script found for token 'test-token'"**
- This is expected - we're using dummy tokens
- The reflection dispatcher is what matters (game-reflection)

**Game doesn't respond but reflection says "SUCCESS"**
- Handler method was called, but game didn't act on the command
- Might need different text format for the game's handler
- See the phrase that was dispatched in the logs - try variations with `--command`

**"Launching PAIcom.exe..." hangs forever**
- Game path might be wrong - check the file exists
- Wine might not be installed on macOS/Linux
- Check the game actually launches manually first

