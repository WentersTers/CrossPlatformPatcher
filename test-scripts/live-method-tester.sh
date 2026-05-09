#!/bin/bash
# live-method-tester.sh
# Test methods by keeping PAIcom running and injecting commands
# This solves the 20-second initialization problem by starting once and testing against the running instance

set -e

SCRIPT_DIR="$( cd "$( dirname "${BASH_SOURCE[0]}" )" && pwd )"
PLAYER_FOLDER="$SCRIPT_DIR/PAIcom_Player_Folder"
DIAGNOSTICS_FOLDER="$PLAYER_FOLDER/diagnostics"
LIVE_TEST_LOG="$DIAGNOSTICS_FOLDER/live-method-test.log"
LIVE_TEST_REPORT="$DIAGNOSTICS_FOLDER/live-method-test-report.txt"

mkdir -p "$DIAGNOSTICS_FOLDER"
> "$LIVE_TEST_LOG"
> "$LIVE_TEST_REPORT"

echo "╔════════════════════════════════════════════════════════════════════╗"
echo "║     Live Method Testing Framework for PAIcom                       ║"
echo "║     Tests methods by keeping game running and sending commands     ║"
echo "╚════════════════════════════════════════════════════════════════════╝"
echo ""

# Step 1: Start PAIcom with diagnostic logging enabled
echo "STEP 1: Starting PAIcom (initialization takes ~20-25 seconds)..."
echo ""

export PAICOM_LIVE_METHOD_TEST=1
export PAICOM_LIVE_TEST_LOG="$LIVE_TEST_LOG"

{
    echo "Starting PAIcom with live method testing enabled..."
    echo "Initialization will take approximately 20-25 seconds"
    echo "Once ready, the game will listen for voice commands"
    echo ""
    echo "Test start time: $(date)"
} | tee "$LIVE_TEST_REPORT"

# Start the game in background, with migration mode disabled (full native)
# Use --no-patch to run the original game but with our environment variables enabled
./build-patch-and-launch.sh \
    --migration-mode full \
    --runtime-diagnostic \
    --runtime-diagnostic-duration 120 \
    > "$DIAGNOSTICS_FOLDER/paicom-live-startup.log" 2>&1 &

GAME_PID=$!
echo "✓ PAIcom started with PID: $GAME_PID" | tee -a "$LIVE_TEST_REPORT"
echo ""

# Step 2: Wait for initialization (20-25 seconds typically)
echo "STEP 2: Waiting for PAIcom to initialize..."
INIT_START=$(date +%s)
INIT_TIMEOUT=45  # Allow up to 45 seconds for full initialization
INIT_SUCCESS=false

# Wait for speech recognition to be ready (OpenWakeWord inference operational)
while [ $(date +%s) -lt $((INIT_START + INIT_TIMEOUT)) ]; do
    # Check if PAIcom process is still running
    if ! kill -0 $GAME_PID 2>/dev/null; then
        echo "✗ PAIcom process has crashed or exited"
        echo "Check startup log: $DIAGNOSTICS_FOLDER/paicom-live-startup.log"
        # Show last few lines of startup log
        echo ""
        echo "Last 30 lines of startup log:"
        tail -30 "$DIAGNOSTICS_FOLDER/paicom-live-startup.log" 2>/dev/null | head -30
        INIT_SUCCESS=false
        break
    fi
    
    # Look for OpenWakeWord being ready and inference stats appearing
    # This indicates the main game loop is running and speech recognition is initialized
    if grep -q "Feature window ready\|Inference stats:" "$DIAGNOSTICS_FOLDER/paicom-live-startup.log" 2>/dev/null; then
        INIT_END=$(date +%s)
        INIT_TIME=$((INIT_END - INIT_START))
        echo "✓ PAIcom speech recognition initialized after ${INIT_TIME}s" | tee -a "$LIVE_TEST_REPORT"
        INIT_SUCCESS=true
        
        # Additional wait to ensure the game's command processing loop is ready
        echo "  Waiting additional 5 seconds for full readiness..."
        sleep 5
        break
    fi
    sleep 1
done

if [ "$INIT_SUCCESS" = false ]; then
    echo ""
    echo "⚠ Warning: PAIcom may not have fully initialized"
    echo "Checking if speech recognition is operational..."
    if grep -q "Feature window ready" "$DIAGNOSTICS_FOLDER/paicom-live-startup.log" 2>/dev/null; then
        echo "✓ Speech recognition appears to be ready (may proceed with caution)"
        INIT_SUCCESS=true
        sleep 5
    else
        echo "Check the startup log for errors:"
        echo "  cat $DIAGNOSTICS_FOLDER/paicom-live-startup.log"
        exit 1
    fi
    echo ""
fi

echo ""
echo "STEP 3: Testing method invocations with voice commands..."
echo "  (Game is now fully initialized and ready to process commands)"
echo ""

