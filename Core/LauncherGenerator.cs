namespace CrossPlatformPatcher.Core;

/// <summary>
/// Writes OS-specific launcher scripts alongside the patched exe so end users
/// can run it on Linux and macOS (via Wine/Mono) with a simple double-click or
/// shell command.
/// 
/// Generated files:
///   run.sh         - Linux/Mac shell launcher (bundled Wine -> system Wine -> Mono)
///   launch.command — Mac Finder double-click wrapper (wraps run.sh)
///   setup-wizard.sh - Interactive smart installer/setup wizard (Linux/Mac)
///   setup.command  - Mac Finder double-click wrapper for setup-wizard.sh
///   run.bat        — Windows batch launcher (direct exe start)
///   SETUP_LINUX.md — Wine + audio setup instructions for Linux
///   SETUP_MAC.md   — Wine + audio setup instructions for macOS
/// </summary>
public static class LauncherGenerator
{
    private static readonly System.Text.Encoding Utf8NoBom = new System.Text.UTF8Encoding(encoderShouldEmitUTF8Identifier: false);

    public static void WriteAll(string outputDir, string exeFileName)
    {
        Directory.CreateDirectory(outputDir);

        WriteRunSh(outputDir, exeFileName);
        WriteLaunchCommand(outputDir);
        WriteSetupWizardSh(outputDir, exeFileName);
        WriteSetupCommand(outputDir);
        WriteRunBat(outputDir, exeFileName);
        WriteSetupLinux(outputDir, exeFileName);
        WriteSetupMac(outputDir, exeFileName);

        Console.WriteLine($"  [launcher] Generated launcher scripts in: {outputDir}");
    }

    // ── run.sh ────────────────────────────────────────────────────────────

