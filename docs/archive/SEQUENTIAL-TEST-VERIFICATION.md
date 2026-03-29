# Testing the Sequential Method Testing Fix

Now that the category filter is properly applied during method discovery, you can verify that each sequential test truly invokes only methods from the target category.

## Quick Verification Test

Run a single category test and examine the logs:

```bash
# Test animation methods
PAICOM_SEQUENTIAL_METHOD_TEST=1 \
PAICOM_TEST_CATEGORY=animation-methods \
./build-patch-and-launch.sh \
    --test-commands \
    --test-duration 5 \
    --migration-mode full \
    --runtime-diagnostic \
    --runtime-diagnostic-duration 5
```

## What to Look For

### Before the Fix (Broken Behavior):
- Methods from multiple categories would be invoked in a single test run
- Log would show `[methodtest] ✓ [animation-methods]` for non-animation methods (lying about category)
- Multiple different method types invoked in one test

### After the Fix (Correct Behavior):
- Only methods matching the target category are selected and invoked
- All logged methods show `[methodtest] ✓ [animation-methods]` for animation-only tests
- Log shows `[methodtest] Category filter: animation-methods` at startup
- If no methods match the category, logs show `[category filter active: animation-methods]`

## Full Sequential Test Suite

Run the complete sequential testing framework:

```bash
./run-sequential-method-tests.sh
```

This will:
1. Generate or find a baseline diagnostic log
2. Analyze methods from first 60 seconds
3. Run sequential tests for each method category
4. Report results with metrics

## Examining Test Logs

After running tests, check the diagnostic logs:

```bash
# See all method test attempts for a specific test
grep "\[methodtest\]" PAIcom_Player_Folder/diagnostics/paicom-live-startup.log | head -30

# Count methods by category
grep "\[methodtest\]" PAIcom_Player_Folder/diagnostics/paicom-*.log | grep -oE "\[.*?\]" | sort | uniq -c

# See only successful invocations
grep "\[methodtest\].*SUCCESS" PAIcom_Player_Folder/diagnostics/paicom-*.log

# See failures
grep "\[methodtest\].*FAILED" PAIcom_Player_Folder/diagnostics/paicom-*.log
```

## Verifying Each Category

### Test 1: Animation Methods
```bash
PAICOM_SEQUENTIAL_METHOD_TEST=1 PAICOM_TEST_CATEGORY=animation-methods \
./build-patch-and-launch.sh --test-commands --test-duration 3

# Should only invoke methods with "animation" in name
```

### Test 2: Audio Methods
```bash
PAICOM_SEQUENTIAL_METHOD_TEST=1 PAICOM_TEST_CATEGORY=audio-methods \
./build-patch-and-launch.sh --test-commands --test-duration 3

# Should only invoke methods with "audio" or "sound" in name
```

### Test 3: Render Methods
```bash
PAICOM_SEQUENTIAL_METHOD_TEST=1 PAICOM_TEST_CATEGORY=render-methods \
./build-patch-and-launch.sh --test-commands --test-duration 3

# Should only invoke methods with "paint", "render", or "draw" in name
```

### Test 4: Visibility Methods
```bash
PAICOM_SEQUENTIAL_METHOD_TEST=1 PAICOM_TEST_CATEGORY=visibility-methods \
./build-patch-and-launch.sh --test-commands --test-duration 3

# Should only invoke methods with "show", "hide", or "visible" in name
```

### Test 5: Form Methods
```bash
PAICOM_SEQUENTIAL_METHOD_TEST=1 PAICOM_TEST_CATEGORY=form-methods \
./build-patch-and-launch.sh --test-commands --test-duration 3

# Should only invoke methods with "form" or "window" in name
```

### Test 6: Input Methods
```bash
PAICOM_SEQUENTIAL_METHOD_TEST=1 PAICOM_TEST_CATEGORY=input-methods \
./build-patch-and-launch.sh --test-commands --test-duration 3

# Should only invoke methods with "text", "input", or "textbox" in name
```

### Test 7: Button Methods
```bash
PAICOM_SEQUENTIAL_METHOD_TEST=1 PAICOM_TEST_CATEGORY=button-methods \
./build-patch-and-launch.sh --test-commands --test-duration 3

# Should only invoke methods with "click" or "button" in name
```

### Test 8: Event Handlers
```bash
PAICOM_SEQUENTIAL_METHOD_TEST=1 PAICOM_TEST_CATEGORY=event-handlers \
./build-patch-and-launch.sh --test-commands --test-duration 3

# Should only invoke methods with "event" or "handler" in name
```

## Understanding Category Determination

The `GetMethodCategory()` method in OpenWakeWordHelper.cs determines which category a method belongs to:

```csharp
"paint", "render", "draw"        →  "render-methods"
"audio", "sound"                  →  "audio-methods"
"animation"                        →  "animation-methods"
"click", "button"                  →  "button-methods"
"show", "hide", "visible"          →  "visibility-methods"
"form", "window"                   →  "form-methods"
"text", "input", "textbox"         →  "input-methods"
"event", "handler" (ends with _)   →  "event-handlers"
```

## Troubleshooting

### No Methods Found for Category
If you get a log entry like:
```
[methodtest] Category filter: animation-methods
[oww-handler-discovery] No high-confidence handler found in assemblies [category filter active: animation-methods]
```

This could mean:
1. No methods with "animation" in the name exist in the loaded assemblies
2. All animation methods scored below the minimum threshold (4 points)
3. The category name is misspelled in the environment variable

**Solution:** Check what methods are actually available:
```bash
grep "\[methodtest\]" PAIcom_Player_Folder/diagnostics/paicom-*.log | cut -d'[' -f2 | cut -d'['  -f1 | sort | uniq
```

### Methods Not Being Filtered
If methods from wrong categories are still being invoked:
1. Verify the fix is in your compiled binary (shouldn't happen since build succeeded)
2. Check that `PAICOM_SEQUENTIAL_METHOD_TEST=1` is being set
3. Restart PAIcom after setting variables (cached state may apply old logic)

### Test Results Show Wrong Categories
If logs show methods being tested in the wrong category:
1. Verify the category naming matches exactly (case-sensitive)
2. Check GetMethodCategory() logic matches your method names
3. Add custom keywords to GetMethodCategory() if needed for your methods

## Next Steps

1. ✓ Run full sequential test suite: `./run-sequential-method-tests.sh`
2. Review test report: `cat PAIcom_Player_Folder/diagnostics/sequential-method-test-report.txt`
3. Verify each category tested correctly
4. Update documentation if categories are different than expected
5. Use these tests to validate compatibility across platforms
