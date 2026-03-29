# Sequential Method Testing Framework

## Overview

This framework systematically tests all methods that are called during the first 20 seconds of PAIcom startup. Each method/family is tested individually with a 5-second test burst using the command `hey paicom open the browser`.

## Quick Start

```bash
cd /Users/sheryluglis/Downloads/CrossPlatformPatcher

# Run the complete testing pipeline
./run-sequential-method-tests.sh
```

That's it! The script will:
1. Generate a fresh 90-second baseline diagnostic log
2. Analyze methods called in the first 20 seconds
3. Run sequential 5-second tests for each method category
4. Generate comprehensive reports

## How It Works

### Phase 1: Log Analysis
The `analyze-early-methods.py` script:
- Parses the runtime diagnostic log
- Extracts all methods called in the first 20 seconds
- Groups them by category:
  - `audio-methods`: Audio callbacks
  - `animation-methods`: Animation handlers
  - `button-methods`: Button click handlers
  - `event-handlers`: General event handlers  
  - `form-methods`: Form initialization
  - `input-methods`: Input/TextBox handlers
  - `render-methods`: Paint/Render methods
  - `visibility-methods`: Show/Hide methods

### Phase 2: Sequential Testing
The `sequential-method-test.sh` script:
- Runs individual 5-second tests for each category
- Each test uses: `hey paicom open the browser` (short command)
- Sets environment variables:
  - `PAICOM_SEQUENTIAL_METHOD_TEST=1` (enables test mode)
  - `PAICOM_TEST_CATEGORY=<category>` (filters to specific category)
- Logs detailed results for each test

### Phase 3: Results Analysis
OpenWakeWordHelper logs:
- Each method invocation with `[methodtest]` prefix
- Success/failure status with `✓` or `✗`
- Execution details and error messages
- Category and method signature

## Environment Variables

These are automatically set by the test runner, but can be used manually:

```bash
# Enable sequential method testing mode
export PAICOM_SEQUENTIAL_METHOD_TEST=1

# Filter to a specific category (optional)
export PAICOM_TEST_CATEGORY=animation-methods

# Test number (for logging)
export PAICOM_METHOD_TEST_NUM=1

# Run build with test mode enabled
./build-patch-and-launch.sh \
  --test-commands --test-duration 5 \
  --migration-mode full \
  --runtime-diagnostic --runtime-diagnostic-duration 5
```

## Output Files

All results are saved to `PAIcom_Player_Folder/diagnostics/`:

### Main Reports
- **sequential-method-test-report.txt** - Comprehensive text report
- **sequential-method-test-results.log** - Summary of pass/fail results

