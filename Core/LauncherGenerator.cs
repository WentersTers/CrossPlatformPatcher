namespace CrossPlatformPatcher.Core;

/// <summary>
/// Writes OS-specific launcher scripts alongside the patched exe so end users
/// can run it on Linux and macOS (via Wine/Mono) with a simple double-click or
/// shell command.
/// 
/// Generated files:
///   run.sh         — Linux/Mac shell launcher (Wine → Mono → error)
///   launch.command — Mac Finder double-click wrapper (wraps run.sh)
///   run.bat        — Windows batch launcher (direct exe start)
///   SETUP_LINUX.md — Wine + audio setup instructions for Linux
///   SETUP_MAC.md   — Wine + audio setup instructions for macOS
/// </summary>
public static class LauncherGenerator
{
    public static void WriteAll(string outputDir, string exeFileName)
    {
        Directory.CreateDirectory(outputDir);

        WriteRunSh(outputDir, exeFileName);
        WriteLaunchCommand(outputDir);
        WriteRunBat(outputDir, exeFileName);
        WriteSetupLinux(outputDir, exeFileName);
        WriteSetupMac(outputDir, exeFileName);

        Console.WriteLine($"  [launcher] Generated launcher scripts in: {outputDir}");
    }

    // ── run.sh ────────────────────────────────────────────────────────────

    private static void WriteRunSh(string dir, string exe)
    {
        var path = Path.Combine(dir, "run.sh");
        File.WriteAllText(path, $"""
            #!/usr/bin/env sh
            # PAIcom Launcher — Linux / macOS
            # Tries Wine (recommended), then Mono.
            set -e
            SCRIPT_DIR="$(cd "$(dirname "$0")" && pwd)"
            EXE="$SCRIPT_DIR/{exe}"

            if command -v wine >/dev/null 2>&1; then
                echo "[launcher] Starting with Wine …"
                exec wine "$EXE" "$@"
            elif command -v mono >/dev/null 2>&1; then
                echo "[launcher] Wine not found – trying Mono (limited audio support) …"
                exec mono "$EXE" "$@"
            else
                echo ""
                echo "ERROR: Neither Wine nor Mono is installed."
                echo ""
                echo "To install Wine on Ubuntu/Debian:"
                echo "  sudo dpkg --add-architecture i386"
                echo "  sudo apt update && sudo apt install wine"
                echo ""
                echo "To install Wine on macOS (via Homebrew):"
                echo "  brew install --cask whisky"
                echo ""
                echo "See SETUP_LINUX.md or SETUP_MAC.md for full instructions."
                exit 1
            fi
            """, System.Text.Encoding.UTF8);

        // Make it executable on Unix (no-op on Windows)
        TryChmod(path, "755");
        Console.WriteLine($"  [launcher] run.sh written.");
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
            """, System.Text.Encoding.UTF8);

        TryChmod(path, "755");
        Console.WriteLine($"  [launcher] launch.command written.");
    }

    // ── run.bat ───────────────────────────────────────────────────────────

    private static void WriteRunBat(string dir, string exe)
    {
        var path = Path.Combine(dir, "run.bat");
        // Use Windows-style line endings for .bat compatibility
        var content = $"@echo off\r\nstart \"\" \"%~dp0{exe}\" %*\r\n";
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

            If you see `[VOSK] NAudio startup failed` in `hotswap.log`, Wine audio is not
            configured. The speech recogniser will fall back to file-based input
            (`command_input.txt`) until audio is working.

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
            """, System.Text.Encoding.UTF8);

        Console.WriteLine($"  [launcher] SETUP_LINUX.md written.");
    }

    // ── SETUP_MAC.md ──────────────────────────────────────────────────────

    private static void WriteSetupMac(string dir, string exe)
    {
        var path = Path.Combine(dir, "SETUP_MAC.md");
        File.WriteAllText(path, $"""
            # Running PAIcom on macOS

            PAIcom is a Windows application. Use **Wine** (or a Wine wrapper) to run it.

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
            - After installing, use `run.sh` or `launch.command`; Whisky provides the
              `wine` command at `/Applications/Whisky.app/Contents/MacOS/wine`.

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

            If microphone input fails, check `hotswap.log` for `[VOSK]` errors.
            To skip speech input: `PAICOM_NO_STT=1 sh run.sh`

            ## Vosk Model Auto-Download

            On first launch, PAIcom downloads a ~40 MB English speech model to:
            ```
            ~/.paicom/models/vosk-model-small-en-us-0.15/
            ```
            An internet connection is required the first time only.
            """, System.Text.Encoding.UTF8);

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

