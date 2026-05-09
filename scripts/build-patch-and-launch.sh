#!/usr/bin/env bash

set -euo pipefail

SCRIPT_DIR="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
PROJECT_FILE="$SCRIPT_DIR/CrossPlatformPatcher.csproj"
PLAYER_DIR="$SCRIPT_DIR/PAIcom_Player_Folder"
PLAYER_EXE="$PLAYER_DIR/PAIcom.exe"
PATCHED_EXE="$PLAYER_DIR/PAIcom_patched.exe"
PUBLISH_ROOT="$SCRIPT_DIR/publish/build-patch-and-launch"
PATCHER_NAME="CrossPlatformPatcher"
CURRENT_PID=""
VERBOSE=0
SHOW_BUILD_OUTPUT=0
NO_LAUNCH=0
BUILD_LOG_FILE=""
TEST_COMMANDS=0
TEST_INTERVAL="1000"
TEST_DURATION=""
RUNTIME_DIAGNOSTIC=0
RUNTIME_DIAGNOSTIC_DURATION=""
FILE_COMMAND_INPUT=0

usage() {
    cat <<'EOF'
Usage:
    ./build-patch-and-launch.sh [--rid <runtime-identifier>] [OPTIONS]

Options:
  --rid <runtime-identifier>   Override the detected publish target.
    --migration-mode <mode>      Launcher migration mode: stable, probe, or full (default: full).
  --test-commands              Run in command injection test mode (auto-launch game).
  --test-interval <ms>         Interval between test commands in ms (default: 1000).
  --test-duration <seconds>    Run tests for N seconds, then auto-stop (default: infinite).
  --file-command-input         Enable file-based command input (read from input-command.txt).
    --runtime-diagnostic         Enable runtime diagnostics in launched game process.
    --runtime-diagnostic-duration <seconds>  Runtime diagnostics snapshot duration.
  --verbose                    Show all executed commands (set -x mode).
  --show-build-output          Display full build/publish command output.
  --no-launch                  Skip launching the patched game.
  --build-log <file>           Save build output to a log file.
  -h, --help                   Show this help text.

Examples:
  ./build-patch-and-launch.sh
  ./build-patch-and-launch.sh --rid osx-arm64
  ./build-patch-and-launch.sh --migration-mode probe
  ./build-patch-and-launch.sh --file-command-input  # Enable input-command.txt monitoring
  ./build-patch-and-launch.sh --test-commands
  ./build-patch-and-launch.sh --test-commands --test-duration 30 --test-interval 500
    ./build-patch-and-launch.sh --test-commands --runtime-diagnostic --runtime-diagnostic-duration 120
  ./build-patch-and-launch.sh --verbose --show-build-output
  ./build-patch-and-launch.sh --build-log build.log --no-launch
EOF
}

# (rest of script copied from root file)
