#!/bin/bash
# Sequential method tester - runs multiple 5-second test bursts
# Each test focuses on a specific method category

set -e

SCRIPT_DIR="$( cd "$( dirname "${BASH_SOURCE[0]}" )" && pwd )"
PLAYER_FOLDER="$SCRIPT_DIR/PAIcom_Player_Folder"
DIAGNOSTICS_FOLDER="$PLAYER_FOLDER/diagnostics"
TEST_REPORT="$DIAGNOSTICS_FOLDER/sequential-method-test-report.txt"
TEST_LOG="$DIAGNOSTICS_FOLDER/sequential-method-test-results.log"

# Create output files
mkdir -p "$DIAGNOSTICS_FOLDER"
> "$TEST_REPORT"
> "$TEST_LOG"

echo "Sequential Method Test Runner"
echo "=============================="
echo ""

# Get log file from parameter or find the most recent
if [ -n "$1" ] && [ -f "$1" ]; then
    LATEST_LOG="$1"
else
    # Find the most recent diagnostic log
    LATEST_LOG=$(find "$DIAGNOSTICS_FOLDER" -name "runtime-diagnostics-raw-*.log" -type f -size +100k 2>/dev/null | sort -r | head -1)
fi

if [ -z "$LATEST_LOG" ]; then
    echo "ERROR: No diagnostic logs found. Run a full diagnostic first:"
    echo "  ./build-patch-and-launch.sh --test-commands --test-duration 90 --runtime-diagnostic --runtime-diagnostic-duration 90"
    exit 1
fi

echo "Using log: $LATEST_LOG"
echo ""

# Analyze the log to get method categories
echo "Analyzing methods from first 20 seconds of startup..."
python3 "$SCRIPT_DIR/analyze-early-methods.py" "$LATEST_LOG"

echo ""
echo "=============================="
echo "Starting sequential method tests..."
echo "=============================="
echo ""

# Test categories with their descriptions
# These will be tested in order
declare -a TEST_CATEGORIES=(
    "animation-methods:Animation handlers"
    "audio-methods:Audio callbacks"
    "button-methods:Button click handlers"
    "event-handlers:General event handlers"
    "form-methods:Form initialization"
    "input-methods:Input/TextBox handlers"
    "render-methods:Paint/Render methods"
    "visibility-methods:Show/Hide methods"
)

TEST_NUM=1
PASS_COUNT=0
FAIL_COUNT=0
TIMESTAMP=$(date +%Y%m%d-%H%M%S)

{
    echo "Sequential Method Test Report"
    echo "============================="
    echo "Generated: $(date)"
    echo "Baseline Log: $LATEST_LOG"
    echo ""
    echo "Test Plan:"
    echo ""
} | tee -a "$TEST_REPORT"

for category_info in "${TEST_CATEGORIES[@]}"; do
    IFS=':' read -r category description <<< "$category_info"
    
    category_log="$DIAGNOSTICS_FOLDER/methodtest-${TEST_NUM}-${category}-${TIMESTAMP}.log"
    
    {
        echo "=========================================================================="
        echo "TEST #$TEST_NUM: $description"
        echo "Category: $category"
        echo "Command: hey paicom open the browser"
        echo "Duration: 5 seconds"
        echo "Log: $category_log"
        echo "=========================================================================="
        echo ""
    } | tee -a "$TEST_REPORT"
    
    echo "[TEST #$TEST_NUM] Testing $description ($category)..." 
    echo "[TEST #$TEST_NUM] Duration: 5s | Command: 'hey paicom open the browser'"
    
    # Prepare environment for this specific test
    export PAICOM_SEQUENTIAL_METHOD_TEST=1
    export PAICOM_TEST_CATEGORY="$category"
    export PAICOM_METHOD_TEST_NUM="$TEST_NUM"
    
    # Run the test with a short 5-second burst
    # Using --test-duration 5 means the test will inject commands for approximately 5 seconds
    TEST_START=$(date +%s)
    
    # Run without timeout (macOS doesn't have timeout command by default)
    ./build-patch-and-launch.sh \
        --test-commands \
        --test-duration 5 \
        --migration-mode full \
        --runtime-diagnostic \
        --runtime-diagnostic-duration 5 \
        > "$category_log" 2>&1
    
    TEST_RESULT=$?
    
    if [ $TEST_RESULT -eq 0 ]; then
        TEST_END=$(date +%s)
        TEST_DURATION=$((TEST_END - TEST_START))
        
        # Check if test succeeded (look for success markers in log)
        if grep -q "SUCCESS\|succeeded\|Dispatch.*SUCCESS\|handler.*invoked" "$category_log" 2>/dev/null; then
            RESULT="PASS"
            ((PASS_COUNT++))
            STATUS_MARKER="✓"
        else
            RESULT="FAIL"
            ((FAIL_COUNT++))
            STATUS_MARKER="✗"
        fi
    else
        TEST_END=$(date +%s)
        TEST_DURATION=$((TEST_END - TEST_START))
        RESULT="ERROR"
        ((FAIL_COUNT++))
        STATUS_MARKER="!"
    fi
    
    echo "" | tee -a "$TEST_REPORT"
    
    # Log test result
    echo "$STATUS_MARKER TEST #$TEST_NUM: $category - $RESULT (${TEST_DURATION}s)"
    echo "  $description: $RESULT" >> "$TEST_LOG"
    
    {
        echo "Result: $RESULT"
        echo "Duration: ${TEST_DURATION}s"
        echo "Log size: $(wc -c < "$category_log" 2>/dev/null || echo 0) bytes"
        echo ""
    } | tee -a "$TEST_REPORT"
    
    # Extract relevant log lines for this category
    {
        echo "Details:"
        grep -E "oww-|dispatch|animation|audio|handler|method" "$category_log" 2>/dev/null | tail -20 || echo "  (no details available)"
        echo ""
    } | tee -a "$TEST_REPORT"
    
    ((TEST_NUM++))
    
    # Small delay between tests
    sleep 2
done

# Print summary
echo ""
echo "=============================="
echo "Test Summary"
echo "=============================="

{
    echo ""
    echo "=============================="
    echo "SUMMARY"
    echo "=============================="
    echo "Total tests: $((PASS_COUNT + FAIL_COUNT))"
    echo "Passed: $PASS_COUNT"
    echo "Failed: $FAIL_COUNT"
    
    if [ $FAIL_COUNT -eq 0 ]; then
        echo "Status: ✓ ALL TESTS PASSED"
    elif [ $PASS_COUNT -eq 0 ]; then
        echo "Status: ✗ ALL TESTS FAILED"
    else
        echo "Status: ⚠ MIXED RESULTS"
    fi
    
    echo ""
    echo "Report saved to: $TEST_REPORT"
    echo "Individual logs in: $DIAGNOSTICS_FOLDER/methodtest-*"
    echo ""
} | tee -a "$TEST_REPORT"

echo "Passed: $PASS_COUNT / $((PASS_COUNT + FAIL_COUNT))"
echo "Failed: $FAIL_COUNT / $((PASS_COUNT + FAIL_COUNT))"
echo ""
echo "Full report: $TEST_REPORT"
echo ""

# Show results summary
if [ $FAIL_COUNT -gt 0 ]; then
    echo "Failed categories:"
    grep "✗\|FAIL\|ERROR" "$TEST_LOG" 2>/dev/null || true
    echo ""
fi

if [ $PASS_COUNT -gt 0 ]; then
    echo "Passed categories:"
    grep "✓\|PASS" "$TEST_LOG" 2>/dev/null || true
    echo ""
fi

exit 0
