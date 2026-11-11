# Quick Start

Fastest way to get started with CrossPlatformPatcher on Windows, Linux or macOS.

Status: the patcher runs on all three platforms. The patched game is certified
on the platforms listed in the [README's Certified platforms
section](../README.md#certified-platforms); unsupported game builds are refused
rather than patched. macOS is the oldest path (Intel and Apple Silicon); the
Linux paths cover plain Wine, GE-Proton and Steam Proton Experimental.

> **Ignore `[LAUNCH HERE].exe` — it is a decoy that ships with the game itself,
> not the program.** The real game is `uninstall.exe`; run it (or the patched
> `uninstall.exe.patched.exe`) with `run.sh` on Linux, directly on Windows, or
> with `launch.command` on macOS. Double-clicking the decoy produces a false
> failure.

## Choose Your Path

### I want to test the patcher quickly (all platforms)

```sh
# 1. Run all tests and build validation
./scripts/build-and-test.sh

# Output: All tests pass ✓
```

---

### I want to build, patch, and run the game (Linux/macOS)

```sh
# 1. One command does it all
./scripts/build-patch-and-launch.sh --migration-mode full

# The game launches automatically when ready
```

---

### I want to build for all platforms (for distribution)

```sh
# 1. Build and publish for Windows, Linux, macOS (Intel + Apple Silicon)
sh publish-all.sh

# 2. Artifacts are in publish/
ls publish/win/CrossPlatformPatcher.exe
ls publish/linux/CrossPlatformPatcher
ls publish/osx-x64/CrossPlatformPatcher
ls publish/osx-arm64/CrossPlatformPatcher
```

---

### I want to build the native macOS Setup Wizard app

```sh
# Prerequisites: Swift 5.9+, macOS 12+
cd SetupWizardMacApp
./build.sh

# Artifact: build/Release/SetupWizard.app

# Code sign for distribution
codesign --force --deep --sign - build/Release/SetupWizard.app
```

See [INSTALLER_GUIDE.md](INSTALLER_GUIDE.md) for code signing and notarization instructions.

---

## Platform-Specific Quick Starts

### macOS (Intel or Apple Silicon)

**First Time:**
```sh
# 1. Ensure Xcode command-line tools are installed
xcode-select --install

# 2. Clone and navigate to repo
cd /path/to/CrossPlatformPatcher

# 3. Build, patch, and launch
./scripts/build-patch-and-launch.sh --migration-mode full
```

**Subsequent Runs:**
```sh
# After first setup, launch the game directly
./PAIcom_Player_Folder/launch.command
```

---

### Linux

**Requirements:** at least 4 GB of RAM (6 GB when running under Steam with
Proton), and a working X display — build and run with a display set, or start
a virtual one first (`Xvfb :99 & export DISPLAY=:99`).

**Steam / Proton:** use Proton 8.x or Proton Experimental. Proton 9 has been
seen to open the menu with no Start button; PAIcom warns at startup when it
detects that version.

**Voice runtime files:** copy the patched exe *with* `PAIcom.OWW.dll`, the
Onnx/Vosk/NAudio/System runtime DLLs and the `models` folder; an exe-only copy
fails the audio hook. Full list: [README voice runtime files](../README.md#voice-runtime-files-required-for-microphone-commands).

**First Time:**
```sh
# 1. Ensure .NET 8.0 SDK is installed
dotnet --version

# If not installed:
# Ubuntu/Debian: sudo apt install dotnet-sdk-8.0
# Other: https://dot.net/download

# 2. Build, patch, and launch
./scripts/build-patch-and-launch.sh --migration-mode full
```

**Subsequent Runs:**
```sh
# Launch the game directly
sh ./PAIcom_Player_Folder/launch.sh
```

---

### Windows

**First Time:**
```powershell
# 1. Ensure .NET 8.0 SDK is installed
dotnet --version

# If not installed: https://dot.net/download

# 2. Build, patch, and launch
.\scripts\build-patch-and-launch.sh -migration-mode full
```

**Subsequent Runs:**
```powershell
# Launch the game directly
.\PAIcom_Player_Folder\launch.bat
```

---

## Common Options

### Testing with Command Injection

```sh
./scripts/build-patch-and-launch.sh \
  --migration-mode full \
  --test-commands \
  --test-duration 60 \
  --test-interval 1500
```

### Building for a Specific Architecture

```sh
# macOS Apple Silicon
./scripts/build-patch-and-launch.sh --rid osx-arm64

# macOS Intel
./scripts/build-patch-and-launch.sh --rid osx-x64

# Linux x64
./scripts/build-patch-and-launch.sh --rid linux-x64

# Windows x64
./scripts/build-patch-and-launch.sh --rid win-x64
```

### Build without Launching

```sh
./scripts/build-patch-and-launch.sh \
  --migration-mode full \
  --no-launch
```

### Enable Runtime Diagnostics

```sh
./scripts/build-patch-and-launch.sh \
  --migration-mode full \
  --runtime-diagnostic \
  --runtime-diagnostic-duration 120
```

---

## Troubleshooting

### "dotnet: command not found"

Install the .NET SDK:

**macOS:**
```sh
brew install dotnet
```

**Linux (Ubuntu/Debian):**
```sh
sudo apt install dotnet-sdk-8.0
```

**Linux (other):**
```sh
curl -sSL https://dot.net/v1/dotnet-install.sh | bash -s -- --channel 8.0
```

**Windows:**
Download from https://dot.net/download

---

### "Wine not found" on first launch

The script guides Wine installation. Choose your method:

**macOS: Whisky (recommended)**
- Download: https://github.com/Whisky-App/Whisky/releases
- Free, native, supports arm64 and x86_64

**macOS/Linux: Homebrew Wine**
```sh
brew install wine
# or
sudo apt install wine-stable
```

---

## Next Steps

1. **For development:** Read [Project Structure](docs/PROJECT_STRUCTURE.md) to understand the codebase
2. **For testing:** See [Test Commands](docs/TEST_COMMANDS.md) and [Live Testing](docs/LIVE-TESTING.md)
3. **For distribution:** See [Build System](docs/BUILD_SYSTEM.md) and [Installer Guide](docs/INSTALLER_GUIDE.md)
4. **For troubleshooting:** See [Optimization Guide](docs/OPTIMIZATION_GUIDE.md) and [Cross-Platform Integration](docs/CROSS-PLATFORM-INTEGRATION.md)

---

## Full Documentation Index

All documentation is organized in [docs/README.md](docs/README.md).
