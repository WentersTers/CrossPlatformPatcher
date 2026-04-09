#!/usr/bin/env bash

set -euo pipefail

SCRIPT_DIR="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
PROJECT_FILE="$SCRIPT_DIR/CrossPlatformPatcher.csproj"
SETUP_PROJECT_FILE="$SCRIPT_DIR/InstallerWizard/InstallerWizard.csproj"
PLAYER_DIR="$SCRIPT_DIR/PAIcom_Player_Folder"
PLAYER_EXE="$PLAYER_DIR/PAIcom.exe"
PATCHED_EXE="$PLAYER_DIR/PAIcom_patched.exe"
PUBLISH_ROOT="$SCRIPT_DIR/publish/build-patch-and-launch"
PATCHER_NAME="CrossPlatformPatcher"
SETUP_WIZARD_NAME="SetupWizard"
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
  --migration-mode <mode>      Launcher migration mode: stable, probe, or full.
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

die() {
    printf 'ERROR: %s\n' "$*" >&2
    exit 1
}

detect_rid() {
    local os arch
    os="$(uname -s 2>/dev/null || echo Unknown)"
    arch="$(uname -m 2>/dev/null || echo Unknown)"

    case "$os" in
        Darwin)
            case "$arch" in
                arm64) echo "osx-arm64" ;;
                x86_64|amd64) echo "osx-x64" ;;
                *) die "Unsupported macOS architecture: $arch" ;;
            esac
            ;;
        Linux)
            case "$arch" in
                x86_64|amd64) echo "linux-x64" ;;
                *) die "Unsupported Linux architecture: $arch" ;;
            esac
            ;;
        MINGW*|MSYS*|CYGWIN*|Windows_NT)
            echo "win-x64"
            ;;
        *)
            die "Unsupported operating system: $os"
            ;;
    esac
}

cleanup() {
    if [[ -n "$CURRENT_PID" ]] && kill -0 "$CURRENT_PID" 2>/dev/null; then
        kill -TERM -- "-$CURRENT_PID" 2>/dev/null || true
        kill -TERM "$CURRENT_PID" 2>/dev/null || true
        wait "$CURRENT_PID" 2>/dev/null || true
    fi
}

run_step() {
    local label="$1"
    shift

    printf '\n==> %s\n' "$label"

    if (( VERBOSE )); then
        printf '[VERBOSE] Command: %s\n' "$*"
    fi

    local output_redirect=""
    if (( SHOW_BUILD_OUTPUT == 0 )); then
        output_redirect="/dev/null"
    fi

    if command -v setsid >/dev/null 2>&1; then
        if [[ -n "$output_redirect" ]]; then
            setsid "$@" > "$output_redirect" 2>&1 &
        else
            setsid "$@" &
        fi
    else
        if [[ -n "$output_redirect" ]]; then
            "$@" > "$output_redirect" 2>&1 &
        else
            "$@" &
        fi
    fi

    CURRENT_PID="$!"
    wait "$CURRENT_PID"
    local status=$?
    CURRENT_PID=""
    
    if (( VERBOSE )); then
        printf '[VERBOSE] Command completed with exit code: %d\n' "$status"
    fi
    
    return "$status"
}

RID=""
MIGRATION_MODE="stable"

