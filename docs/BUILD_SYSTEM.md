# Build System Documentation

This document provides complete details on the CrossPlatformPatcher build pipeline, including all supported targets, build modes, and output artifacts.

## Overview

The build system consists of:

1. **Core Patcher** (`dotnet build`/`dotnet publish`) — C# standalone executable
2. **Test Suite** (`dotnet test`) — Validation and integration tests
3. **Launcher Generation** (`LauncherGenerator.cs`) — Runtime shell script generation
4. **macOS Setup Wizard (.app)** (`SetupWizardMacApp/build.sh`) — Native SwiftUI installer UI

---

## 1. Building the Patcher Binary

### 1.1 Debug Build

Fastest build for development and testing:

```sh
# From repo root
dotnet build CrossPlatformPatcher.csproj -c Debug
```

**Output:** `bin/Debug/net8.0/CrossPlatformPatcher.exe` (platform-dependent binary)

**Use case:** Development, debugging, rapid iteration

### 1.2 Release Build

Optimized build for distribution:

```sh
# From repo root
dotnet build CrossPlatformPatcher.csproj -c Release
```

**Output:** `bin/Release/net8.0/CrossPlatformPatcher.exe`

---

## 2. Publishing Self-Contained Executables

Self-contained publishes bundle the entire .NET runtime, producing a single executable per platform that requires no .NET installation on the target machine.

### 2.1 All Platforms (One-Command)

**Linux/macOS:**
```sh
sh publish-all.sh
```

**Windows:**
```cmd
publish-all.bat
```

**Output structure:**
```
publish/
  win/
    CrossPlatformPatcher.exe          (Windows x64)
  linux/
    CrossPlatformPatcher             (Linux x64)
  osx-x64/
    CrossPlatformPatcher             (macOS Intel)
  osx-arm64/
    CrossPlatformPatcher             (macOS Apple Silicon)
```

### 2.2 Individual Platform Publishes

**Windows (x64):**
```sh
dotnet publish CrossPlatformPatcher.csproj \
  -r win-x64 \
  -c Release \
  --self-contained true \
  -p:PublishSingleFile=true \
  -o publish/win
```

**Linux (x64):**
```sh
dotnet publish CrossPlatformPatcher.csproj \
  -r linux-x64 \
  -c Release \
  --self-contained true \
  -p:PublishSingleFile=true \
  -o publish/linux
```

**macOS (Intel):**
```sh
dotnet publish CrossPlatformPatcher.csproj \
  -r osx-x64 \
  -c Release \
  --self-contained true \
  -p:PublishSingleFile=true \
  -o publish/osx-x64
```

**macOS (Apple Silicon):**
```sh
dotnet publish CrossPlatformPatcher.csproj \
  -r osx-arm64 \
  -c Release \
  --self-contained true \
  -p:PublishSingleFile=true \
  -o publish/osx-arm64
```

### 2.3 Publication Flags Explained

| Flag | Meaning |
|------|---------|
| `-r <rid>` | Runtime identifier (platform target) |
| `-c Release` | Release configuration (optimized) |
| `--self-contained true` | Bundle .NET runtime |
| `-p:PublishSingleFile=true` | Produce single executable (not folder) |
| `-o <dir>` | Output directory |

---

## 3. Running Tests

### 3.1 Parity Tests (Developers)

Validates that `Core` helper implementations match `PAIcom.OWW` versions:

```sh
dotnet test CrossPlatformPatcher.Tests/CrossPlatformPatcher.Tests.csproj \
  -c Release \
  --filter "FullyQualifiedName~RuntimeHelperParityTests"
```

### 3.2 Full Test Suite

Runs all tests (parity + integration + unit):

```sh
dotnet test CrossPlatformPatcher.Tests/CrossPlatformPatcher.Tests.csproj \
  -c Release
```

### 3.3 Standard Validation Workflow

The recommended developer validation script:

```sh
./scripts/build-and-test.sh
```

This runs:
1. Parity check (Core vs PAIcom.OWW methods)
2. Release build
3. Full test suite
4. (Optional) Patch workflow validation with `RUN_PATCH_WORKFLOW=1`