    private static void WriteRunSh(string dir, string exe)
    {
        var path = Path.Combine(dir, "run.sh");
        var content = """
            #!/usr/bin/env sh
            # PAIcom Launcher - Linux / macOS
            # Tries bundled Wine, then system Wine, then Mono.
            set -e
            SCRIPT_DIR="$(cd "$(dirname "$0")" && pwd)"
            EXE="$SCRIPT_DIR/__EXE__"
            LOG_FILE="$SCRIPT_DIR/launcher.log"
            RUNTIME_LOG="$SCRIPT_DIR/launcher-runtime.log"
            BUNDLED_WINE="$SCRIPT_DIR/wine/bin/wine"
            ALT_BUNDLED_WINE="$SCRIPT_DIR/runtime/wine/bin/wine"
            WHISKY_WINE="/Applications/Whisky.app/Contents/MacOS/wine"
            WHISKY_CMD=""
            if [ -z "$WHISKY_BOTTLE" ]; then
                WHISKY_BOTTLE="PAIcom"
            fi

            timestamp() {
                date "+%Y-%m-%d %H:%M:%S"
            }

            log() {
                printf "%s [launcher] %s\n" "$(timestamp)" "$*" >> "$LOG_FILE"
                printf "[launcher] %s\n" "$*"
            }

            launch_runtime() {
                log "Executing: $*"
                set +e
                # Export OpenWakeWord environment variables (can be overridden by user)
                export PAICOM_OWW_THRESHOLD="${PAICOM_OWW_THRESHOLD:-0.7}"
                export PAICOM_OWW_LOCK_MS="${PAICOM_OWW_LOCK_MS:-3000}"
                export PAICOM_OWW_AUDIO_CHUNK_SIZE="${PAICOM_OWW_AUDIO_CHUNK_SIZE:-1024}"
                export PAICOM_OWW_INFERENCE_THREAD_SCALE="${PAICOM_OWW_INFERENCE_THREAD_SCALE:-1.0}"
                export PAICOM_OWW_VERBOSE_LOG="${PAICOM_OWW_VERBOSE_LOG:-false}"
                
                "$@" >> "$RUNTIME_LOG" 2>&1
                EXIT_CODE=$?
                set -e
                log "Runtime exited with code: $EXIT_CODE"

                if [ "$EXIT_CODE" -ne 0 ] && grep -q "MissingManifestResourceException" "$RUNTIME_LOG"; then
                    log "Detected MissingManifestResourceException in runtime output."
                    log "Likely cause: app is running on Wine Mono instead of native .NET Framework."
                    log "Action: run setup-wizard.sh and choose '.NET Framework 4.8 (Whisky bottle)'."
                fi

                exit "$EXIT_CODE"
            }

            : > "$LOG_FILE"
            : > "$RUNTIME_LOG"
            log "Launcher start"
            log "Script dir: $SCRIPT_DIR"
            log "Target exe: $EXE"
            log "Whisky bottle: $WHISKY_BOTTLE"
            log "Runtime log: $RUNTIME_LOG"

            if command -v whisky >/dev/null 2>&1; then
                WHISKY_CMD="$(command -v whisky)"
            elif [ -x "/opt/homebrew/bin/whisky" ]; then
                WHISKY_CMD="/opt/homebrew/bin/whisky"
            elif [ -x "/usr/local/bin/whisky" ]; then
                WHISKY_CMD="/usr/local/bin/whisky"
            fi
            if [ -n "$WHISKY_CMD" ]; then
                log "Detected whisky command: $WHISKY_CMD"
            else
                log "Detected whisky command: <none>"
            fi

            # Keep this app self-contained unless the user explicitly overrides WINEPREFIX.
            if [ -z "$WINEPREFIX" ]; then
                export WINEPREFIX="$SCRIPT_DIR/.wine-prefix"
            fi
            log "WINEPREFIX: $WINEPREFIX"

            if [ ! -f "$EXE" ]; then
                log "ERROR: Target executable not found: $EXE"
                exit 1
            fi

            if [ -x "$ALT_BUNDLED_WINE" ]; then
                log "Starting with bundled Wine (runtime/wine): $ALT_BUNDLED_WINE"
                launch_runtime "$ALT_BUNDLED_WINE" "$EXE" "$@"
            elif [ -x "$BUNDLED_WINE" ]; then
                log "Starting with bundled Wine (wine): $BUNDLED_WINE"
                launch_runtime "$BUNDLED_WINE" "$EXE" "$@"
            elif [ -x "$WHISKY_WINE" ]; then
                log "Starting with Whisky Wine runtime: $WHISKY_WINE"
                launch_runtime "$WHISKY_WINE" "$EXE" "$@"
            elif command -v wine >/dev/null 2>&1; then
                log "Starting with system Wine: $(command -v wine)"
                launch_runtime wine "$EXE" "$@"
            elif [ -n "$WHISKY_CMD" ]; then
                log "Starting with Whisky CLI: $WHISKY_CMD"
                if "$WHISKY_CMD" shellenv "$WHISKY_BOTTLE" >/dev/null 2>&1; then
                    log "Whisky bottle exists: $WHISKY_BOTTLE"
                    SHELLENV_EXPORTS="$($WHISKY_CMD shellenv "$WHISKY_BOTTLE" 2>> "$LOG_FILE" | grep '^export ' || true)"
                    if [ -n "$SHELLENV_EXPORTS" ]; then
                        eval "$SHELLENV_EXPORTS"
                    fi

                    if [ -n "$WINE" ] && command -v "$WINE" >/dev/null 2>&1; then
                        log "Using Whisky shellenv Wine command: $WINE"
                        launch_runtime "$WINE" "$EXE" "$@"
                    elif command -v wine >/dev/null 2>&1; then
                        log "Using Wine from Whisky shellenv PATH: $(command -v wine)"
                        launch_runtime wine "$EXE" "$@"
                    elif command -v wine64 >/dev/null 2>&1; then
                        log "Using wine64 from Whisky shellenv PATH: $(command -v wine64)"
                        launch_runtime wine64 "$EXE" "$@"
                    else
                        log "Could not find wine binary after applying whisky shellenv; falling back to whisky run."
                        launch_runtime "$WHISKY_CMD" run "$WHISKY_BOTTLE" "$EXE" "$@"
                    fi
                else
                    log "Whisky bottle '$WHISKY_BOTTLE' not found. Creating it now."
                    if "$WHISKY_CMD" create "$WHISKY_BOTTLE"; then
                        log "Whisky bottle created successfully: $WHISKY_BOTTLE"
                        SHELLENV_EXPORTS="$($WHISKY_CMD shellenv "$WHISKY_BOTTLE" 2>> "$LOG_FILE" | grep '^export ' || true)"
                        if [ -n "$SHELLENV_EXPORTS" ]; then
                            eval "$SHELLENV_EXPORTS"
                        fi

                        if [ -n "$WINE" ] && command -v "$WINE" >/dev/null 2>&1; then
                            log "Using Whisky shellenv Wine command: $WINE"
                            launch_runtime "$WINE" "$EXE" "$@"
                        elif command -v wine >/dev/null 2>&1; then
                            log "Using Wine from Whisky shellenv PATH: $(command -v wine)"
                            launch_runtime wine "$EXE" "$@"
                        elif command -v wine64 >/dev/null 2>&1; then
                            log "Using wine64 from Whisky shellenv PATH: $(command -v wine64)"
                            launch_runtime wine64 "$EXE" "$@"
                        else
                            log "Could not find wine binary after creating bottle; falling back to whisky run."
                            launch_runtime "$WHISKY_CMD" run "$WHISKY_BOTTLE" "$EXE" "$@"
                        fi
                    else
                        log "ERROR: Could not create Whisky bottle '$WHISKY_BOTTLE'."
                        log "Try manually: whisky create $WHISKY_BOTTLE"
                        exit 1
                    fi
                fi
            elif command -v mono >/dev/null 2>&1; then
                log "Wine not found - trying Mono fallback: $(command -v mono)"
                launch_runtime mono "$EXE" "$@"
            else
                log "ERROR: Neither Wine nor Mono is available."
                log "Run the guided setup wizard: sh setup-wizard.sh"
                log "Or read SETUP_LINUX.md / SETUP_MAC.md for manual steps."
                exit 1
            fi
            """;

        File.WriteAllText(path, content.Replace("__EXE__", exe), Utf8NoBom);

        // Make it executable on Unix (no-op on Windows)
        TryChmod(path, "755");
        Console.WriteLine($"  [launcher] run.sh written.");
    }

