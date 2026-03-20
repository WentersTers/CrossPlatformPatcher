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

usage() {
    cat <<'EOF'
Usage:
  ./build-patch-and-launch.sh [--rid <runtime-identifier>] [OPTIONS]

Options:
  --rid <runtime-identifier>   Override the detected publish target.
  --verbose                    Show all executed commands (set -x mode).
  --show-build-output          Display full build/publish command output.
  --no-launch                  Skip launching the patched game.
  --build-log <file>           Save build output to a log file.
  -h, --help                   Show this help text.

Examples:
  ./build-patch-and-launch.sh
  ./build-patch-and-launch.sh --rid osx-arm64
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
else
    PATCHER_FILE="$PATCHER_NAME"
fi

PUBLISH_DIR="$PUBLISH_ROOT/$RID"
PUBLISHED_PATCHER="$PUBLISH_DIR/$PATCHER_FILE"
PLAYER_PATCHER="$PLAYER_DIR/$PATCHER_FILE"

printf 'Detected runtime identifier: %s\n' "$RID"
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

cp "$PUBLISHED_PATCHER" "$PLAYER_PATCHER"
chmod +x "$PLAYER_PATCHER" 2>/dev/null || true

printf '\n==> Copying published patcher to player folder\n'
printf '    %s -> %s\n' "$PUBLISHED_PATCHER" "$PLAYER_PATCHER"

run_step "Patching PAIcom.exe" \
    "$PLAYER_PATCHER" "$PLAYER_EXE" --out "$PATCHED_EXE"

if (( NO_LAUNCH )); then
    printf '\n==> Build completed successfully\n'
    printf 'Skipping launch (--no-launch flag was set)\n'
    if [[ -n "$BUILD_LOG_FILE" ]]; then
        printf 'Build log: %s\n' "$BUILD_LOG_FILE"
    fi
else
    printf '\n==> Launching patched game\n'
    exec sh "$PLAYER_DIR/setup-wizard.sh" --launch
fi
