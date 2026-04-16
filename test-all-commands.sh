#!/usr/bin/env bash
# Comprehensive test script to test ALL commands from commands.txt
# Usage: ./test-all-commands.sh [--interactive] [--duration SECONDS] [--interval MS]

set -euo pipefail

SCRIPT_DIR="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
PLAYER_DIR="$SCRIPT_DIR/PAIcom_Player_Folder"
COMMANDS_FILE="$PLAYER_DIR/custom-commands/commands.txt"
PATCHER_EXE="$SCRIPT_DIR/CrossPlatformPatcher.csproj"

# Default settings
INTERACTIVE_MODE=0
DURATION=""
INTERVAL="1000"
MIGRATION_MODE="full"
VERBOSE=0

# Colors for output
RED='\033[0;31m'
GREEN='\033[0;32m'
YELLOW='\033[1;33m'
BLUE='\033[0;34m'
CYAN='\033[0;36m'
NC='\033[0m' # No Color

usage() {
    cat <<'EOF'
Usage:
    ./test-all-commands.sh [OPTIONS]

Options:
  --interactive           Run in interactive mode (manual command entry)
  --duration <seconds>    Auto-test duration limit (default: test all commands once)
  --interval <ms>         Time between commands in ms (default: 1000)
  --migration-mode <mode> Launcher mode: stable, probe, or full (default: full)
  --verbose               Show detailed output
  -h, --help              Show this help

Examples:
  # Test all commands once with 1s interval
  ./test-all-commands.sh

  # Test all commands with 2s interval for 60 seconds
  ./test-all-commands.sh --interval 2000 --duration 60

  # Interactive mode - type commands manually
  ./test-all-commands.sh --interactive

  # Fast test with 500ms interval
  ./test-all-commands.sh --interval 500
EOF
}

# Parse arguments
while [[ $# -gt 0 ]]; do
    case "$1" in
        --interactive)
            INTERACTIVE_MODE=1
            shift
            ;;
        --duration)
            DURATION="$2"
            shift 2
            ;;
        --interval)
            INTERVAL="$2"
            shift 2
            ;;
        --migration-mode)
            MIGRATION_MODE="$2"
            shift 2
            ;;
        --verbose)
            VERBOSE=1
            shift
            ;;
        -h|--help)
            usage
            exit 0
            ;;
        *)
            echo "Unknown option: $1"
            usage
            exit 1
            ;;
    esac
done

# Check if commands file exists
if [[ ! -f "$COMMANDS_FILE" ]]; then
    echo -e "${RED}ERROR: commands.txt not found at $COMMANDS_FILE${NC}"
    exit 1
fi

# Extract just the command phrases (remove token references)
echo -e "${CYAN}Loading commands from $COMMANDS_FILE...${NC}"
COMMANDS=()
while IFS= read -r line; do
    # Remove Windows line endings
    line=$(echo "$line" | tr -d '\r')
    # Skip empty lines
    [[ -z "$line" ]] && continue
    
    # Remove trailing (token.txt) part
    cmd=$(echo "$line" | sed 's/ *([^)]*)$//')
    COMMANDS+=("$cmd")
done < "$COMMANDS_FILE"

TOTAL_COMMANDS=${#COMMANDS[@]}
echo -e "${GREEN}Loaded $TOTAL_COMMANDS commands${NC}"

if [[ $INTERACTIVE_MODE -eq 1 ]]; then
    # Interactive mode
    echo -e "\n${BLUE}═══════════════════════════════════════════════════════════════${NC}"
    echo -e "${BLUE}   INTERACTIVE COMMAND TEST MODE${NC}"
    echo -e "${BLUE}═══════════════════════════════════════════════════════════════${NC}"
    echo -e "${YELLOW}This will launch PAIcom.exe and allow you to type commands.${NC}"
    echo -e "${YELLOW}Type 'help' for command list, 'exit' to quit.${NC}"
    echo -e "${BLUE}═══════════════════════════════════════════════════════════════${NC}\n"
    
    dotnet run --project "$PATCHER_EXE" -- --test-commands "$PLAYER_DIR/PAIcom.exe" --interactive
else
    # Auto mode - test all commands
    echo -e "\n${BLUE}═══════════════════════════════════════════════════════════════${NC}"
    echo -e "${BLUE}   AUTOMATED COMMAND TEST - ALL $TOTAL_COMMANDS COMMANDS${NC}"
    echo -e "${BLUE}═══════════════════════════════════════════════════════════════${NC}"
    echo -e "${YELLOW}Interval: ${INTERVAL}ms${NC}"
    if [[ -n "$DURATION" ]]; then
        echo -e "${YELLOW}Duration: ${DURATION}s limit${NC}"
    else
        echo -e "${YELLOW}Duration: Test all commands once${NC}"
    fi
    echo -e "${YELLOW}Migration Mode: $MIGRATION_MODE${NC}"
    echo -e "${BLUE}═══════════════════════════════════════════════════════════════${NC}\n"
    
    # Build command arguments
    TEST_ARGS=("--test-commands" "$PLAYER_DIR/PAIcom.exe" "--interval" "$INTERVAL" "--migration-mode" "$MIGRATION_MODE")
    
    if [[ -n "$DURATION" ]]; then
        TEST_ARGS+=("--duration" "$DURATION")
    fi
    
    # Add all commands
    for cmd in "${COMMANDS[@]}"; do
        TEST_ARGS+=("--command" "$cmd")
    done
    
    # Run the test
    echo -e "${CYAN}Starting test...${NC}\n"
    dotnet run --project "$PATCHER_EXE" -- "${TEST_ARGS[@]}"
fi

echo -e "\n${GREEN}Test completed!${NC}"