    // ── setup-wizard.sh (Linux/Mac smart setup) ──────────────────────────

    private static void WriteSetupWizardSh(string dir, string exe)
    {
        var path = Path.Combine(dir, "setup-wizard.sh");
        var content = """
            #!/usr/bin/env sh
            # PAIcom Smart Setup Wizard - Linux / macOS
            set -e

            SCRIPT_DIR="$(cd "$(dirname "$0")" && pwd)"
            EXE="$SCRIPT_DIR/__EXE__"
            RUN_SCRIPT="$SCRIPT_DIR/run.sh"
            BUNDLED_WINE="$SCRIPT_DIR/wine/bin/wine"
            ALT_BUNDLED_WINE="$SCRIPT_DIR/runtime/wine/bin/wine"
            WHISKY_WINE="/Applications/Whisky.app/Contents/MacOS/wine"
            WHISKY_BOTTLE="${WHISKY_BOTTLE:-PAIcom}"
            LOG_FILE="$SCRIPT_DIR/setup-wizard.log"

            timestamp() {
                date "+%Y-%m-%d %H:%M:%S"
            }

            log() {
                printf "%s [setup] %s\n" "$(timestamp)" "$*" >> "$LOG_FILE"
                printf "[setup] %s\n" "$*"
            }

            : > "$LOG_FILE"
            log "Setup wizard start"
            log "Script dir: $SCRIPT_DIR"
            log "Target exe: $EXE"
            log "Whisky bottle: $WHISKY_BOTTLE"

            detect_brew_cmd() {
                if command -v brew >/dev/null 2>&1; then
                    command -v brew
                    return 0
                fi
                if [ -x "/opt/homebrew/bin/brew" ]; then
                    echo "/opt/homebrew/bin/brew"
                    return 0
                fi
                if [ -x "/usr/local/bin/brew" ]; then
                    echo "/usr/local/bin/brew"
                    return 0
                fi
                return 1
            }

            detect_whisky_cmd() {
                if command -v whisky >/dev/null 2>&1; then
                    command -v whisky
                    return 0
                fi
                if [ -x "/opt/homebrew/bin/whisky" ]; then
                    echo "/opt/homebrew/bin/whisky"
                    return 0
                fi
                if [ -x "/usr/local/bin/whisky" ]; then
                    echo "/usr/local/bin/whisky"
                    return 0
                fi
                return 1
            }

            if [ -z "$WINEPREFIX" ]; then
                export WINEPREFIX="$SCRIPT_DIR/.wine-prefix"
            fi

            detect_os() {
                uname -s 2>/dev/null || echo "Unknown"
            }

            detect_wine() {
                if [ -x "$ALT_BUNDLED_WINE" ]; then
                    echo "wine:$ALT_BUNDLED_WINE"
                    return 0
                fi
                if [ -x "$BUNDLED_WINE" ]; then
                    echo "wine:$BUNDLED_WINE"
                    return 0
                fi
                if [ -x "$WHISKY_WINE" ]; then
                    echo "wine:$WHISKY_WINE"
                    return 0
                fi
                if command -v wine >/dev/null 2>&1; then
                    echo "wine:$(command -v wine)"
                    return 0
                fi
                if command -v whisky >/dev/null 2>&1; then
                    echo "whisky:$(command -v whisky)"
                    return 0
                fi
                if [ -x "/opt/homebrew/bin/whisky" ]; then
                    echo "whisky:/opt/homebrew/bin/whisky"
                    return 0
                fi
                if [ -x "/usr/local/bin/whisky" ]; then
                    echo "whisky:/usr/local/bin/whisky"
                    return 0
                fi
                return 1
            }

            detect_dotnet_release() {
                if command -v wine64 >/dev/null 2>&1; then
                    wine64 reg query "HKLM\\Software\\Microsoft\\NET Framework Setup\\NDP\\v4\\Full" /v Release 2>/dev/null | awk '/Release/ {print $NF}'
                    return 0
                fi
                if command -v wine >/dev/null 2>&1; then
                    wine reg query "HKLM\\Software\\Microsoft\\NET Framework Setup\\NDP\\v4\\Full" /v Release 2>/dev/null | awk '/Release/ {print $NF}'
                    return 0
                fi
                return 1
            }

            print_header() {
                echo ""
                echo "========================================"
                echo " PAIcom Smart Setup Wizard"
                echo "========================================"
                echo "OS: $(detect_os)"
                echo "App: $EXE"
                echo "WINEPREFIX: $WINEPREFIX"
                echo ""
            }

            run_diagnostics() {
                log "Diagnostics started"
                echo "[diagnostics] Checking environment..."

                if [ -f "$EXE" ]; then
                    echo "[ok] Target executable found."
                    log "Target executable exists"
                else
                    echo "[error] Target executable missing: $EXE"
                    log "Target executable missing"
                fi

                if RUNTIME_PATH="$(detect_wine 2>/dev/null)"; then
                    case "$RUNTIME_PATH" in
                        wine:*)
                            echo "[ok] Wine available: ${RUNTIME_PATH#wine:}"
                            log "Detected runtime: ${RUNTIME_PATH#wine:}"
                            ;;
                        whisky:*)
                            echo "[ok] Whisky available: ${RUNTIME_PATH#whisky:}"
                            log "Detected whisky: ${RUNTIME_PATH#whisky:}"
                            ;;
                        *)
                            echo "[ok] Runtime available: $RUNTIME_PATH"
                            log "Detected runtime: $RUNTIME_PATH"
                            ;;
                    esac
                else
                    echo "[warn] Wine/Whisky not found."
                    log "No Wine/Whisky runtime detected"
                fi

                if command -v mono >/dev/null 2>&1; then
                    echo "[ok] Mono available: $(command -v mono)"
                    log "Mono detected"
                else
                    echo "[info] Mono not found (optional fallback)."
                    log "Mono not detected"
                fi

                if WHISKY_CMD_PATH="$(detect_whisky_cmd 2>/dev/null)"; then
                    SHELLENV_EXPORTS="$($WHISKY_CMD_PATH shellenv "$WHISKY_BOTTLE" | grep '^export ' || true)"
                    if [ -n "$SHELLENV_EXPORTS" ]; then
                        eval "$SHELLENV_EXPORTS"
                    fi

                    DOTNET_RELEASE="$(detect_dotnet_release 2>/dev/null || true)"
                    if [ -n "$DOTNET_RELEASE" ]; then
                        echo "[ok] .NET Framework v4 Full Release: $DOTNET_RELEASE"
                        log ".NET release detected: $DOTNET_RELEASE"
                    else
                        echo "[warn] .NET Framework v4 Full not detected in Whisky bottle."
                        log ".NET release not detected in bottle"
                    fi
                fi

                if [ -x "$RUN_SCRIPT" ]; then
                    echo "[ok] Launcher executable: run.sh"
                    log "run.sh executable"
                else
                    echo "[warn] run.sh is not executable. Fixing now..."
                    log "run.sh not executable - attempting chmod"
                    chmod +x "$RUN_SCRIPT" || true
                fi

                echo "[diagnostics] Complete."
                log "Diagnostics complete"
                echo ""
            }

            install_wine_linux() {
                echo "[setup] Linux Wine installation helper"
                if command -v apt >/dev/null 2>&1; then
                    echo "Detected apt (Ubuntu/Debian)."
                    echo "Will run: sudo dpkg --add-architecture i386 && sudo apt update && sudo apt install -y wine wine32 wine64"
                    printf "Proceed? [y/N]: "
                    read -r ans
                    case "$ans" in
                        y|Y|yes|YES)
                            sudo dpkg --add-architecture i386
                            sudo apt update
                            sudo apt install -y wine wine32 wine64
                            ;;
                        *) echo "Skipped." ;;
                    esac
                    return
                fi

                if command -v dnf >/dev/null 2>&1; then
                    echo "Detected dnf (Fedora)."
                    echo "Will run: sudo dnf install -y wine"
                    printf "Proceed? [y/N]: "
                    read -r ans
                    case "$ans" in
                        y|Y|yes|YES) sudo dnf install -y wine ;;
                        *) echo "Skipped." ;;
                    esac
                    return
                fi

                if command -v pacman >/dev/null 2>&1; then
                    echo "Detected pacman (Arch)."
                    echo "Will run: sudo pacman -S --noconfirm wine"
                    printf "Proceed? [y/N]: "
                    read -r ans
                    case "$ans" in
                        y|Y|yes|YES) sudo pacman -S --noconfirm wine ;;
                        *) echo "Skipped." ;;
                    esac
                    return
                fi

                echo "No supported package manager detected automatically."
                echo "Please check SETUP_LINUX.md for manual installation steps."
            }

            install_wine_macos() {
                echo "[setup] macOS Wine installation helper"

                BREW_CMD=""
                WHISKY_CMD_PATH=""

                if BREW_CMD="$(detect_brew_cmd 2>/dev/null)"; then
                    export PATH="$(dirname "$BREW_CMD"):$PATH"
                fi

                if WHISKY_CMD_PATH="$(detect_whisky_cmd 2>/dev/null)"; then
                    echo "Whisky CLI detected at: $WHISKY_CMD_PATH"
                    if "$WHISKY_CMD_PATH" shellenv "$WHISKY_BOTTLE" >/dev/null 2>&1; then
                        echo "Whisky bottle is ready: $WHISKY_BOTTLE"
                        return
                    fi

                    echo "Whisky bottle '$WHISKY_BOTTLE' is missing."
                    printf "Create bottle '$WHISKY_BOTTLE' now? [Y/n]: "
                    read -r ans
                    case "$ans" in
                        n|N|no|NO)
                            echo "Skipped bottle creation."
                            ;;
                        *)
                            if "$WHISKY_CMD_PATH" create "$WHISKY_BOTTLE"; then
                                echo "[setup] Bottle created: $WHISKY_BOTTLE"
                            else
                                echo "[warn] Failed to create bottle '$WHISKY_BOTTLE'."
                            fi
                            ;;
                    esac
                    return
                fi

                if [ -x "$WHISKY_WINE" ]; then
                    echo "Whisky already appears to be installed at: $WHISKY_WINE"
                    return
                fi

                if BREW_CMD="$(detect_brew_cmd 2>/dev/null)"; then
                    echo "Homebrew detected."
                    echo "Recommended command: brew install --cask whisky"
                    printf "Install now? [y/N]: "
                    read -r ans
                    case "$ans" in
                        y|Y|yes|YES)
                            "$BREW_CMD" install --cask whisky
                            if WHISKY_CMD_PATH="$(detect_whisky_cmd 2>/dev/null)"; then
                                echo "[setup] Whisky installed: $WHISKY_CMD_PATH"
                                echo "[setup] Creating default bottle: $WHISKY_BOTTLE"
                                "$WHISKY_CMD_PATH" create "$WHISKY_BOTTLE" || true
                            fi
                            ;;
                        *) echo "Skipped." ;;
                    esac
                else
                    echo "Homebrew not found."

                    printf "Download and install Homebrew now? [y/N]: "
                    read -r ans
                    case "$ans" in
                        y|Y|yes|YES)
                            if command -v curl >/dev/null 2>&1; then
                                echo "[setup] Installing Homebrew (official installer)..."
                                /bin/bash -c "$(curl -fsSL https://raw.githubusercontent.com/Homebrew/install/HEAD/install.sh)"

                                if [ -x /opt/homebrew/bin/brew ]; then
                                    export PATH="/opt/homebrew/bin:$PATH"
                                elif [ -x /usr/local/bin/brew ]; then
                                    export PATH="/usr/local/bin:$PATH"
                                fi

                                if BREW_CMD="$(detect_brew_cmd 2>/dev/null)"; then
                                    echo "[setup] Homebrew installed successfully."
                                    echo "[setup] Installing Whisky..."
                                    "$BREW_CMD" install --cask whisky

                                    if WHISKY_CMD_PATH="$(detect_whisky_cmd 2>/dev/null)"; then
                                        echo "[setup] Creating default bottle: $WHISKY_BOTTLE"
                                        "$WHISKY_CMD_PATH" create "$WHISKY_BOTTLE" || true
                                    fi
                                else
                                    echo "[warn] Homebrew install finished, but brew was not found in PATH."
                                    echo "[info] Open a new terminal, then run: brew install --cask whisky"
                                fi
                            else
                                echo "[error] curl is not available. Cannot auto-download Homebrew installer."
                                echo "[info] Install Homebrew manually from: https://brew.sh"
                            fi
                            ;;
                        *)
                            echo "Skipped Homebrew auto-install."
                            echo "Install Homebrew from https://brew.sh then run: brew install --cask whisky"
                            echo "Or install Whisky manually from: https://github.com/Whisky-App/Whisky/releases"
                            ;;
                    esac
                fi
            }

            install_wine_guided() {
                OS_NAME="$(detect_os)"
                case "$OS_NAME" in
                    Darwin) install_wine_macos ;;
                    Linux) install_wine_linux ;;
                    *)
                        echo "Unsupported OS for guided installer: $OS_NAME"
                        echo "Use manual setup instructions in SETUP_LINUX.md / SETUP_MAC.md"
                        ;;
                esac
            }

            install_dotnet48_macos() {
                echo "[setup] macOS .NET Framework 4.8 helper (Whisky bottle)"

                WHISKY_CMD_PATH=""
                BREW_CMD=""
                WINETRICKS_CMD=""

                if ! WHISKY_CMD_PATH="$(detect_whisky_cmd 2>/dev/null)"; then
                    echo "[error] Whisky CLI not found. Install Whisky first."
                    return
                fi

                if ! "$WHISKY_CMD_PATH" shellenv "$WHISKY_BOTTLE" >/dev/null 2>&1; then
                    echo "[setup] Bottle '$WHISKY_BOTTLE' is missing. Creating it..."
                    if ! "$WHISKY_CMD_PATH" create "$WHISKY_BOTTLE"; then
                        echo "[error] Failed to create bottle '$WHISKY_BOTTLE'."
                        return
                    fi
                fi

                if ! BREW_CMD="$(detect_brew_cmd 2>/dev/null)"; then
                    echo "[error] Homebrew is required for automated winetricks install."
                    echo "[info] Install Homebrew first, then retry this option."
                    return
                fi

                if command -v winetricks >/dev/null 2>&1; then
                    WINETRICKS_CMD="$(command -v winetricks)"
                elif [ -x "/opt/homebrew/bin/winetricks" ]; then
                    WINETRICKS_CMD="/opt/homebrew/bin/winetricks"
                elif [ -x "/usr/local/bin/winetricks" ]; then
                    WINETRICKS_CMD="/usr/local/bin/winetricks"
                fi

                if [ -z "$WINETRICKS_CMD" ]; then
                    echo "[setup] Installing winetricks and cabextract via Homebrew..."
                    "$BREW_CMD" install winetricks cabextract

                    if command -v winetricks >/dev/null 2>&1; then
                        WINETRICKS_CMD="$(command -v winetricks)"
                    elif [ -x "/opt/homebrew/bin/winetricks" ]; then
                        WINETRICKS_CMD="/opt/homebrew/bin/winetricks"
                    elif [ -x "/usr/local/bin/winetricks" ]; then
                        WINETRICKS_CMD="/usr/local/bin/winetricks"
                    fi
                fi

                if [ -z "$WINETRICKS_CMD" ]; then
                    echo "[error] winetricks not found after install attempt."
                    return
                fi

                SHELLENV_EXPORTS="$($WHISKY_CMD_PATH shellenv "$WHISKY_BOTTLE" | grep '^export ' || true)"
                if [ -n "$SHELLENV_EXPORTS" ]; then
                    eval "$SHELLENV_EXPORTS"
                fi

                echo "[setup] Initializing Wine prefix for bottle '$WHISKY_BOTTLE'..."
                if command -v wine64 >/dev/null 2>&1; then
                    wine64 wineboot -u || true
                    wineserver -w || true
                elif command -v wine >/dev/null 2>&1; then
                    wine wineboot -u || true
                    wineserver -w || true
                fi

                DOTNET_RELEASE="$(detect_dotnet_release 2>/dev/null || true)"
                if [ -n "$DOTNET_RELEASE" ]; then
                    echo "[setup] Existing .NET Framework release detected: $DOTNET_RELEASE"
                    return
                fi

                echo "[setup] Installing dotnet48 in bottle '$WHISKY_BOTTLE'..."
                echo "[setup] This may take several minutes and can open Wine dialogs."
                set +e
                "$WINETRICKS_CMD" -q remove_mono || true
                WINEDLLOVERRIDES="mscoree,mshtml=" "$WINETRICKS_CMD" -q --force dotnet48
                WT_EXIT=$?
                set -e

                DOTNET_RELEASE="$(detect_dotnet_release 2>/dev/null || true)"
                if [ -n "$DOTNET_RELEASE" ]; then
                    echo "[setup] dotnet48 installation completed. Release=$DOTNET_RELEASE"
                    return
                fi

                echo "[warn] dotnet48 did not verify (winetricks exit: $WT_EXIT). Trying dotnet472 fallback..."
                set +e
                WINEDLLOVERRIDES="mscoree,mshtml=" "$WINETRICKS_CMD" -q --force dotnet472
                WT2_EXIT=$?
                set -e

                DOTNET_RELEASE="$(detect_dotnet_release 2>/dev/null || true)"
                if [ -n "$DOTNET_RELEASE" ]; then
                    echo "[setup] dotnet472 fallback succeeded. Release=$DOTNET_RELEASE"
                    return
                fi

                echo "[error] .NET Framework installation could not be verified in bottle '$WHISKY_BOTTLE'."
                echo "[info] Try launching Whisky.app once, open bottle '$WHISKY_BOTTLE', then retry option 4."
                echo "[info] Last winetricks exit codes: dotnet48=$WT_EXIT, dotnet472=$WT2_EXIT"
            }

            install_dotnet48_guided() {
                OS_NAME="$(detect_os)"
                case "$OS_NAME" in
                    Darwin) install_dotnet48_macos ;;
                    *)
                        echo "This helper currently targets macOS + Whisky only."
                        ;;
                esac
            }

            launch_now() {
                echo "[setup] Launching app..."
                exec sh "$RUN_SCRIPT"
            }

            if [ "x$1" = "x--diagnose" ]; then
                print_header
                run_diagnostics
                exit 0
            fi

            print_header
            run_diagnostics

            while true; do
                echo "Choose an option:"
                echo "  1) Install Wine (guided)"
                echo "  2) Re-run diagnostics"
                echo "  3) Launch now"
                echo "  4) Install .NET Framework 4.8 (Whisky bottle)"
                echo "  5) Exit"
                printf "Selection [1-5]: "
                read -r choice

                case "$choice" in
                    1) log "Menu option selected: Install Wine (guided)"; install_wine_guided ;;
                    2) log "Menu option selected: Re-run diagnostics"; run_diagnostics ;;
                    3) log "Menu option selected: Launch now"; launch_now ;;
                    4) log "Menu option selected: Install .NET Framework 4.8"; install_dotnet48_guided ;;
                    5) log "Menu option selected: Exit"; echo "Exiting setup wizard."; exit 0 ;;
                    *) echo "Invalid option. Please choose 1, 2, 3, 4, or 5." ;;
                esac
                echo ""
            done

            """;

        File.WriteAllText(path, content.Replace("__EXE__", exe), Utf8NoBom);

        // Make it executable on Unix (no-op on Windows)
        TryChmod(path, "755");
        Console.WriteLine($"  [launcher] setup-wizard.sh written.");
    }