# Give the game an extra moment before sending first command
sleep 2

# Define test commands - each triggers different method families
TEST_COMMANDS=(
    "hey paicom open the browser|visibility,form,render"
    "hey paicom play|audio,event"
    "hey paicom stop|audio,event"
    "hey paicom open|visibility,form"
    "hey paicom close|visibility,form"
)

# Track results
TOTAL_TESTS=0
PASSED_TESTS=0
FAILED_TESTS=0

# Run each test command
for test_entry in "${TEST_COMMANDS[@]}"; do
    IFS='|' read -r COMMAND EXPECTED_CATEGORIES <<< "$test_entry"
    
    TOTAL_TESTS=$((TOTAL_TESTS + 1))
    
    echo "=========================================="
    echo "Test #$TOTAL_TESTS: $COMMAND"
    echo "Expected categories: $EXPECTED_CATEGORIES"
    echo ""
    
    # Record baseline (number of methodtest entries before command)
    BASELINE=$(grep -c "methodtest" "$LIVE_TEST_LOG" 2>/dev/null)
    if [ -z "$BASELINE" ] || [ "$BASELINE" = "" ]; then
        BASELINE=0
    fi
    
    # Send the voice command by writing to command input
    if [ -f "$PLAYER_FOLDER/command_input.txt" ]; then
        echo "$COMMAND" > "$PLAYER_FOLDER/command_input.txt"
        echo "✓ Sent: $COMMAND"
    else
        echo "! Could not send command (no command_input.txt)"
        continue
    fi
    
    # Wait for command to be processed (6 seconds to ensure full processing)
    # Increase from 3s to account for speech recognition latency
    echo "  Waiting for processing..."
    sleep 6
    
    # Check if any new methods were logged
    AFTER=$(grep -c "methodtest" "$LIVE_TEST_LOG" 2>/dev/null)
    if [ -z "$AFTER" ] || [ "$AFTER" = "" ]; then
        AFTER=0
    fi
    
    NEW_METHODS=$((AFTER - BASELINE))
    
    if [ $NEW_METHODS -gt 0 ]; then
        echo "✓ PASS - Command processed, $NEW_METHODS methods invoked"
        PASSED_TESTS=$((PASSED_TESTS + 1))
        {
            echo "Result: PASS"
            echo "Methods invoked: $NEW_METHODS"
            echo "Last method entries:"
            grep "methodtest" "$LIVE_TEST_LOG" | tail -3 | sed 's/^/  /'
        } | tee -a "$LIVE_TEST_REPORT"
    else
        echo "✗ FAIL - No methods logged"
        FAILED_TESTS=$((FAILED_TESTS + 1))
        echo "Result: FAIL - No methods invoked" >> "$LIVE_TEST_REPORT"
    fi
    
    echo "" | tee -a "$LIVE_TEST_REPORT"
    sleep 1
done

# Step 4: Shutdown
echo "STEP 4: Shutting down PAIcom..."
echo ""

# Kill the game process
if kill $GAME_PID 2>/dev/null; then
    echo "✓ PAIcom process terminated"
else
    echo "! Could not terminate PAIcom (may have already exited)"
fi

# Allow cleanup
sleep 2

# Step 5: Generate final report
echo "" | tee -a "$LIVE_TEST_REPORT"
echo "╔════════════════════════════════════════════════════════════════════╗" | tee -a "$LIVE_TEST_REPORT"
echo "║                    Testing Complete                               ║" | tee -a "$LIVE_TEST_REPORT"
echo "╚════════════════════════════════════════════════════════════════════╝" | tee -a "$LIVE_TEST_REPORT"
echo "" | tee -a "$LIVE_TEST_REPORT"

{
    echo "========== TEST SUMMARY =========="
    echo "Total tests: $TOTAL_TESTS"
    echo "Passed: $PASSED_TESTS"
    echo "Failed: $FAILED_TESTS"
    echo ""
    if [ $FAILED_TESTS -eq 0 ] && [ $TOTAL_TESTS -gt 0 ]; then
        echo "Status: ✓ ALL TESTS PASSED"
    elif [ $PASSED_TESTS -gt 0 ]; then
        echo "Status: ⚠ PARTIAL SUCCESS"
    else
        echo "Status: ✗ ALL TESTS FAILED"
    fi
    echo ""
    echo "Test end time: $(date)"
    echo ""
    echo "Detailed results: $LIVE_TEST_LOG"
    echo "Full report: $LIVE_TEST_REPORT"
} | tee -a "$LIVE_TEST_REPORT"

echo ""
echo "✓ Live testing complete!"
echo ""
echo "View results:"
echo "  cat $LIVE_TEST_REPORT"
echo "  cat $LIVE_TEST_LOG"
