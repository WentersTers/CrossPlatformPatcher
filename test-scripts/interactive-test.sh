#!/usr/bin/env bash
# Interactive command test - manually type commands to test
# Best for testing specific commands or debugging

set -euo pipefail

SCRIPT_DIR="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
PLAYER_DIR="$SCRIPT_DIR/PAIcom_Player_Folder"
PATCHER_PROJECT="$SCRIPT_DIR/CrossPlatformPatcher.csproj"

echo "═══════════════════════════════════════════════════════════════"
echo "  INTERACTIVE COMMAND TEST MODE"
echo "═══════════════════════════════════════════════════════════════"
echo ""

if [[ ! -f "$PLAYER_DIR/PAIcom.exe" ]]; then
    echo "❌ ERROR: PAIcom.exe not found in $PLAYER_DIR"
    exit 1
fi

echo "🎮 Launching PAIcom.exe in test mode..."
echo "   You'll be able to type commands manually."
echo ""
echo "Available voice commands (examples):"
echo "  - hey paicom open the browser"
echo "  - hey paicom play some music"
echo "  - hey paicom volume up"
echo "  - hey paicom open discord"
echo "  - hey paicom open youtube"
echo "  - hey paicom open task manager"
echo ""
echo "Type 'help' for full list, 'exit' to quit."
echo ""
echo "═══════════════════════════════════════════════════════════════"
echo ""

dotnet run --project "$PATCHER_PROJECT" -c Release -- \
    --test-commands "$PLAYER_DIR/PAIcom.exe" \
    --interactive \
    --migration-mode full
