# QUICK START: Sequential Method Testing

## TL;DR

Run this one command to test all methods from the first 20 seconds of PAIcom startup:

```bash
cd /Users/sheryluglis/Downloads/CrossPlatformPatcher
./run-sequential-method-tests.sh
```

That's it! The script will:
1. ✓ Generate fresh diagnostic logs
2. ✓ Analyze all methods called in first 20 seconds
3. ✓ Test each method **family sequentially** with 5-second bursts
4. ✓ Generate detailed reports

**Estimated runtime: 5-7 minutes**

---

## What Gets Tested?

Your specific test request: **5-second test bursts per method family**

The framework automatically:
- Extracts methods from early startup logs (0-20s)
- Groups them into 8 categories based on function
- Tests each category with the short command: `hey paicom open the browser`
- Logs **PASS/FAIL** for each method tested

### Method Categories Tested
1. **Animation methods** - Form animation handlers
2. **Audio methods** - Audio/sound callbacks  
3. **Button methods** - Click handlers
4. **Event handlers** - General event handlers
5. **Form methods** - Form initialization  
6. **Input methods** - TextBox/input handlers
7. **Render methods** - Paint/draw handlers
8. **Visibility methods** - Show/hide handlers

---

## What Happens During Each Test

Each 5-second test (+2s delay) does this:

```
[TEST #1] Category: animation-methods
  ├─ Send: "hey paicom open the browser" 
  ├─ Duration: 5 seconds
  ├─ OpenWakeWordHelper logs each method invocation:
  │  ├─ ✓ [animation-methods] Form1.AnimationHandler1 SUCCESS
  │  ├─ ✓ [animation-methods] Form1.AnimationHandler2 SUCCESS  
  │  └─ ✗ [animation-methods] Form2.UnknownHandler FAILED
  └─ Result: PASS (at least some methods worked)
```

---

## Reading the Output

### Console Output
```
╔════════════════════════════════════════════════════════════════════╗
║     Sequential Method Testing Framework for PAIcom                 ║
║     Testing all methods from first 20 seconds of startup           ║
╚════════════════════════════════════════════════════════════════════╝

Step 1: Generating baseline diagnostic log...
✓ Baseline log created: .../runtime-diagnostics-raw-20260327-*.log

Step 2: Analyzing methods from first 20 seconds...
SEQUENTIAL METHOD TEST PLAN
===========================
Total early methods (0-20s): 347
Method categories to test: 8

TEST #1: Test animation-methods (42 unique methods)
  First appeared: 8.45s after startup
  Methods to test: 42

[continuing for all 8 categories...]

Step 3: Running sequential method tests...
============================
[TEST #1] Testing Animation handlers (animation-methods)...
✓ TEST #1: animation-methods - PASS (5s)
[TEST #2] Testing Audio callbacks (audio-methods)...
✓ TEST #2: audio-methods - PASS (5s)
[continuing for all tests...]

Test Summary
============
Total tests: 8
Passed: 7
Failed: 1
Status: ⚠ MIXED RESULTS

Full report: .../sequential-method-test-report.txt
```

### Report Files

All results saved to: `PAIcom_Player_Folder/diagnostics/`

1. **sequential-method-test-report.txt** - Full text report
2. **sequential-method-test-results.log** - Summary (one line per test)
3. **methodtest-1-animation-methods-*.log** - Detailed log for test #1
4. **methodtest-2-audio-methods-*.log** - Detailed log for test #2
5. ... (one for each category)

### Finding Failed Methods

To see which specific methods failed:

```bash
# Show failed categories
grep "✗\|FAIL" PAIcom_Player_Folder/diagnostics/sequential-method-test-results.log

# Show detailed error for a specific test
cat PAIcom_Player_Folder/diagnostics/methodtest-2-audio-methods-*.log | grep "\[methodtest\]"
```

---

## Example Run