    // ── setup.command (Mac double-click setup) ───────────────────────────

    private static void WriteSetupCommand(string dir)
    {
        var path = Path.Combine(dir, "setup.command");
        File.WriteAllText(path, $"""
            #!/usr/bin/env sh
            # Mac Finder double-click setup launcher - opens the setup wizard
            SCRIPT_DIR="$(cd "$(dirname "$0")" && pwd)"
            sh "$SCRIPT_DIR/setup-wizard.sh" "$@"
            """, Utf8NoBom);

        TryChmod(path, "755");
        Console.WriteLine($"  [launcher] setup.command written.");
    }

    // ── launch.command (Mac double-click) ─────────────────────────────────

    private static void WriteLaunchCommand(string dir)
    {
        var path = Path.Combine(dir, "launch.command");
        File.WriteAllText(path, $"""
            #!/usr/bin/env sh
            # Mac Finder double-click launcher — delegates to run.sh
            SCRIPT_DIR="$(cd "$(dirname "$0")" && pwd)"
            sh "$SCRIPT_DIR/run.sh" "$@"
            """, Utf8NoBom);

        TryChmod(path, "755");
        Console.WriteLine($"  [launcher] launch.command written.");
    }

    // ── run.bat ───────────────────────────────────────────────────────────

    private static void WriteRunBat(string dir, string exe)
    {
        var path = Path.Combine(dir, "run.bat");
        // Use Windows-style line endings for .bat compatibility
        var content = "@echo off\r\n" +
                     "REM Set OpenWakeWord environment variables (can be overridden by user)\r\n" +
                     "if not defined PAICOM_OWW_THRESHOLD set PAICOM_OWW_THRESHOLD=0.7\r\n" +
                     "if not defined PAICOM_OWW_LOCK_MS set PAICOM_OWW_LOCK_MS=3000\r\n" +
                     "if not defined PAICOM_OWW_AUDIO_CHUNK_SIZE set PAICOM_OWW_AUDIO_CHUNK_SIZE=1024\r\n" +
                     "if not defined PAICOM_OWW_INFERENCE_THREAD_SCALE set PAICOM_OWW_INFERENCE_THREAD_SCALE=1.0\r\n" +
                     "if not defined PAICOM_OWW_VERBOSE_LOG set PAICOM_OWW_VERBOSE_LOG=false\r\n" +
                     "\r\n" +
                     "start \"\" \"%~dp0" + exe + "\" %*\r\n";
        File.WriteAllText(path, content, System.Text.Encoding.ASCII);
        Console.WriteLine($"  [launcher] run.bat written.");
    }

