#!/usr/bin/env bash
# Quick test: Run all 99 commands with optimal settings
# This script tests every single command from commands.txt

set -euo pipefail

SCRIPT_DIR="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
PLAYER_DIR="$SCRIPT_DIR/PAIcom_Player_Folder"
COMMANDS_FILE="$PLAYER_DIR/custom-commands/commands.txt"
PATCHER_PROJECT="$SCRIPT_DIR/CrossPlatformPatcher.csproj"

echo "═══════════════════════════════════════════════════════════════"
echo "  CROSS-PLATFORM PATCHER - ALL COMMANDS TEST"
echo "═══════════════════════════════════════════════════════════════"
echo ""

# Verify PAIcom.exe exists
if [[ ! -f "$PLAYER_DIR/PAIcom.exe" ]]; then
    echo "❌ ERROR: PAIcom.exe not found in $PLAYER_DIR"
    echo "   Make sure the game is installed in the Player folder."
    exit 1
fi

# Verify commands.txt exists
if [[ ! -f "$COMMANDS_FILE" ]]; then
    echo "❌ ERROR: commands.txt not found at $COMMANDS_FILE"
    exit 1
fi

# Count commands
TOTAL=$(grep -c '(' "$COMMANDS_FILE" || echo "0")
echo "📋 Found $TOTAL commands to test"
echo "📍 Game: $PLAYER_DIR/PAIcom.exe"
echo ""

# Check if patcher is built
echo "🔨 Building patcher..."
dotnet build "$PATCHER_PROJECT" -c Release -v quiet

# Check if patched exe exists, if not create it
PATCHED_EXE="$PLAYER_DIR/PAIcom_patched.exe"
TEST_EXE="$PLAYER_DIR/PAIcom_patched_test.exe"

if [[ ! -f "$PATCHED_EXE" ]]; then
    echo ""
    echo "📦 No patched executable found. Creating one..."
    dotnet run --project "$PATCHER_PROJECT" -c Release -- \
        "$PLAYER_DIR/PAIcom.exe" \
        --out "$PATCHED_EXE" \
        --migration-mode full
fi

# Ensure test exe exists (copy from patched)
if [[ ! -f "$TEST_EXE" ]]; then
    echo "📋 Creating test executable from patched version..."
    cp "$PATCHED_EXE" "$TEST_EXE"
    # Copy all required DLLs and files that run.sh expects
    for file in PAIcom.OWW.dll Vosk.dll NAudio.Core.dll NAudio.WinMM.dll libvosk.dll libgcc_s_seh-1.dll libstdc++-6.dll libwinpthread-1.dll onnxruntime.dll Microsoft.ML.OnnxRuntime.dll System.Memory.dll System.Buffers.dll System.Numerics.Vectors.dll System.Runtime.CompilerServices.Unsafe.dll; do
        if [[ -f "$PLAYER_DIR/../$file" ]]; then
            cp "$PLAYER_DIR/../$file" "$PLAYER_DIR/" 2>/dev/null || true
        fi
    done
fi

echo ""
echo "🚀 Starting test with ALL $TOTAL commands..."
echo "   - Auto-launches PAIcom.exe (patched version)"
echo "   - Injects each command via IPC"
echo "   - Waits 1 second between commands"
echo "   - Press Ctrl+C to stop"
echo ""
echo "═══════════════════════════════════════════════════════════════"
echo ""

# Run the test with all commands extracted from commands.txt
COMMAND_ARGS=()
while IFS= read -r line; do
    # Remove Windows line endings
    line=$(echo "$line" | tr -d '\r')
    [[ -z "$line" ]] && continue
    # Remove trailing (token.txt) part
    cmd=$(echo "$line" | sed 's/ *([^)]*)$//')
    COMMAND_ARGS+=("--command" "$cmd")
done < "$COMMANDS_FILE"

dotnet run --project "$PATCHER_PROJECT" -c Release -- \
    --test-commands "$PATCHED_EXE" \
    --interval 1000 \
    --migration-mode full \
    "${COMMAND_ARGS[@]}"