while [[ $# -gt 0 ]]; do
    case "$1" in
        --rid)
            [[ $# -ge 2 ]] || die "--rid requires a value"
            RID="$2"
            shift 2
            ;;
        --verbose)
            VERBOSE=1
            shift
            ;;
        --migration-mode)
            [[ $# -ge 2 ]] || die "--migration-mode requires a value"
            MIGRATION_MODE="$2"
            case "$MIGRATION_MODE" in
                stable|probe|full)
                    ;;
                *)
                    die "Unsupported migration mode: $MIGRATION_MODE (expected: stable, probe, full)"
                    ;;
            esac
            shift 2
            ;;
        --test-commands)
            TEST_COMMANDS=1
            NO_LAUNCH=1
            shift
            ;;
        --test-interval)
            [[ $# -ge 2 ]] || die "--test-interval requires a value"
            TEST_INTERVAL="$2"
            shift 2
            ;;
        --test-duration)
            [[ $# -ge 2 ]] || die "--test-duration requires a value"
            TEST_DURATION="$2"
            shift 2
            ;;
        --runtime-diagnostic)
            RUNTIME_DIAGNOSTIC=1
            shift
            ;;
        --runtime-diagnostic-duration)
            [[ $# -ge 2 ]] || die "--runtime-diagnostic-duration requires a value"
            RUNTIME_DIAGNOSTIC_DURATION="$2"
            shift 2
            ;;
        --file-command-input)
            FILE_COMMAND_INPUT=1
            shift
            ;;
        --show-build-output)
            SHOW_BUILD_OUTPUT=1
            shift
            ;;
        --no-launch)
            NO_LAUNCH=1
            shift
            ;;
        --build-log)
            [[ $# -ge 2 ]] || die "--build-log requires a value"
            BUILD_LOG_FILE="$2"
            SHOW_BUILD_OUTPUT=1
            shift 2
            ;;
        -h|--help)
            usage
            exit 0
            ;;
        *)
            die "Unknown argument: $1"
            ;;
    esac
done

if (( VERBOSE )); then
    set -x
fi

trap 'cleanup; exit 130' INT TERM
trap cleanup EXIT

[[ -f "$PROJECT_FILE" ]] || die "Project file not found: $PROJECT_FILE"
[[ -d "$PLAYER_DIR" ]] || die "Player folder not found: $PLAYER_DIR"
[[ -f "$PLAYER_EXE" ]] || die "PAIcom.exe not found: $PLAYER_EXE"

if [[ -z "$RID" ]]; then
    RID="$(detect_rid)"
fi

if [[ "$RID" == win-* ]]; then
    PATCHER_FILE="$PATCHER_NAME.exe"
    SETUP_WIZARD_FILE="$SETUP_WIZARD_NAME.exe"
else
    PATCHER_FILE="$PATCHER_NAME"
    SETUP_WIZARD_FILE="$SETUP_WIZARD_NAME"
fi

PUBLISH_DIR="$PUBLISH_ROOT/$RID"
PUBLISHED_PATCHER="$PUBLISH_DIR/$PATCHER_FILE"
PLAYER_PATCHER="$PLAYER_DIR/$PATCHER_FILE"
PUBLISHED_SETUP_WIZARD="$PUBLISH_DIR/$SETUP_WIZARD_FILE"
PLAYER_SETUP_WIZARD="$PLAYER_DIR/$SETUP_WIZARD_FILE"

printf 'Detected runtime identifier: %s\n' "$RID"
printf 'Migration mode: %s\n' "$MIGRATION_MODE"
printf 'Player folder: %s\n' "$PLAYER_DIR"
printf 'Patched output: %s\n' "$PATCHED_EXE"

if [[ -x "$SCRIPT_DIR/prepare-vosk-models.sh" ]]; then
    run_step "Preparing Vosk model archives" \
        "$SCRIPT_DIR/prepare-vosk-models.sh"
fi

run_step "Building Release configuration" \
    dotnet build "$PROJECT_FILE" -c Release

run_step "Publishing self-contained patcher for $RID" \
    dotnet publish "$PROJECT_FILE" \
    -c Release \
    -r "$RID" \
    --self-contained true \
    -p:PublishSingleFile=true \
    -p:AssemblyName="$PATCHER_NAME" \
    -o "$PUBLISH_DIR"

run_step "Publishing GUI setup wizard for $RID" \
    dotnet publish "$SETUP_PROJECT_FILE" \
    -c Release \
    -r "$RID" \
    --self-contained true \
    -p:PublishSingleFile=true \
    -p:AssemblyName="$SETUP_WIZARD_NAME" \
    -o "$PUBLISH_DIR"

cp "$PUBLISHED_PATCHER" "$PLAYER_PATCHER"
chmod +x "$PLAYER_PATCHER" 2>/dev/null || true