**Enable patch workflow validation:**
```sh
RUN_PATCH_WORKFLOW=1 ./scripts/build-and-test.sh
```

---

## 4. Building Patched PAIcom and Launching

The unified build/patch/launch workflow:

```sh
./scripts/build-patch-and-launch.sh [OPTIONS]
```

### 4.1 Common Usage Patterns

**Basic launch (full migration mode):**
```sh
./scripts/build-patch-and-launch.sh --migration-mode full
```

**Build and patch only (no launch):**
```sh
./scripts/build-patch-and-launch.sh --migration-mode full --no-launch
```

**Testing mode with command injection:**
```sh
./scripts/build-patch-and-launch.sh \
  --migration-mode full \
  --test-commands \
  --test-duration 60 \
  --test-interval 1500
```

**With runtime diagnostics:**
```sh
./scripts/build-patch-and-launch.sh \
  --migration-mode full \
  --runtime-diagnostic \
  --runtime-diagnostic-duration 120
```

**Force specific architecture (arm64 on Apple Silicon):**
```sh
./scripts/build-patch-and-launch.sh \
  --rid osx-arm64 \
  --migration-mode full
```

### 4.2 build-patch-and-launch.sh Options

| Option | Description |
|--------|-------------|
| `--rid <rid>` | Override detected runtime identifier |
| `--migration-mode <mode>` | Launcher mode: `stable`, `probe`, or `full` (default: `full`) |
| `--test-commands` | Inject test commands into running game |
| `--test-interval <ms>` | Milliseconds between test commands (default: 1000) |
| `--test-duration <sec>` | Seconds to run tests, then auto-stop |
| `--runtime-diagnostic` | Enable runtime diagnostics in launched game |
| `--runtime-diagnostic-duration <sec>` | Snapshot duration for diagnostics |
| `--file-command-input` | Read commands from `input-command.txt` |
| `--verbose` | Print executed commands |
| `--show-build-output` | Display full build/publish output |
| `--no-launch` | Build/publish/patch only, skip launch |
| `--build-log <file>` | Save build output to log file |
| `-h, --help` | Show help text |

---

## 5. Building the macOS Setup Wizard App (.app)

### 5.1 Quick Build

```sh
cd SetupWizardMacApp
./build.sh
```

**Output:** `SetupWizardMacApp/build/Release/SetupWizard.app`

**Prerequisites:**
- macOS 12+
- Swift 5.9+
- Xcode command-line tools (`xcode-select --install`)

### 5.2 What the App Does

The SetupWizard is a native SwiftUI application that:

1. Detects system architecture (arm64 or x86_64)
2. Downloads latest patcher release from GitHub
3. Prompts user to select `PAIcom.exe`
4. Patches the executable using the downloaded patcher
5. Guides Wine/Whisky installation (macOS/Linux)
6. Generates platform-specific launchers
7. Launches the patched PAIcom

### 5.3 Code Signing the App

