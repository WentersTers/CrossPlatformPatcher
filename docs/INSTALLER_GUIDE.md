# Cross-Platform Installer Guide

This document covers installer methods for all platforms.

> **macOS native:** The [`SetupWizardMacApp/`](../SetupWizardMacApp/) SwiftUI .app bundle remains the primary macOS distribution path and is **completely unchanged**.
>
> **Cross-platform .NET alternative:** The [`SetupWizardWindows/`](../SetupWizardWindows/) project (Avalonia UI, .NET 8.0) provides a unified installer for **Windows, Linux (AppImage), and macOS** as an alternative to the native Swift app.

---

## Table of Contents

1. [macOS Setup Wizard App (.app)](#1-macos-setup-wizard-app-app)
2. [Cross-Platform SetupWizard (.NET 8.0 Avalonia)](#2-cross-platform-setupwizard-net-80-avalonia)
3. [Code Signing and Distribution](#3-code-signing-and-distribution)
4. [Build & Publish Workflow](#4-build--publish-workflow)
5. [Distribution Checklist](#5-distribution-checklist)
6. [Troubleshooting](#6-troubleshooting)

---

## 1. macOS Setup Wizard App (.app)

**This section remains the same as before.** The native SwiftUI app is the primary macOS distribution path. See below for build and signing instructions.

### 1.1 What Is It?

A native **SwiftUI application** that provides an interactive GUI for:
- Detecting system architecture (arm64 vs x86_64)
- Downloading the latest patcher from GitHub
- Prompting for `PAIcom.exe` location
- Patching the executable
- Installing/detecting Wine (Whisky, Homebrew Wine)
- Creating platform-specific launchers
- Launching the patched game

### 1.2 Building the App

```sh
cd SetupWizardMacApp
./build.sh
```

**Prerequisites:**
- macOS 12+
- Swift 5.9+
- Xcode command-line tools

**Output:**
```
SetupWizardMacApp/build/Release/SetupWizard.app
```

This document covers the native `SetupWizard.app` application for macOS.

## Overview

The macOS installation method uses a **native SwiftUI application** that provides an interactive GUI for setup and patching:

- **Setup Wizard App (.app)** — Native SwiftUI application for interactive setup and patching

The app handles all setup tasks: detecting architecture, downloading the patcher, patching PAIcom, installing Wine/Whisky, and launching the game.

---

## 1. macOS Setup Wizard App (.app)

### 1.1 What Is It?

A native **SwiftUI application** that provides an interactive GUI for:
- Detecting system architecture (arm64 vs x86_64)
- Downloading the latest patcher from GitHub
- Prompting for `PAIcom.exe` location
- Patching the executable
- Installing/detecting Wine (Whisky, Homebrew Wine)
- Creating platform-specific launchers
- Launching the patched game

### 1.2 Building the App

```sh
cd SetupWizardMacApp
./build.sh
```

**Prerequisites:**
- macOS 12+
- Swift 5.9+
- Xcode command-line tools

**Output:**
```
SetupWizardMacApp/build/Release/SetupWizard.app
```

### 1.3 Build Process Explained

The `SetupWizardMacApp/build.sh` script performs these steps:

1. **Create build directories**
   ```
   build/Release/SetupWizard.app/Contents/
   ├── MacOS/
   ├── Resources/
   └── Frameworks/
   ```

2. **Compile with Swift Package Manager**
   ```sh
   swift build -c release --static-swift-stdlib
   ```
   - Produces: `.build/release/SetupWizard`

3. **Create .app bundle structure**
   ```
   SetupWizard.app/
   └── Contents/
       ├── MacOS/
       │   └── SetupWizard                (executable)
       ├── Resources/
       │   └── winetricks                 (if present)
       └── Info.plist
   ```

4. **Copy executable**
   ```sh
   cp .build/release/SetupWizard \
     "$RELEASE_DIR/SetupWizard.app/Contents/MacOS/SetupWizard"
   chmod +x ...
   ```

5. **Generate Info.plist**
   - Bundle ID: `com.github.crossplatformpatcher.setupwizard`
   - Executable name: SetupWizard
   - Supports macOS Retina displays and automatic graphics switching

6. **Ad-hoc code sign** (built into build.sh)
   - Ad-hoc signature required for macOS SIP

### 1.4 App Architecture

The SetupWizard app includes several service layers:

#### ProcessRunner
- Executes shell commands with real-time output streaming
- Handles graceful cancellation (SIGTERM → SIGKILL)
- Tracks process state for recovery

#### SetupStateManager
- Persistent state tracking across sessions
- Stores progress, selected folders, Wine type
- Enables resume after cancellation
- Stored in: `~/.wine/setup-state.json`

#### APIClient
- Fetches latest patcher release from GitHub API
- Auto-detects architecture (arm64 vs x86_64)
- Downloads with progress tracking

#### DependencyChecker
- Detects Whisky installation
- Checks system Wine availability
- Validates .NET requirements

### 1.5 User Workflow

1. User double-clicks `SetupWizard.app`
2. App detects their architecture and OS version
3. App fetches latest patcher release from GitHub
4. User selects location of `PAIcom.exe`
5. App downloads and runs the patcher
6. App guides Wine/Whisky installation (if needed)
7. App generates platform-specific launcher scripts
8. App launches the patched PAIcom

---

## 2. Cross-Platform SetupWizard (.NET 8.0 Avalonia)

The cross-platform SetupWizard is a .NET 8.0 **Avalonia UI** application that runs on **Windows, Linux, and macOS**. It provides the same setup wizard functionality as the macOS Swift app but as a portable .NET binary.

### 2.1 Project Structure

```
SetupWizardCore/              ← Shared business logic (no UI dependencies)
  ├── Models/
  │   └── SetupState.cs       ← Persistent state model
  ├── Services/
  │   ├── SetupStateManager.cs    ← State persistence
  │   ├── SetupWizardLogger.cs    ← File + console logging
  │   ├── GitHubClient.cs         ← GitHub API + download
  │   ├── ProcessRunner.cs        ← Process execution with streaming
  │   ├── ModelDownloader.cs      ← Vosk model download + extraction
  │   ├── DependencyChecker.cs    ← Wine/Whisky detection
  │   ├── DotNetInstaller.cs      ← .NET 4.8 installation strategies
  │   └── PatcherOrchestrator.cs  ← High-level download+patch flow
  └── SetupWizardCore.csproj

SetupWizardWindows/           ← Avalonia UI application
  ├── App.axaml / App.axaml.cs   ← Application entry + DI setup
  ├── Converters/
  │   └── ObjectConverters.cs    ← Screen visibility converter
  ├── ViewModels/
  │   └── MainViewModel.cs       ← Main state + commands
  ├── Views/
  │   ├── MainWindow.axaml       ← Root window with screen switching
  │   ├── WelcomeView.axaml
  │   ├── FolderPickerView.axaml
  │   ├── ModelSelectionView.axaml
  │   ├── DownloadPatchView.axaml
  │   ├── DependencyCheckView.axaml
  │   ├── DotNetInstallView.axaml
  │   ├── CompleteView.axaml
  │   └── ErrorView.axaml
  └── SetupWizardWindows.csproj
```

### 2.2 Prerequisites

- [.NET SDK 8.0](https://dotnet.microsoft.com/download/dotnet/8.0)
- For AppImage packaging: [`appimagetool`](https://github.com/AppImage/AppImageKit/releases)

### 2.3 Building All Platforms

Use the unified build script:

```sh
bash scripts/build-setupwizard-all.sh
```

This publishes self-contained executables for all four targets:

| Runtime ID | Output Directory | File |
|---|---|---|
| `win-x64` | `SetupWizardWindows/bin/Release/net8.0/win-x64/publish/` | `SetupWizard.exe` |
| `linux-x64` | `SetupWizardWindows/bin/Release/net8.0/linux-x64/publish/` | `SetupWizard` (ELF) |
| `osx-x64` | `SetupWizardWindows/bin/Release/net8.0/osx-x64/publish/` | `SetupWizard` (Mach-O) |
| `osx-arm64` | `SetupWizardWindows/bin/Release/net8.0/osx-arm64/publish/` | `SetupWizard` (Mach-O) |

### 2.4 Building Individual Platforms

```sh
# Windows x64
dotnet publish SetupWizardWindows/SetupWizardWindows.csproj \
  -c Release -r win-x64 --self-contained true -p:PublishSingleFile=true

# Linux x64
dotnet publish SetupWizardWindows/SetupWizardWindows.csproj \
  -c Release -r linux-x64 --self-contained true -p:PublishSingleFile=true

# macOS Intel
dotnet publish SetupWizardWindows/SetupWizardWindows.csproj \
  -c Release -r osx-x64 --self-contained true -p:PublishSingleFile=true

# macOS Apple Silicon
dotnet publish SetupWizardWindows/SetupWizardWindows.csproj \
  -c Release -r osx-arm64 --self-contained true -p:PublishSingleFile=true
```

### 2.5 AppImage Packaging (Linux)

After building the `linux-x64` target, package it as an AppImage:

```sh
bash scripts/package-appimage.sh [path-to-appimagetool]
```

This creates `SetupWizard-x86_64.AppImage` in the repository root.

**Manual AppImage steps:**

1. Create AppDir structure:
   ```
   SetupWizard.AppDir/
   ├── AppRun                       # Entry script
   ├── SetupWizard.desktop          # FreeDesktop entry
   ├── SetupWizard.png              # Application icon (256x256)
   └── usr/
       └── bin/
           ├── SetupWizard          # Published .NET binary
           └── lib*                 # Any native .so dependencies
   ```

2. Run `appimagetool`:
   ```sh
   ARCH=x86_64 appimagetool SetupWizard.AppDir SetupWizard-x86_64.AppImage
   ```

### 2.6 Windows Installer Packaging

For Windows distribution, you can wrap the published `SetupWizard.exe` in an installer using tools like:

- **Inno Setup** (free): https://jrsoftware.org/isinfo.php
- **MSIX Packaging**: Built into Windows SDK

### 2.7 User Workflow

1. User launches `SetupWizard` (`.exe`, ELF, or AppImage)
2. Welcome screen explains the setup process
3. User selects the folder containing `PAIcom.exe`
4. User chooses a Vosk speech recognition model (or skips)
5. The model is downloaded and extracted from Alphacephei CDN
6. The latest patcher is downloaded from GitHub Releases
7. The patcher runs against `PAIcom.exe` with `--migration-mode full`
8. User is guided through dependency installation (Wine, .NET)
9. Setup complete — ready to launch

### 2.8 Technology Stack

| Layer | Choice |
|---|---|
| GUI Framework | Avalonia UI 11.0+ |
| Runtime | .NET 8.0 |
| HTTP Client | `System.Net.Http.HttpClient` |
| Zip Extraction | `System.IO.Compression.ZipFile` |
| JSON | `System.Text.Json` |
| Process Execution | `System.Diagnostics.Process` |
| Logging | File + Console |
| MVVM | CommunityToolkit.Mvvm |

---

## 3. Code Signing and Distribution

> **Note:** Sections 3–6 below apply only to the **macOS SwiftUI app** (`SetupWizardMacApp/`).
> The cross-platform .NET builds do not require code signing.

### 2.1 Quick Ad-Hoc Signing

The build script includes automatic ad-hoc code signing. If you need to re-sign or update the signature:

```sh
codesign --force --deep --sign - SetupWizardMacApp/build/Release/SetupWizard.app
```

### 2.2 Developer ID Code Signing (For Distribution)

For distribution through App Store or as a trusted app, use your Developer ID:

```sh
codesign --force --deep --sign "Developer ID Application: Your Name (XXXXXXXXXX)" \
  SetupWizardMacApp/build/Release/SetupWizard.app
```

Verify the signature:

```sh
codesign -v SetupWizardMacApp/build/Release/SetupWizard.app
spctl -a -v SetupWizardMacApp/build/Release/SetupWizard.app
```

### 2.3 Notarization (Required for Distribution on Big Sur+)

Apple requires notarization for apps distributed outside the App Store. This process validates the app is free of malware.

**Prerequisites:**
- Developer ID (paid Apple Developer account)
- App already code-signed with your Developer ID

**Steps:**

1. **Create a compressed archive:**
   ```sh
   ditto -c -k --sequesterRsrc SetupWizardMacApp/build/Release/SetupWizard.app \
     SetupWizard.zip
   ```

2. **Submit for notarization:**
   ```sh
   xcrun notarytool submit SetupWizard.zip \
     --apple-id "your-apple-id@example.com" \
     --team-id "XXXXXXXXXX" \
     --password "app-specific-password"
   ```

3. **Check status:**
   ```sh
   xcrun notarytool info <submission-id> \
     --apple-id "your-apple-id@example.com" \
     --team-id "XXXXXXXXXX" \
     --password "app-specific-password"
   ```

4. **Staple the notarization ticket** (once approved):
   ```sh
   xcrun stapler staple SetupWizardMacApp/build/Release/SetupWizard.app
   ```

For detailed notarization instructions, see:
https://developer.apple.com/documentation/security/notarizing_macos_software_before_distribution

### 2.4 Creating a .dmg Distribution Package

To distribute the app to users, create a disk image:

```sh
hdiutil create -volname "SetupWizard" \
  -srcfolder SetupWizardMacApp/build/Release/SetupWizard.app \
  -ov -format UDZO \
  -imagekey zlib-level=9 \
  SetupWizard.dmg
```

Users can then:
1. Download `SetupWizard.dmg`
2. Double-click to mount the disk image
3. Drag `SetupWizard.app` to `/Applications`
4. Launch from Applications

---

## 3. Distribution Checklist

### Before Building and Distributing

- [ ] Code changes tested with `./scripts/build-and-test.sh`
- [ ] Patcher behavior verified with `./scripts/build-patch-and-launch.sh`
- [ ] SetupWizard.app tested on both Intel and Apple Silicon Macs
- [ ] Documentation (SETUP_MAC.md, SETUP_LINUX.md) is current

### Building for Release

```sh
# 1. Build the SetupWizard app
cd SetupWizardMacApp && ./build.sh && cd ..

# 2. Code sign with Developer ID
codesign --force --deep --sign "Developer ID Application: Your Name (XXXXXXXXXX)" \
  SetupWizardMacApp/build/Release/SetupWizard.app

# 3. Notarize the app (if distributing outside internal use)
xcrun notarytool submit <archive.zip> --apple-id "<email>" --team-id "<team-id>" --password "<app-password>"
xcrun stapler staple SetupWizardMacApp/build/Release/SetupWizard.app

# 4. Create .dmg for distribution
hdiutil create -volname "SetupWizard" \
  -srcfolder SetupWizardMacApp/build/Release/SetupWizard.app \
  -ov -format UDZO SetupWizard.dmg
```

### Distribution Artifacts

- **For GitHub Releases:** `SetupWizard.dmg` (or `SetupWizard.app` directly for ad-hoc signed builds)
- **For App Store:** Notarized and signed app (requires enrollment)

---

## 4. Troubleshooting

### 4.1 "Command not found: swift"

Install Xcode command-line tools:

```sh
xcode-select --install
```

Or install Swift directly:

```sh
brew install swift
```

### 4.2 App won't run ("unidentified developer" warning)

**For ad-hoc signed apps (expected):**

Users should:

1. Right-click the app
2. Select "Open"
3. Click "Open" in the warning dialog

Or remove the quarantine attribute:

```sh
xattr -d com.apple.quarantine SetupWizard.app
```

**For Developer ID signed apps (shouldn't happen):**

The signature may have issues. Verify:

```sh
codesign -v SetupWizard.app
```

### 4.3 Swift build fails

```sh
cd SetupWizardMacApp
swift build -c release --static-swift-stdlib
```

If Swift modules are missing, reinstall Xcode command-line tools:

```sh
xcode-select --reset
xcode-select --install
```

### 4.4 Code sign fails with "resource fork"

Common on case-sensitive file systems. Use ad-hoc signing:

```sh
codesign --force --deep --sign - SetupWizard.app
```

---

## Related Documentation

- [Build System](BUILD_SYSTEM.md) — Full build pipeline details
- [Build Outputs](BUILD_OUTPUTS.md) — Artifact directory structure
- [macOS Setup](SETUP_MAC.md) — User-facing setup guide

---

## Deprecation Notice

> **Note:** The `.pkg` installer package (build-mac-pkg.sh) has been deprecated and removed.
> The native `SetupWizard.app` provides superior functionality and cross-platform compatibility.
> For macOS distribution, use the `.app` bundle with code signing and notarization as described above.


## 3. Build & Publish Workflow

CrossPlatformPatcher uses self-contained executables that require no .NET installation on the target machine.

### 3.1 Publish Commands

To generate release builds for all supported platforms, run the publish script from the repository root:

**Windows:**
\\\cmd
scripts\publish-all.bat
\\\

**macOS / Linux:**
\\\sh
bash scripts/publish-all.sh
\\\

### 3.2 Output Directory Structure

The publish scripts generate the following structure in the \publish/\ folder:

\\\
publish/
└── CrossPlatformPatcher/
    ├── win-x64/
    │   └── CrossPlatformPatcher-W-x64.exe
    ├── linux-x64/
    │   └── CrossPlatformPatcher-L-x64
    ├── osx-x64/
    │   └── CrossPlatformPatcher-M-x64
    └── osx-arm64/
        └── CrossPlatformPatcher-M-Arm
\\\

### 3.3 Platform-Specific Executable Naming Conventions

To clearly distinguish platform targets, the generated binaries follow this naming scheme:

- \-W-x64\: Windows 64-bit (\.exe\ extension)
- \-L-x64\: Linux 64-bit (ELF binary)
- \-M-x64\: macOS Intel 64-bit (Mach-O binary)
- \-M-Arm\: macOS Apple Silicon (ARM64 Mach-O binary)

### 3.4 Self-Contained Binary Verification

The publish pipeline uses \--self-contained true\ and \-p:PublishSingleFile=true\ to bundle the .NET runtime and all managed dependencies into a single file.

After publishing, verify the output by checking that the executable exists and has a significant size (usually >30MB) indicating the runtime is successfully bundled. Native dependencies like \onnxruntime\ files will also be copied to the output directory alongside the main executable.
