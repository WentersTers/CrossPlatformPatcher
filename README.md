# CrossPlatformPatcher - Cross-Platform PAIcom Patcher

This is an **isolated, standalone build** of the cross-platform PAIcom patch injector. It contains all features for replacing the Windows-only `System.Speech` speech recognizer with **Vosk** (offline, cross-platform speech-to-text).

The patcher itself runs on **Windows, Linux, macOS** (Intel and Apple Silicon) and outputs a patched PAIcom.exe that also runs on all those platforms via Wine/Mono.

> **macOS users:** This tool is distributed without an Apple Developer ID signature.
> macOS may show a security or "unidentified developer" warning on first run — this is
> expected. See [macOS: Gatekeeper / "unidentified developer" warning](#macos-gatekeeper--unidentified-developer-warning)
> in the Troubleshooting section for exact resolution steps.

## Quick Start

### Build the Patcher (All Platforms)

**Windows:**
```cmd
dotnet publish CrossPlatformPatcher.csproj -r win-x64 -c Release --self-contained true -p:PublishSingleFile=true -o publish/win
```

**Linux:**
```sh
dotnet publish CrossPlatformPatcher.csproj -r linux-x64 -c Release --self-contained true -p:PublishSingleFile=true -o publish/linux
```

**macOS (Intel):**
```sh
dotnet publish CrossPlatformPatcher.csproj -r osx-x64 -c Release --self-contained true -p:PublishSingleFile=true -o publish/osx-x64
```

**macOS (Apple Silicon):**
```sh
dotnet publish CrossPlatformPatcher.csproj -r osx-arm64 -c Release --self-contained true -p:PublishSingleFile=true -o publish/osx-arm64
```

Or use the included one-command script:

**Windows:**
```cmd
publish-all.bat
```

**Linux/Mac:**
```sh
sh publish-all.sh
```

For an end-to-end build, publish, patch, and launch flow from the repo root on macOS or Linux, use:

```sh
./build-patch-and-launch.sh
```

The wrapper script supports additional flags:
- `--rid <runtime-identifier>` (override auto-detected target)
- `--verbose` (print each invoked command)
- `--show-build-output` (don’t suppress `dotnet` output)
- `--no-launch` (build/publish/patch only, do not run setup-wizard)
- `--build-log <file>` (save output to a log file)

If you want to force a specific runtime identifier, pass `--rid`:

```sh
./build-patch-and-launch.sh --rid osx-arm64
```

This script:
- builds the patcher in Release
- publishes a self-contained OS-specific patcher
- copies the published patcher into `PAIcom_Player_Folder/`
- patches `PAIcom.exe` to `PAIcom_patched.exe`
- runs `PAIcom_Player_Folder/launch.command`

Build, patch, and launcher logs stream to the terminal that started the script. Press `Ctrl+C` to stop the wrapper and its child process.

> **Note:** `publish-all.sh` / `publish-all.bat` publish `CrossPlatformPatcher.csproj` for
> all four runtime identifiers into `publish/CrossPlatformPatcher/<RID>/` with the following naming scheme:
> - Windows x64: `CrossPlatformPatcher-W-x64.exe`
> - Linux x64: `CrossPlatformPatcher-L-x64`
> - macOS x64: `CrossPlatformPatcher-M-x64`
> - macOS ARM64: `CrossPlatformPatcher-M-Arm`

### Use the Patcher

1. Get your own copy of `PAIcom.exe` (purchased from Steam)
2. Run the patcher:
   ```
   CrossPlatformPatcher PAIcom.exe --out PAIcom_patched.exe
   ```
3. The patcher generates:
   - `PAIcom_patched.exe` — the patched game
   - `run.sh` / `run.bat` / `launch.command` — OS-specific launchers
   - `setup-wizard.sh` / `setup.command` — guided smart setup wizard (Linux/macOS)
   - `SETUP_LINUX.md` / `SETUP_MAC.md` — setup instructions

4. To run the patched game:
   - **Windows:** Double-click `PAIcom_patched.exe` or `run.bat`
   - **Linux:** Run `sh setup-wizard.sh` once, then `sh run.sh`
   - **macOS:** Double-click `setup.command` once, then `launch.command`

On macOS, the setup wizard can download/install Homebrew when missing, then install Whisky.

## What's Inside

### Key Components

| File/Folder | Purpose |
|---|---|
| `Core/` | Patch injection logic |
| `Core/ReferenceAssemblyResolver.cs` | Cross-platform .NET FW 4.8 ref resolution |
| `Core/VoskResourceEmbedder.cs` | Embeds Vosk libs into the patched exe |
| `Core/LauncherGenerator.cs` | Creates OS-specific launcher scripts |
| `Core/SpeechCompatibilityPatcher.cs` | Adds compatibility wrappers/logging for System.Speech paths |
| `Core/NativeLibraries/` | Vosk native binaries (Win, Linux, macOS) |
| `Core/ManagedLibraries/` | Vosk & NAudio managed wrappers |
| `publish-all.bat` / `publish-all.sh` | Multi-platform publish script |

### Bundled Dependencies

All dependencies are **embedded** in the final binary (e.g., `CrossPlatformPatcher-W-x64.exe`, `CrossPlatformPatcher-L-x64`, or `CrossPlatformPatcher-M-Arm`):

- **Vosk 0.3.38** (cross-platform speech recognition)
  - Native libs for Windows x64, Linux x64/ARM64, macOS universal
  - GCC runtime companions (libgcc, libstdc++, libpthread) for Windows
- **NAudio 2.2.1** (microphone input)
- **Newtonsoft.Json 13.0.3** (JSON parsing)
- **.NET Framework 4.8 reference assemblies** (for on-the-fly IL compilation)

**Result:** You can build and run the patcher on ANY platform without installing dependencies locally.

## Features

### Vosk Speech Recognition (Cross-Platform)
- Offline speech-to-text (no internet required after first run)
- Supports Windows (WinMM), Linux (ALSA/PulseAudio via Wine), macOS (CoreAudio)
- Automatic model download on first run (~40 MB)
- Falls back to file-based command dispatch if audio unavailable

### Compatibility Layer (Wine/Mono)
- Windows users: run `PAIcom_patched.exe` directly
- Linux/Mac users: Wine redirects Windows API calls; falls back to Mono
- Generated launchers automatically pick Wine or Mono

### Fallback Strategies
1. **Primary app behavior** (original voice pipeline)
2. **Compatibility wrappers** (prevent startup crashes on unavailable APIs)
3. **Launcher diagnostics** (`launcher-runtime.log` with `[compat][speech]` lines)

## Configuration

### Environment Variables

```bash
# Skip microphone input entirely (use file-based dispatch only)
PAICOM_NO_STT=1 ./run.sh

# Verbose logging (includes Vosk diagnostics)
# (Edit SETUP_LINUX.md for Wine configuration)
```

### File-Based Commands

If microphone recognition is unavailable under Wine, use `launcher-runtime.log`
to inspect `[compat][speech]` diagnostics and verify the active speech path.

## Differences from Original PAIcomPatcher

The original `PAIcomPatcher` (in the parent folder) is **Windows-only**:
- Uses native Windows `System.Speech.Recognition` APIs
- Requires .NET Framework 4.8 installed locally
- Only builds as `win-x64`

**CrossPlatformPatcher** (this folder) is **fully cross-platform**:
- Uses Vosk (portable, offline STT) instead of System.Speech
- Embeds .NET FW 4.8 refs as NuGet package (works on any OS)
- Publishes for win-x64, linux-x64, osx-x64, osx-arm64
- Generated output automatically works on Wine/Mono for non-Windows users

## What the Patched Exe Contains

**Important:** The patched `PAIcom.exe` is **100% the user's original file** with:
- Compatibility IL rewrites (guarding fragile `Process.Start` and `System.Speech` call paths)
- Diagnostics helper methods added for speech-path logging

**No PAIcom source code or assets are included or distributed.**

## Development Notes

### Rebuild And Verify

Use the checked-in patcher sources to rebuild the project from scratch:

```sh
dotnet build CrossPlatformPatcher.csproj -c Release
dotnet test CrossPlatformPatcher.Tests/CrossPlatformPatcher.Tests.csproj -c Release
```

If you are preparing release artifacts or validating platform-specific launchers, republish with the appropriate runtime identifier(s):

```sh
dotnet publish CrossPlatformPatcher.csproj -r win-x64 -c Release --self-contained true -p:PublishSingleFile=true -o publish/win
dotnet publish CrossPlatformPatcher.csproj -r linux-x64 -c Release --self-contained true -p:PublishSingleFile=true -o publish/linux
dotnet publish CrossPlatformPatcher.csproj -r osx-x64 -c Release --self-contained true -p:PublishSingleFile=true -o publish/osx-x64
dotnet publish CrossPlatformPatcher.csproj -r osx-arm64 -c Release --self-contained true -p:PublishSingleFile=true -o publish/osx-arm64
```

Keep original PAIcom binaries and any reverse-engineered source outside version control. The repository should only contain the patcher, tests, docs, and build inputs needed to reproduce the patcher itself.

### Adding Vosk Dependency Updates

To update Vosk or NAudio versions:

1. Create a temp .NET 4.8 project:
   ```sh
   mkdir tmp && cd tmp
   dotnet new console -f net48
   dotnet add package Vosk --version X.Y.Z
   dotnet add package NAudio --version X.Y.Z
   dotnet add package Newtonsoft.Json --version X.Y.Z
   dotnet publish -r win-x64 --self-contained false -o out
   ```

2. Copy binaries:
   ```sh
   cp out/Vosk.dll                ../Core/ManagedLibraries/
   cp out/libvosk.dll             ../Core/NativeLibraries/vosk-win-x64.dll
   cp out/libgcc_s_seh-1.dll      ../Core/NativeLibraries/vosk-win-gcc.dll
   # ... etc for other natives
   ```

3. Update version strings in `Core/NativeLibraries/README.md`

4. Rebuild:
   ```sh
   dotnet build CrossPlatformPatcher.csproj -c Release
   ```

## Troubleshooting

### Build Error: "Could not find Vosk"

The `Core/NativeLibraries/` and `Core/ManagedLibraries/` folders are expected to contain the binaries. 
The build will skip missing files gracefully, but the patcher will have reduced functionality without them.

See `Core/NativeLibraries/README.md` and `Core/ManagedLibraries/README.md` for setup.

### Linux: Wine Audio Not Detected

Check `launcher-runtime.log` in the patched exe's directory for `[compat][speech]`
messages and exceptions. Wine audio backend still needs configuration.

### macOS: "wine not found"

Install Whisky (free) or CrossOver. Whisky provides a `wine` command automatically.

### macOS: Gatekeeper / "unidentified developer" warning

CrossPlatformPatcher is distributed **without an Apple Developer ID signature** by choice
(no notarization, no ad-hoc signing). macOS may block first launch with a dialog such as:

> *"CrossPlatformPatcher cannot be opened because it is from an unidentified developer."*

**This is expected and does not indicate malicious behaviour.** To run anyway:

**Option A — Open Anyway (System Settings)**
1. Try to open the blocked file — click **OK** to dismiss the initial dialog.
2. Open **System Settings → Privacy & Security**.
3. In the **Security** section, click **Open Anyway** next to the blocked app.
4. Authenticate and click **Open** in the confirmation dialog.

**Option B — Remove quarantine attribute (Terminal)**

If you downloaded a release archive, every extracted file carries a
`com.apple.quarantine` extended attribute. Remove it with:

```sh
# Recursively clear the entire extracted folder:
xattr -cr /path/to/CrossPlatformPatcher-M-Arm

# Or remove from individual files:
xattr -d com.apple.quarantine /path/to/CrossPlatformPatcher
xattr -d com.apple.quarantine /path/to/launch.command
xattr -d com.apple.quarantine /path/to/run.sh
```

After clearing quarantine, launch as normal. Refer to the generated `SETUP_MAC.md` for
the full step-by-step walkthrough.

## OpenWakeWord Integration (Wake Word Detection)

### Overview

**OpenWakeWord** is a **custom wake word detection system** integrated into CrossPlatformPatcher. It detects a specific wake phrase (e.g., *"Hey PAIcom"*) in real-time audio before processing speech-to-text with Vosk.

**Key Benefits:**
- **Low-latency detection** (~50–200ms per audio chunk on background thread)
- **Zero blocking** on audio capture callbacks (inference runs asynchronously)
- **Custom model** (hey_pie_com.quant.onnx) — optimized for your wake phrase
- **Cross-platform** — Windows, Linux, macOS (via Wine)
- **Tunable thresholds** — adjust sensitivity via CLI or environment variables
- **Hard lock** — prevents duplicate detections within a 3-second window

### Architecture

```
Audio Input (NAudio/System.Audio)
  ↓
[Audio Callback Thread] — fast, non-blocking (queues audio, checks lock state)
  ↓
[Background ThreadPool] — long-running inference (~50–200ms per chunk)
  ↓
ONNX Model Runtime (hey_pie_com.quant.onnx)
  ↓
[Confidence Score] ≥ threshold? → [Hard Lock 3000ms] → [Vosk STT]
```

### CLI Configuration

When patching, use these flags to customize OpenWakeWord settings:

```bash
# Patch with custom OWW settings
CrossPlatformPatcher PAIcom.exe \
  --oww-threshold 0.7 \
  --oww-lock-ms 3000 \
  --oww-audio-chunk-size 1024 \
  --oww-inference-thread-scale 1.0 \
  --oww-verbose-log

# Explanation of each flag:
# --oww-threshold <0.0–1.0>
#   Confidence threshold for wake word detection (default: 0.7).
#   Lower = more sensitive (more false positives).
#   Higher = less sensitive (more false negatives).
#   Range: [0.0, 1.0]
#
# --oww-lock-ms <milliseconds>
#   Hard lock duration in milliseconds (default: 3000 = 3 seconds).
#   Prevents back-queuing and duplicate detections.
#   Covers typical command recognition + safety margin.
#   Range: [100, 10000]
#
# --oww-audio-chunk-size <samples>
#   Audio chunk size in samples (default: 1024).
#   At 16 kHz, 1024 samples ≈ 64ms of audio.
#   Larger = fewer inference calls, higher latency.
#   Range: [128, 8192]
#
# --oww-inference-thread-scale <0.5–2.0>
#   ThreadPool scaling factor for inference workers (default: 1.0).
#   0.5 = single low-priority thread; 2.0 = multi-threaded, aggressive.
#   Range: [0.25, 3.0]
#
# --oww-verbose-log
#   Enable detailed [oww] logging to launcher-runtime.log.
#   Shows confidence scores for every audio chunk (debug aid).
```

### Runtime Configuration (Environment Variables)

After patching, end users can **override settings at runtime** by setting environment variables:

```bash
# Linux/macOS
export PAICOM_OWW_THRESHOLD=0.65
export PAICOM_OWW_LOCK_MS=2500
export PAICOM_OWW_VERBOSE_LOG=true
sh run.sh

# Windows
set PAICOM_OWW_THRESHOLD=0.65
set PAICOM_OWW_LOCK_MS=2500
set PAICOM_OWW_VERBOSE_LOG=true
run.bat
```

**Supported environment variables:**
- `PAICOM_OWW_THRESHOLD` (float, default 0.7)
- `PAICOM_OWW_LOCK_MS` (int, default 3000)
- `PAICOM_OWW_AUDIO_CHUNK_SIZE` (int, default 1024)
- `PAICOM_OWW_INFERENCE_THREAD_SCALE` (float, default 1.0)
- `PAICOM_OWW_MODEL_RESOURCE` (string, default "oww.model.hey_pie_com.quant.onnx")
- `PAICOM_OWW_AUDIO_SAMPLE_RATE` (int, default 16000)
- `PAICOM_OWW_VERBOSE_LOG` (bool, default false)

### Embedded Resources

The patcher automatically embeds into the patched .exe:
- **ONNX Model:** hey_pie_com.quant.onnx (~1–5 MB, quantized)
- **ONNX Runtime natives:** Platform-specific binaries for inference acceleration
  - Windows x64: onnxruntime-win-x64.dll
  - Linux x64: onnxruntime-linux-x64.so
  - Linux ARM64: onnxruntime-linux-arm64.so
  - macOS x64: onnxruntime-osx-x64.dylib
  - macOS ARM64: onnxruntime-osx-arm64.dylib

All resources are extracted at runtime into the patched app's directory.

If ONNX native files are missing locally, run:

```bash
dotnet run -- --prepare-onnx-natives
```

This copies runtime-native binaries from your local NuGet cache into `Core/NativeLibraries/`.

### Diagnostics

**Check `launcher-runtime.log` for OpenWakeWord events:**

```
2025-03-18 10:15:23.456 [oww] Initialized with settings: ...
2025-03-18 10:15:23.650 [oww] Loading ONNX model from resource: oww.model.hey_pie_com.quant.onnx
2025-03-18 10:15:23.750 [oww] Model loaded, size=2.34 MB
2025-03-18 10:15:23.850 [oww] Starting inference worker
[oww] Audio processed, confidence=0.123
[oww] Audio processed, confidence=0.087
[oww] 🎙️ Wake word detected! Confidence: 0.856  ← Detection!
[oww] Hard lock active (3000 ms) — audio queued
```

**Verbose logging (with `--oww-verbose-log`):**
```
[oww] Inference: detected=false, confidence=0.245
[oww] Inference: detected=false, confidence=0.189
[oww] Inference: detected=true, confidence=0.912
```

### Performance & Tuning

| Setting | Performance Impact | Notes |
|---------|-------------------|-------|
| `threshold` ↑ | Fewer false positives | May miss valid wake words |
| `threshold` ↓ | More detections | Increased false positive rate |
| `chunk-size` ↓ | Higher CPU (more calls) | Lower latency perception |
| `chunk-size` ↑ | Lower CPU | Higher latency (fewer inference cycles) |
| `thread-scale` ↑ | Higher CPU (more workers) | Better multi-chunk handling |
| `lock-ms` ↑ | Prevents back-queuing | May delay legitimate 2nd command |

**Default balanced setting:** threshold=0.7, lock-ms=3000, chunk-size=1024, thread-scale=1.0

### Troubleshooting

**No wake word detections despite running:**
1. Verify microphone is working: check `launcher-runtime.log` for audio enqueue logs
2. Lower `PAICOM_OWW_THRESHOLD` (e.g., 0.5) to increase sensitivity
3. Enable `PAICOM_OWW_VERBOSE_LOG=true` to see confidence scores
4. Check audio sample rate matches model (default 16000 Hz)

**Too many false positives:**
1. Raise `PAICOM_OWW_THRESHOLD` (e.g., 0.85) to reduce sensitivity
2. Reduce `PAICOM_OWW_LOCK_MS` to allow faster 2nd detection (if intentional)

**Inference worker crashes:**
1. Check `launcher-runtime.log` for ONNX Runtime errors
2. Ensure ONNX Runtime native library matches platform architecture
3. Verify hey_pie_com.quant.onnx is embedded in patched exe

**Latency issues:**
1. Increase `PAICOM_OWW_AUDIO_CHUNK_SIZE` (e.g., 2048) to reduce inference frequency
2. Lower `PAICOM_OWW_INFERENCE_THREAD_SCALE` (e.g., 0.5) to single-thread inference
3. Move background task to lower-priority queue (OS-dependent)

## License

This patcher is provided as-is for personal use with legitimately purchased copies of PAIcom.
See LICENSE in the parent directory.

## Support

- **Vosk documentation:** https://alphacephei.com/vosk/
- **OpenWakeWord:** https://github.com/dscripka/openWakeWord
- **ONNX Runtime:** https://onnxruntime.ai/
- **Wine docs:** https://www.winehq.org/
- **NAudio:** https://github.com/naudio/NAudio