Before distribution, the app must be code-signed. See [INSTALLER_GUIDE.md § 6](INSTALLER_GUIDE.md#6-code-signing-and-distribution) for complete signing and distribution instructions.

**Quick signing (ad-hoc):**
```sh
codesign --force --deep --sign - SetupWizardMacApp/build/Release/SetupWizard.app
```

**For distribution:**
- Code sign with a valid Developer ID
- Notarize the app (required for Big Sur+)
- Create a .dmg for distribution

---

## 6. Build Outputs Summary

### 6.1 Repository Build Artifacts

| Path | Contents | Generated By |
|------|----------|--------------|
| `bin/Debug/` | Debug binaries | `dotnet build -c Debug` |
| `bin/Release/` | Release binaries | `dotnet build -c Release` |
| `publish/` | Platform-specific executables | `publish-all.sh` / platform publishes |
| `SetupWizardMacApp/build/Release/` | Native macOS setup wizard (.app) | `SetupWizardMacApp/build.sh` |

### 6.2 User Artifacts (Generated During Patching)

When running `build-patch-and-launch.sh`:

| Path | Contents |
|------|----------|
| `PAIcom_Player_Folder/PAIcom_patched.exe` | Patched Windows executable |
| `~/.wine/` | Wine prefix (if not using Whisky) |
| `~/.paicom/` | Game configuration and models |
| Generated launcher scripts | Platform-specific runners |

---

## 7. Troubleshooting Common Build Issues

### 7.1 "The type 'X' exists in multiple namespaces"

Usually caused by duplicate PAIcom.OWW definitions:

**Solution:** Ensure `.csproj` properly excludes duplicate sources:
```xml
<ItemGroup>
  <Compile Remove="Command Caller reference/**" />
</ItemGroup>
```

### 7.2 dotnet build fails with missing dependencies

Install .NET SDK 8.0+:

```sh
# macOS with Homebrew
brew install dotnet

# Linux (Ubuntu/Debian)
curl -sSL https://dot.net/v1/dotnet-install.sh | bash -s -- --channel 8.0

# Windows
# Download from https://dotnet.microsoft.com/download
```

### 7.3 publish-all.sh fails on non-matching platform

The script auto-detects your platform. To force a specific target:

```sh
./scripts/build-patch-and-launch.sh --rid osx-arm64 --no-launch
```

### 7.4 macOS installer build fails (pkgbuild/productbuild not found)

These tools are part of macOS:

```sh
# Ensure Xcode command-line tools are installed
xcode-select --install

# Or install full Xcode from App Store
```

---

## 8. Development Workflow

### 8.1 Quick Iteration

```sh
# 1. Make code changes
# 2. Run parity + tests
./scripts/build-and-test.sh

# 3. If tests pass, build/patch/launch
./scripts/build-patch-and-launch.sh --migration-mode full
```

### 8.2 Full CI/CD Pipeline

```sh
# 1. Parity check
dotnet test CrossPlatformPatcher.Tests/CrossPlatformPatcher.Tests.csproj \
  -c Release \
  --filter "FullyQualifiedName~RuntimeHelperParityTests"

# 2. Build
dotnet build CrossPlatformPatcher.csproj -c Release

# 3. Full tests
dotnet test CrossPlatformPatcher.Tests/CrossPlatformPatcher.Tests.csproj -c Release

# 4. Publish all platforms
sh publish-all.sh

# 5. (Optional) Build SetupWizard.app
cd SetupWizardMacApp && ./build.sh
```

### 8.3 Release Workflow

```sh
# Tag the release
git tag v0.5.0
git push origin v0.5.0

# Publish all platforms
sh publish-all.sh

# Code sign and notarize macOS app (if distributing)
codesign --force --deep --sign "Developer ID Application: Name (TEAM)" \
  SetupWizardMacApp/build/Release/SetupWizard.app
xcrun notarytool submit SetupWizard.zip \
  --apple-id "email@example.com" \
  --team-id "XXXXXXXXXX" \
  --password "app-password"
xcrun stapler staple SetupWizardMacApp/build/Release/SetupWizard.app

# Create .dmg for distribution
hdiutil create -volname "SetupWizard" \
  -srcfolder SetupWizardMacApp/build/Release/SetupWizard.app \
  -ov -format UDZO SetupWizard.dmg

# Upload artifacts to GitHub Release:
# - publish/win/CrossPlatformPatcher.exe
# - publish/linux/CrossPlatformPatcher
# - publish/osx-x64/CrossPlatformPatcher
# - publish/osx-arm64/CrossPlatformPatcher
# - SetupWizard.dmg (or SetupWizard.app for direct distribution)
```

See [Installer Guide](INSTALLER_GUIDE.md) for detailed code signing and distribution instructions.

---

## Related Documentation

- [Build Outputs Structure](BUILD_OUTPUTS.md) — Detailed artifact directories
- [Installer Guide](INSTALLER_GUIDE.md) — macOS .app code signing and distribution
- [macOS Setup](SETUP_MAC.md) — User-facing setup documentation
- [Quick Reference](../QUICK_REFERENCE.md) — Command cheat sheet
