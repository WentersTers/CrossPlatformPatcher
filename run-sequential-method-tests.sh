#!/bin/bash
# run-sequential-method-tests.sh
# Master runner script for sequential method testing
# 
# This script orchestrates a comprehensive test of all methods that are
# called during the first 20 seconds of PAIcom startup, testing each
# method/family sequentially with 5-second test bursts.
#
# Usage: ./run-sequential-method-tests.sh [baseline-log]
#
# If baseline-log is provided, it will be used for analysis.
# Otherwise, a fresh 90-second diagnostic run will be executed.

set -e

SCRIPT_DIR="$( cd "$( dirname "${BASH_SOURCE[0]}" )" && pwd )"
PLAYER_FOLDER="$SCRIPT_DIR/PAIcom_Player_Folder"
DIAGNOSTICS_FOLDER="$PLAYER_FOLDER/diagnostics"

echo ""
echo "╔════════════════════════════════════════════════════════════════════╗"
echo "║     Sequential Method Testing Framework for PAIcom                 ║"
echo "║     Testing all methods from first 20 seconds of startup           ║"
echo "╚════════════════════════════════════════════════════════════════════╝"
echo ""

# Check if baseline log is provided
BASELINE_LOG="${1:-}"

if [ -z "$BASELINE_LOG" ]; then
    echo "Step 1: Finding existing diagnostic log (or generating new)..."
    echo ""
    
    # Find most recent successful log (large files likely completed fully)
    BASELINE_LOG=$(find "$DIAGNOSTICS_FOLDER" -name "runtime-diagnostics-raw-*.log" -type f -size +100k 2>/dev/null | sort -r | head -1)
    
    if [ -z "$BASELINE_LOG" ]; then
        echo "  No suitable log found. Generating new baseline (90 seconds)..."
        echo "  Running: ./build-patch-and-launch.sh --test-commands --test-duration 90 ..."
        echo ""
        
        # Run comprehensive diagnostic
        timeout 120 ./build-patch-and-launch.sh \
            --test-commands \
            --test-duration 90 \
            --migration-mode full \
            --runtime-diagnostic \
            --runtime-diagnostic-duration 90 \
            > /dev/null 2>&1 || true
        
        # Find the latest log (wait a moment for file to be written)
        sleep 1
        BASELINE_LOG=$(find "$DIAGNOSTICS_FOLDER" -name "runtime-diagnostics-raw-*.log" -type f -size +100k 2>/dev/null | sort -r | head -1)
        
        if [ -z "$BASELINE_LOG" ]; then
            echo "ERROR: No diagnostic log generated. Build may have failed."
            exit 1
        fi
    fi
    
    echo "✓ Using log: $BASELINE_LOG"
    echo ""
else
    echo "Step 1: Using provided baseline log..."
    if [ ! -f "$BASELINE_LOG" ]; then
        echo "ERROR: Log file not found: $BASELINE_LOG"
        exit 1
    fi
    echo "✓ $BASELINE_LOG"
    echo ""
fi

echo "Step 2: Analyzing methods from first 20 seconds..."
echo ""

python3 "$SCRIPT_DIR/analyze-early-methods.py" "$BASELINE_LOG"

echo ""
echo "Step 3: Running sequential method tests..."
echo ""
echo "This will test each method category with 5-second bursts."
echo "Estimated duration: ~3-5 minutes (depending on method count)"
echo ""

# Run sequential tests
"$SCRIPT_DIR/sequential-method-test.sh" "$BASELINE_LOG"

echo ""
echo "╔════════════════════════════════════════════════════════════════════╗"
echo "║                    Testing Complete                               ║"
echo "╚════════════════════════════════════════════════════════════════════╝"
echo ""
echo "Test reports and logs are available in:"
echo "  Report: $DIAGNOSTICS_FOLDER/sequential-method-test-report.txt"
echo "  Results: $DIAGNOSTICS_FOLDER/sequential-method-test-results.log"
echo "  Individual logs: $DIAGNOSTICS_FOLDER/methodtest-*.log"
echo ""