    // ── SETUP_LINUX.md ────────────────────────────────────────────────────

    private static void WriteSetupLinux(string dir, string exe)
    {
        var path = Path.Combine(dir, "SETUP_LINUX.md");
        File.WriteAllText(path, $"""
            # Running PAIcom on Linux

            PAIcom is a Windows application. Use **Wine** to run it natively on Linux.

            ## Recommended: Smart Setup Wizard

            Run:

            ```sh
            sh setup-wizard.sh
            ```

            The wizard can:
            - check your environment
            - install Wine using your distro package manager
            - launch PAIcom after setup

            ## Quick Start

            ```sh
            sh run.sh
            ```

            ## Installing Wine

            ### Ubuntu / Debian
            ```sh
            sudo dpkg --add-architecture i386
            sudo apt update
            sudo apt install wine wine32 wine64
            ```

            ### Fedora
            ```sh
            sudo dnf install wine
            ```

            ### Arch Linux
            ```sh
            sudo pacman -S wine
            ```

            ## Audio Setup (Required for Microphone Input)

            PAIcom uses Windows audio (WinMM/WASAPI) through Wine. You need PulseAudio or
            PipeWire configured as Wine's audio backend.

            1. Open Wine configuration:
               ```sh
               winecfg
               ```
            2. Go to the **Audio** tab.
            3. Set **Driver** to `PulseAudio` (or `PipeWire` if available).
            4. Click OK and restart PAIcom.

            If microphone capture fails under Wine, voice recognition may not start.
            Verify microphone routing in `winecfg` and review `launcher-runtime.log`
            for `[compat][speech]` diagnostics.

            ## Vosk Model Auto-Download

            On first launch, PAIcom downloads a ~40 MB English speech model to:
            ```
            ~/.paicom/models/vosk-model-small-en-us-0.15/
            ```
            An internet connection is required the first time only.

            To skip microphone input entirely, set the environment variable before running:
            ```sh
            PAICOM_NO_STT=1 sh run.sh
            ```

            ## Logs

            Generated launchers now write diagnostics to:
            ```
            ./launcher.log
            ./launcher-runtime.log
            ./setup-wizard.log
            ```

            Voice compatibility diagnostics are written to `launcher-runtime.log`
            with the `[compat][speech]` prefix.
            """, Utf8NoBom);

        Console.WriteLine($"  [launcher] SETUP_LINUX.md written.");
    }