### Individual Test Logs
- **methodtest-1-animation-methods-*.log** - Animation handler test
- **methodtest-2-audio-methods-*.log** - Audio callback test
- **methodtest-3-button-methods-*.log** - Button handler test
- **methodtest-4-event-handlers-*.log** - General event handler test
- **methodtest-5-form-methods-*.log** (Form initialization test
- **methodtest-6-input-methods-*.log** - Input handler test
- **methodtest-7-render-methods-*.log** - Render/paint test
- **methodtest-8-visibility-methods-*.log** - Show/hide handler test

## Reading the Output

### Analyzer Output
```
SEQUENTIAL METHOD TEST PLAN
============================
Total early methods (0-20s): 347
Method categories to test: 8

TEST #1: Test animation-methods (42 unique methods)
  First appeared: 8.45s after startup
  Methods to test: 42
    - D9B+]}FOz6OifCnUpI8ffY^W!.⁬⁭‭⁫⁯‮...
    - ...
```

### Test Results Output
```
[TEST #1] Testing Animation handlers (animation-methods)... 
[TEST #1] Duration: 5s | Command: 'hey paicom open the browser'
✓ TEST #1: animation-methods - PASS (5s)
```

### Method Logging in Logs
```
[methodtest] Sequential method testing ENABLED
[methodtest] Category filter: animation-methods
[methodtest] ✓ [animation-methods] Form1..Method1 SUCCESS
[methodtest] ✓ [animation-methods] Form1..Method2 SUCCESS
[methodtest] ✗ [animation-methods] Form2..AnimHandler FAILED
[methodtest] Detail: System.InvalidOperationException: Handle not created
```

## Interpreting Results

### Success Indicators
- ✓ marker in logs
- "SUCCESS" in method test output
- "handler invoked" or "Dispatch succeeded" messages
- Individual method logs show 0 or small error counts

### Failure Indicators
- ✗ marker in logs
- "FAILED" in method test output
- Exception messages (InvalidOperationException, PlatformNotSupportedException, etc.)
- Missing output from expected methods

## Manual Testing

To test a specific category manually:

```bash
cd /Users/sheryluglis/Downloads/CrossPlatformPatcher

# Test animation methods with direct output
PAICOM_SEQUENTIAL_METHOD_TEST=1 \
PAICOM_TEST_CATEGORY=animation-methods \
./build-patch-and-launch.sh \
  --test-commands --test-duration 5 \
  --migration-mode full \
  --runtime-diagnostic --runtime-diagnostic-duration 5 \
  2>&1 | grep "\[methodtest\]"
```

## Using Existing Baseline Log

If you already have a diagnostic log and want to run tests based on it:

```bash
./run-sequential-method-tests.sh \
  /path/to/runtime-diagnostics-raw-20260326-233751.log
```

This skips the baseline generation and goes straight to analysis and testing.

## Troubleshooting

### No Methods Found
- Make sure the diagnostic log exists and is recent
- Check that the log file path is correct
- Verify the log format (should start with `# Runtime diagnostics raw log`)

### Tests Run Too Fast
- Increase `--test-duration` parameter
- Some methods may not need 5 seconds to test
- Look at individual method logs for details

### Missing Method Categories
- The baseline log may not have captured all methods
- Run a longer diagnostic (e.g., 120+ seconds)
- Ensure PAIcom fully initializes before the log ends

## Advanced Usage

### Verbose Method Logging
```bash
PAICOM_ENABLE_VERBOSE_LOGGING=1 \
PAICOM_SEQUENTIAL_METHOD_TEST=1 \
./build-patch-and-launch.sh \
  --test-commands --test-duration 5 \
  --migration-mode full \
  --runtime-diagnostic --runtime-diagnostic-duration 10
```

### Stress Testing
Run the same category multiple times:
```bash
for i in {1..3}; do
  echo "Stress test run $i"
  PAICOM_SEQUENTIAL_METHOD_TEST=1 \
  PAICOM_TEST_CATEGORY=animation-methods \
  ./build-patch-and-launch.sh \
    --test-commands --test-duration 5 \
    --migration-mode full
done
```

## Background

The testing framework is based on analysis of runtime diagnostic logs that show:

1. **Timeline of method invocation** - Which methods are called when
2. **Method categories** - Grouping related methods together
3. **Obfuscation handling** - Finding methods despite name mangling
4. **Reflection-based discovery** - Testing the actual handler discovery code

The 20-second window captures the most critical initialization phase including:
- Audio system initialization (2s)
- Animation system load (8-9s)
- Form creation and initialization (20-22s)

## Architecture Diagram

```
PAIcom Startup (90s test)
    ↓
Runtime Diagnostic Logs (all methods captured)
    ↓
[analyze-early-methods.py]
    ↓
Extract methods from 0-20s window
    ↓
Group by category (audio, animation, form, etc.)
    ↓
Generate test plan
    ↓
[sequential-method-test.sh]
    ↓
For each category:
  └→ Run 5-second test
  └→ PAICOM_TEST_CATEGORY=<category>
  └→ OpenWakeWordHelper logs results
  └→ Parse log for [methodtest] entries
  └→ Record pass/fail
    ↓
Generate report
    ↓
Results ready for analysis
```

## Next Steps

After running tests:

1. **Review Report** - `sequential-method-test-report.txt`
2. **Check Failed Categories** - Look at individual .log files
3. **Extract Error Patterns** - Common exceptions or missing types
4. **Apply Fixeess** - Implement compatibility patches based on findings
5. **Run Again** - Verify fixes work

---

For questions or issues, check the individual test logs in `PAIcom_Player_Folder/diagnostics/`
