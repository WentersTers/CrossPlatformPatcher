# Live Method Testing - Quick Reference

## Problem Solved

The previous sequential testing framework had a fatal flaw: it restarted PAIcom every 5 seconds, but the game takes 20+ seconds just to initialize. Methods never got tested because they were never given a chance to run.

## Solution: Live Testing

**Keep PAIcom running continuously and test against the live instance.**

## Quick Start

```bash
cd /Users/sheryluglis/Downloads/CrossPlatformPatcher
./live-method-tester.sh
```

That's it! The script will:
- ✓ Start PAIcom once
- ✓ Wait for initialization (~25 seconds)
- ✓ Send 5 test voice commands
- ✓ Log which methods succeed/fail
- ✓ Generate a report

## What Changed

### New Files
- **live-method-tester.sh** - Main test harness (keeps PAIcom running)
- **LIVE-TESTING-GUIDE.md** - Full documentation

### Modified Files
- **Core/OpenWakeWordHelper.cs**:
  - Added `_liveMethodTestMode` and `_liveTestLogPath` fields
  - Added `RecordLiveTestAttempt()` method
  - Updated `InitializeMethodTestingMode()` to detect live test environment variables
  - Live test logging happens automatically when methods are invoked

### Environment Variables
```bash
PAICOM_LIVE_METHOD_TEST=1              # Enable live testing
PAICOM_LIVE_TEST_LOG=<log_file_path>   # Where to write results
```

## Results

Check these files after testing:
- `PAIcom_Player_Folder/diagnostics/live-method-test-report.txt` - Summary
- `PAIcom_Player_Folder/diagnostics/live-method-test.log` - Detailed method logs

## Build Status
✓ Builds successfully (0 errors, 11 warnings - all pre-existing)

## How It Works

```
[Start PAIcom] → [Wait 25s for init] → [Send commands] → [Log results] → [Shutdown]
                                             ↓
                                   Methods now execute & get logged
                                   ✓ = Success
                                   ✗ = Failed + exception details
```

## Key Difference

| Aspect | Sequential | Live |
|--------|-----------|------|
| Game startup | Every test (restart) | Once |
| Initialization time | Never completes | Full 20-25s |
| Method execution | Never happens | Real invocations |
| Test reliability | Fails (no init) | Works (full init) |
| Total test time | N × 5s | ~60s total |