    // ── SETUP_MAC.md ──────────────────────────────────────────────────────

    private static void WriteSetupMac(string dir, string exe)
    {
        var path = Path.Combine(dir, "SETUP_MAC.md");
        File.WriteAllText(path, $"""
            # Running PAIcom on macOS

            PAIcom is a Windows application. Use **Wine** (or a Wine wrapper) to run it.

            ## Recommended: Smart Setup Wizard

            Double-click `setup.command`, or run:

            ```sh
            sh setup-wizard.sh
            ```

            The wizard can:
            - run diagnostics
            - download and install Homebrew if missing
            - guide Wine installation (Whisky/Homebrew path)
            - launch PAIcom after setup

            ## ⚠️ Security Warning — Unsigned Application

            **CrossPlatformPatcher and the generated launchers are distributed without an
            Apple Developer ID signature.** This is intentional. macOS may display one or
            more of the following security prompts before first run:

            - *"CrossPlatformPatcher cannot be opened because it is from an unidentified developer."*
            - *"macOS cannot verify the developer of this app."*
            - A Gatekeeper dialog blocking launch.

            **These warnings are expected for unsigned apps and do not indicate malicious
            behaviour.** Only run files you trust and obtained from the official source.

            ### Resolution A — "Open Anyway" via System Settings (recommended)

            1. Attempt to open the file (double-click `launch.command` or run `./CrossPlatformPatcher-M-Arm`).
            2. macOS blocks the launch and shows a dialog — click **OK** to dismiss it.
            3. Open **System Settings → Privacy & Security**.
            4. Scroll to the **Security** section. You will see:
               *"CrossPlatformPatcher was blocked from use because it is not from an identified developer."*
            5. Click **Open Anyway**.
            6. Authenticate with your password or Touch ID when prompted.
            7. Re-run the app — macOS will show one final confirmation dialog; click **Open**.

            ### Resolution B — Remove the quarantine attribute (Terminal)

            If you downloaded the release archive, macOS attaches a `com.apple.quarantine`
            extended attribute to every file in the archive. Remove it with:

            ```sh
            # Remove quarantine from the entire extracted folder (replace the path as needed):
            xattr -cr /path/to/CrossPlatformPatcher-M-Arm

            # Or target individual files:
            xattr -d com.apple.quarantine /path/to/CrossPlatformPatcher
            xattr -d com.apple.quarantine /path/to/launch.command
            xattr -d com.apple.quarantine /path/to/run.sh
            ```

            After removing the quarantine attribute, launch as normal.

            ## Quick Start

            Double-click `launch.command`, or open Terminal and run:
            ```sh
            sh run.sh
            ```

            If execute permission was lost during extraction:
            ```sh
            chmod +x launch.command run.sh
            ```

            ## Installing Wine

            ### Option A — Whisky (free, easy, Apple Silicon + Intel)
            Download from https://github.com/Whisky-App/Whisky/releases
            - Supports both Intel (x86_64) and Apple Silicon (arm64) Macs.
                        - After installing, use `run.sh` or `launch.command`; the launcher can use
                            either Whisky's Wine runtime binary or Whisky CLI.

            ## Isolated Runtime Prefix

            By default, generated launchers set:

            ```sh
            WINEPREFIX="./.wine-prefix"
            ```

            This keeps app/runtime state isolated to this folder and avoids reusing a
            global Wine prefix unless you explicitly override `WINEPREFIX`.

            ### Option B — Homebrew (Intel Macs only)
            ```sh
            brew install --cask wine-stable
            ```

            ### Option C — CrossOver (paid, best compatibility)
            https://www.codeweavers.com/crossover

            ## Apple Silicon (M1/M2/M3)

            Apple Silicon Macs require a Rosetta-compatible Wine build. **Whisky** handles
            this automatically. If using Homebrew, ensure Homebrew is running under Rosetta:
            ```sh
            arch -x86_64 brew install --cask wine-stable
            ```

            ## Audio Setup

            PAIcom uses Windows audio (WinMM) via Wine. On macOS, Wine routes audio through
            CoreAudio automatically in most Wine builds — no extra configuration needed.

            If microphone input fails, review `launcher-runtime.log` for
            `[compat][speech]` diagnostics.

            ## Vosk Model Auto-Download

            On first launch, PAIcom downloads a ~40 MB English speech model to:
            ```
            ~/.paicom/models/vosk-model-small-en-us-0.15/
            ```
            An internet connection is required the first time only.

            ## Logs

            Generated launchers now write diagnostics to:
            ```
            ./launcher.log
            ./launcher-runtime.log
            ./setup-wizard.log
            ```

            Voice compatibility diagnostics are written to `launcher-runtime.log`
            with the `[compat][speech]` prefix.
            """, Utf8NoBom);

        Console.WriteLine($"  [launcher] SETUP_MAC.md written.");
    }

    // ── Helpers ───────────────────────────────────────────────────────────

    private static void TryChmod(string path, string mode)
    {
        // chmod is a no-op on Windows but important when running the patcher on Linux/Mac
        try
        {
            var proc = new System.Diagnostics.Process
            {
                StartInfo = new System.Diagnostics.ProcessStartInfo("chmod", $"{mode} \"{path}\"")
                {
                    UseShellExecute = false,
                    RedirectStandardOutput = true,
                    RedirectStandardError  = true,
                    CreateNoWindow = true,
                }
            };
            proc.Start();
            proc.WaitForExit(2000);
        }
        catch
        {
            // Expected on Windows – silently ignore
        }
    }
}