```bash
$ ./run-sequential-method-tests.sh

[output omitted for brevity...]

========================================================================
TEST #3: Test button-methods 
========================================================================
Result: PASS
Duration: 5s
Log size: 2453 bytes

Details:
[methodtest] ✓ [button-methods] Form1.button1_Click SUCCESS
[methodtest] ✓ [button-methods] Form2.cmdExecute_Click SUCCESS

========================================================================
TEST #4: Test event-handlers
========================================================================
Result: FAIL
Duration: 5s
Log size: 1847 bytes

Details:
[methodtest] ✗ [event-handlers] Form1.OnLoad FAILED
[methodtest] Detail: System.InvalidOperationException: Handle not created

========================================================================

SUMMARY
============
Total tests: 8
Passed: 6
Failed: 2
Status: ⚠ MIXED RESULTS
```

---

## Advanced Usage

### Re-run with Existing Log
If you already have a diagnostics log and just want to run tests:

```bash
./run-sequential-method-tests.sh \
  PAIcom_Player_Folder/diagnostics/runtime-diagnostics-raw-20260326-233751.log
```

### Verbose Output
Enable extra logging:

```bash
PAICOM_ENABLE_VERBOSE_LOGGING=1 \
./run-sequential-method-tests.sh
```

### Test Specific Category Only
```bash
cd /Users/sheryluglis/Downloads/CrossPlatformPatcher

PAICOM_SEQUENTIAL_METHOD_TEST=1 \
PAICOM_TEST_CATEGORY=animation-methods \
./build-patch-and-launch.sh \
  --test-commands --test-duration 5 \
  --migration-mode full \
  --runtime-diagnostic --runtime-diagnostic-duration 5
```

Then check for `[methodtest]` markers in the diagnostics:
```bash
grep "\[methodtest\]" PAIcom_Player_Folder/diagnostics/runtime-diagnostics-raw-*.log
```

---

## What This Tests

### ✅ Tests These Critical Methods
- **Audio callbacks** - Invoked during first 2 seconds
- **Animation system** - Loaded at ~8 seconds  
- **Form initialization** - Methods called during form creation (20-22s)
- **Event handlers** - All standard Windows Forms event handlers
- **Obfuscated methods** - The ~400+ methods in the obfuscated form class

### ✅ Environment
- macOS (via Wine if needed)
- All handler discovery mechanisms
- UI threading / BeginInvoke paths
- Direct reflection invocation

### ❌ Does NOT Test
- Full event handling sequence (just individual methods)
- Keyboard/mouse input
- Long-running operations
- Audio playback completeness

---

## Next Steps

1. **Run the test**: `./run-sequential-method-tests.sh`
2. **Review results**: `cat PAIcom_Player_Folder/diagnostics/sequential-method-test-report.txt`
3. **Check failed categories**: Look at individual methodtest-*.log files
4. **Identify patterns**: Common errors, missing types, platform issues
5. **Apply fixes**: Update compatibility patchers based on findings
6. **Run again**: Verify fixes work

---

## Architecture

```
LogAnalysis (analyze-early-methods.py)
 └─ Extract methods from 0-20s
 └─ Group by category
 └─ Generate test plan

TestRunner (sequential-method-test.sh)  
 └─ For each category:
    ├─ Set PAICOM_TEST_CATEGORY
    ├─ Run 5-second test burst
    ├─ Parse logs for [methodtest] markers
    └─ Record pass/fail

Helper (OpenWakeWordHelper.cs)
 └─ On method invocation:
    ├─ Check PAICOM_SEQUENTIAL_METHOD_TEST
    ├─ Filter by PAICOM_TEST_CATEGORY
    ├─ Log [methodtest] entries
    └─ Track success/failure
```

---

## Questions?

- **Build fails?** Run `dotnet build CrossPlatformPatcher.csproj -c Release`
- **Scripts not executable?** Run `chmod +x *.sh *.py`
- **Old logs in the way?** Safe to delete `PAIcom_Player_Folder/diagnostics/methodtest-*.log`
- **Full details?** See [SEQUENTIAL-TESTING-GUIDE.md](SEQUENTIAL-TESTING-GUIDE.md)

---

**Ready? Let's go:**
```bash
./run-sequential-method-tests.sh
```
