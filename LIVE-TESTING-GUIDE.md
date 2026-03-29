# Live Method Testing System

## Overview

The live method testing system keeps PAIcom running **continuously** and tests methods by sending voice commands while the game is active. This solves the initialization problem - instead of restarting the game every 5 seconds, it starts once, waits for initialization (~20 seconds), and then sends test commands.

## How It Works

```
┌─────────────────────────────────────────────────────────┐
│ Start PAIcom with live testing enabled                  │
│ (PAICOM_LIVE_METHOD_TEST=1)                            │
└────────────┬────────────────────────────────────────────┘
             │
             ├─ ~2-3s: Audio system initializes
             │
             ├─ ~8-10s: Assembly loading begins
             │
             ├─ ~20-25s: Form created, methods invoking
             │         ✓ OpenWakeWordHelper detects methods
             │         ✓ Logs to live test file
             │
             ├─ Once ready: Send voice commands
             │         "hey paicom open the browser"
             │         "hey paicom play"
             │         "hey paicom stop"
             │
             └─ Monitor live test log for method invocations
                ✓ = Method worked
                ✗ = Method failed
```

## Usage

### Basic Invocation

```bash
./live-method-tester.sh
```

This will:
1. Start PAIcom with live testing enabled
2. Wait ~30 seconds for initialization
3. Send 5 test voice commands in sequence
4. Monitor the live test log for method invocations
5. Shutdown PAIcom and generate a report

### What Gets Tested

Each test command triggers different method families:

| Command | Methods Tested |
|---------|----------------|
| `hey paicom open the browser` | visibility, form rendering, event handlers |
| `hey paicom play` | audio callbacks, event handlers |
| `hey paicom stop` | audio callbacks, event handlers |
| `hey paicom open` | visibility, form initialization |
| `hey paicom close` | visibility, form methods |

### Output Files

All results saved to `PAIcom_Player_Folder/diagnostics/`:

- **live-method-test-report.txt** - Summary report with pass/fail counts
- **live-method-test.log** - Detailed log of every method invocation and result
- **paicom-live-startup.log** - Full startup diagnostics from PAIcom

### Reading Results

```bash
# View the test report
cat PAIcom_Player_Folder/diagnostics/live-method-test-report.txt

# View detailed method logs
cat PAIcom_Player_Folder/diagnostics/live-method-test.log

# Check specific method results
grep "visibility" PAIcom_Player_Folder/diagnostics/live-method-test.log
grep "✗" PAIcom_Player_Folder/diagnostics/live-method-test.log  # Failed methods
```

## How Live Testing Differs from Sequential Testing

### Sequential Testing (Old Approach)
❌ Problems:
- Starts fresh PAIcom instance for each test
- Game never fully initializes (needs 20+ seconds)
- Methods never get a chance to run
- Reports all tests as failed (no initialization)

### Live Testing (New Approach)
✅ Solutions:
- Starts PAIcom **once** and keeps it running
- Waits ~25 seconds for full initialization
- Then tests against the actual running game
- Logs real method invocations as they happen
- Shows which methods actually work vs fail

## Implementation Details

### Environment Variables

When running live tests, set:

```bash
export PAICOM_LIVE_METHOD_TEST=1
export PAICOM_LIVE_TEST_LOG="/path/to/live-method-test.log"
```

### Code Changes

Modified `Core/OpenWakeWordHelper.cs`:
- Added `_liveMethodTestMode` flag
- Added `_liveTestLogPath` for log file path  
- Added `RecordLiveTestAttempt()` method
- Updated `InitializeMethodTestingMode()` to detect live test variables

When methods are invoked:
```csharp
// Every method invocation now logs to live test log
RecordLiveTestAttempt(method, success, detail);

// Output format:
// [2026-03-26T17:48:30.1234567Z] ✓ Form1.Show - SUCCESS
// [2026-03-26T17:48:30.1234567Z] ✗ Form1.OnPaint - PlatformNotSupportedException
```

## Testing Different Scenarios

### Test All Voice Commands
The default script tests 5 common voice commands. To add more:

Edit `live-method-tester.sh` and add to `TEST_COMMANDS` array:
```bash
TEST_COMMANDS=(
    "hey paicom open the browser|visibility,form,render"
    "hey paicom play|audio,event"
    "hey paicom your custom command|expected,categories"
)
```

### Test Single Command
```bash
# Start PAIcom manually with live testing
export PAICOM_LIVE_METHOD_TEST=1
export PAICOM_LIVE_TEST_LOG="./PAIcom_Player_Folder/diagnostics/live-test.log"
./build-patch-and-launch.sh --migration-mode full --runtime-diagnostic

# In another terminal, send a command
echo "hey paicom open the browser" > ./PAIcom_Player_Folder/command_input.txt

# Monitor results
tail -f ./PAIcom_Player_Folder/diagnostics/live-test.log
```

### Longer Test Duration
By default, each test gets 3 seconds to process. For slower systems:

Edit the `sleep 3` lines in `live-method-tester.sh`:
```bash
sleep 5  # Wait 5 seconds instead of 3
```

## Interpreting Results

### Success Log Entry
```
[2026-03-26T17:48:56.5123456Z] ✓ Form1.Show - SUCCESS
```
✓ = Method executed without error

### Failure Log Entry
```
[2026-03-26T17:48:56.7234567Z] ✗ Form1.OnPaint - InvalidOperationException: Handle not created
```
✗ = Method threw an exception (details included)

### Test Report Summary
```
========== TEST SUMMARY ==========
Total tests: 5
Passed: 3
Failed: 2
Status: ⚠ PARTIAL SUCCESS
```

## Troubleshooting

### No Methods Logged
- Check if PAIcom initialized: Look for method entries after 25 seconds
- Verify environment variables are set: `env | grep PAICOM_LIVE`
- Check that command_input.txt exists in PAIcom_Player_Folder
- PAIcom may have crashed: Check paicom-live-startup.log

### Methods Logged But No Voice Recognition
- Voice commands may not be supported in your environment
- Check PAIcom logs for audio/speech errors
- Try different voice commands

### Script Freezes
- PAIcom process may hang: Manually kill and retry
- Increase timeout in script if system is slow
- Check system resources (CPU, memory)

## Next Steps

Once you have live test results:

1. **Review results**: See which methods fail
2. **Identify patterns**: Do all graphics calls fail? All audio? 
3. **Fix compatibility**: Create targeted patches for failing methods
4. **Re-test**: Run live tests again to verify improvements
5. **Iterate**: Repeat until most methods pass

Example workflow:
```bash
./live-method-tester.sh
# Review results, identify paint failures
# Add graphics compatibility patch
dotnet build -c Release
./live-method-tester.sh
# Retest, see if paint methods now work
```