cp "$PUBLISHED_SETUP_WIZARD" "$PLAYER_SETUP_WIZARD"
chmod +x "$PLAYER_SETUP_WIZARD" 2>/dev/null || true

printf '\n==> Copying published patcher to player folder\n'
printf '    %s -> %s\n' "$PUBLISHED_PATCHER" "$PLAYER_PATCHER"
printf '\n==> Copying GUI setup wizard to player folder\n'
printf '    %s -> %s\n' "$PUBLISHED_SETUP_WIZARD" "$PLAYER_SETUP_WIZARD"

run_step "Patching PAIcom.exe" \
    "$PLAYER_PATCHER" "$PLAYER_EXE" --out "$PATCHED_EXE" --migration-mode "$MIGRATION_MODE"

# Export test environment variables early so they're available in all launch paths
# (test-commands, no-launch, and normal launch)
PAICOM_MIGRATION_MODE="$MIGRATION_MODE"
if [[ -n "${PAICOM_SEQUENTIAL_METHOD_TEST:-}" ]]; then
    export PAICOM_SEQUENTIAL_METHOD_TEST
fi
if [[ -n "${PAICOM_TEST_CATEGORY:-}" ]]; then
    export PAICOM_TEST_CATEGORY
fi
if [[ -n "${PAICOM_METHOD_TEST_NUM:-}" ]]; then
    export PAICOM_METHOD_TEST_NUM
fi
if [[ -n "${PAICOM_LIVE_METHOD_TEST:-}" ]]; then
    export PAICOM_LIVE_METHOD_TEST
fi
if [[ -n "${PAICOM_LIVE_TEST_LOG:-}" ]]; then
    export PAICOM_LIVE_TEST_LOG
fi
export PAICOM_MIGRATION_MODE

if (( FILE_COMMAND_INPUT )); then
    export PAICOM_FILE_COMMAND_INPUT=1
    export PAICOM_FILE_COMMAND_INPUT_PATH="$PLAYER_DIR/input-command.txt"
fi

if (( TEST_COMMANDS )); then
    printf '\n==> Launching command injection test mode\n'
    printf '    Interval: %sms\n' "$TEST_INTERVAL"
    if [[ -n "$TEST_DURATION" ]]; then
        printf '    Duration: %s seconds\n' "$TEST_DURATION"
    fi
    if (( RUNTIME_DIAGNOSTIC )); then
        printf '    Runtime diagnostics: enabled\n'
        if [[ -n "$RUNTIME_DIAGNOSTIC_DURATION" ]]; then
            printf '    Runtime diagnostics duration: %s seconds\n' "$RUNTIME_DIAGNOSTIC_DURATION"
        fi
    fi
    
    # Build command line arguments
    TEST_ARGS=("$PATCHED_EXE" "--interval" "$TEST_INTERVAL")
    if [[ -n "$TEST_DURATION" ]]; then
        TEST_ARGS+=("--duration" "$TEST_DURATION")
    fi
    if (( RUNTIME_DIAGNOSTIC )); then
        TEST_ARGS+=("--runtime-diagnostic")
        if [[ -n "$RUNTIME_DIAGNOSTIC_DURATION" ]]; then
            TEST_ARGS+=("--runtime-diagnostic-duration" "$RUNTIME_DIAGNOSTIC_DURATION")
        fi
    fi
    
    printf '\n==> Running command injection tests\n'
    exec "$PLAYER_PATCHER" --test-commands "${TEST_ARGS[@]}"
elif (( NO_LAUNCH )); then
    printf '\n==> Build completed successfully\n'
    printf 'Skipping launch (--no-launch flag was set)\n'
    if [[ -n "$BUILD_LOG_FILE" ]]; then
        printf 'Build log: %s\n' "$BUILD_LOG_FILE"
    fi
else
    printf '\n==> Launching patched game\n'
    exec sh "$PLAYER_DIR/setup-wizard.sh" --launch
fi
