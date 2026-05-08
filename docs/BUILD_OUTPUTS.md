# Build Outputs Structure

This document describes the complete directory structure of build artifacts produced by various build commands.

---

## 1. Local Build Outputs

### 1.1 Debug Build

**Command:** `dotnet build CrossPlatformPatcher.csproj -c Debug`

**Output structure:**
```
bin/
└── Debug/
    └── net8.0/
        ├── CrossPlatformPatcher.exe        (Main executable)
        ├── CrossPlatformPatcher.dll        (Assembly)
        ├── CrossPlatformPatcher.pdb        (Debug symbols)
        ├── dnlib.dll                       (IL rewriting library)
        ├── *.dll                           (All dependencies)
        └── ...
```

**Size:** ~50-100 MB (includes full symbol information)

**Usage:** Development, fast iteration, debugging

---

### 1.2 Release Build

**Command:** `dotnet build CrossPlatformPatcher.csproj -c Release`

**Output structure:**
```
bin/
└── Release/
    └── net8.0/
        ├── CrossPlatformPatcher.exe        (Optimized executable)
        ├── CrossPlatformPatcher.dll        (Optimized assembly)
        ├── dnlib.dll                       (IL rewriting library)
        ├── *.dll                           (All dependencies)
        └── ...
```

**Size:** ~40-60 MB (optimized, reduced symbols)

**Usage:** Distribution candidates, comprehensive testing

---

## 2. Published Self-Contained Executables

### 2.1 Complete Published Structure

**Command:** `sh publish-all.sh` or individual platform publishes

**Root output structure:**
```
publish/
├── build-patch-and-launch/
│   ├── CrossPlatformPatcher.exe        (Intermediate build)
│   └── ...
├── win/
│   └── CrossPlatformPatcher.exe        (Windows x64, self-contained)
├── linux/
│   └── CrossPlatformPatcher            (Linux x64, self-contained)
├── osx-x64/
│   └── CrossPlatformPatcher            (macOS Intel, self-contained)
└── osx-arm64/
    └── CrossPlatformPatcher            (macOS Apple Silicon, self-contained)
```

### 2.2 Windows Publish

**Command:**
```sh
dotnet publish CrossPlatformPatcher.csproj \
  -r win-x64 \
  -c Release \
  --self-contained true \
  -p:PublishSingleFile=true \
  -o publish/win
```

**Output:**
```
publish/win/
├── CrossPlatformPatcher.exe            (Single executable, ~80-100 MB)
├── appsettings.json                    (Configuration)
├── .NET runtime                        (Bundled inside .exe)
└── All dependencies                    (Bundled inside .exe)
```

**Characteristics:**
- Single standalone .exe file
- No .NET installation required on target
- Portable across Windows machines with same architecture
- Size: ~80-100 MB (compressed into single file)

---

### 2.3 Linux Publish

**Command:**
```sh
dotnet publish CrossPlatformPatcher.csproj \
  -r linux-x64 \
  -c Release \
  --self-contained true \
  -p:PublishSingleFile=true \
  -o publish/linux
```

**Output:**
```
publish/linux/
├── CrossPlatformPatcher                (Single executable, ~80-100 MB)
├── appsettings.json                    (Configuration)
├── .NET runtime                        (Bundled inside executable)
└── All dependencies                    (Bundled inside executable)
```

**Characteristics:**
- Single standalone executable
- No .NET installation required
- Executable permissions required: `chmod +x CrossPlatformPatcher`
- Size: ~80-100 MB
- Compatible with any Linux distribution with glibc 2.31+

---

### 2.4 macOS Intel Publish

**Command:**
```sh
dotnet publish CrossPlatformPatcher.csproj \
  -r osx-x64 \
  -c Release \
  --self-contained true \
  -p:PublishSingleFile=true \
  -o publish/osx-x64
```

**Output:**
```
publish/osx-x64/
├── CrossPlatformPatcher                (Single executable, ~80-100 MB, Intel 64-bit)
├── appsettings.json                    (Configuration)
├── .NET runtime                        (Bundled inside executable)
└── All dependencies                    (Bundled inside executable)
```

**Characteristics:**
- Single standalone executable (Mach-O x86_64 format)
- No .NET installation required
- Executable permissions required: `chmod +x CrossPlatformPatcher`
- Size: ~80-100 MB
- Can run on Intel Macs via native x86_64 or via Rosetta 2 emulation on Apple Silicon

---

### 2.5 macOS Apple Silicon Publish

**Command:**
```sh
dotnet publish CrossPlatformPatcher.csproj \
  -r osx-arm64 \
  -c Release \
  --self-contained true \
  -p:PublishSingleFile=true \
  -o publish/osx-arm64
```

**Output:**
```
publish/osx-arm64/
├── CrossPlatformPatcher                (Single executable, ~80-100 MB, ARM64)
├── appsettings.json                    (Configuration)
├── .NET runtime                        (Bundled inside executable)
└── All dependencies                    (Bundled inside executable)
```

**Characteristics:**
- Single standalone executable (Mach-O arm64 format)
- Native execution on Apple Silicon Macs
- No emulation overhead (vs. Rosetta 2 x86_64)
- Size: ~80-100 MB
- Best performance on M1/M2/M3 Macs

---

## 3. macOS Application Outputs

### 3.1 SetupWizard macOS App (.app)

**Command:** `cd SetupWizardMacApp && ./build.sh`

**Build directory structure:**
```
SetupWizardMacApp/
├── .build/
│   └── release/
│       └── SetupWizard                   (Compiled executable)
├── build/
│   └── Release/
│       └── SetupWizard.app/              (Final .app bundle)
│           └── Contents/
│               ├── MacOS/
│               │   └── SetupWizard       (Executable)
│               ├── Resources/
│               │   └── winetricks        (Optional helper tool)
│               └── Info.plist            (Bundle metadata)
└── Sources/
    ├── main.swift
    ├── Views/
    ├── Services/
    └── Models/
```

**Final artifact:**
```
SetupWizardMacApp/build/Release/SetupWizard.app
```

**Characteristics:**
- Native macOS application bundle
- SwiftUI-based GUI
- Code-signable for distribution (ad-hoc, Developer ID, or notarized)
- Size: ~50-80 MB
- Requires macOS 12+ and Swift 5.9+
- Can be distributed in a .dmg file or directly as an .app
- See [Installer Guide](INSTALLER_GUIDE.md) for code signing and distribution instructions

---

## 4. Patched Game Artifacts

### 4.1 Runtime Artifacts (Generated During Patching)

**Command:** `./scripts/build-patch-and-launch.sh --migration-mode full`

**Artifacts created in `PAIcom_Player_Folder/`:**
```
PAIcom_Player_Folder/
├── PAIcom.exe                           (Original executable, input)
├── PAIcom_patched.exe                   (Patched executable, output)
├── CrossPlatformPatcher                 (Patcher binary, platform-dependent)
├── launch.command                       (macOS launcher script)
├── launch.sh                            (Linux launcher script)
├── launch.bat                           (Windows launcher script)
├── *.txt                                (Configuration files)
└── other files...
```

**Characteristics:**
- `PAIcom_patched.exe` is the main output
- Ready to run via platform-specific launcher
- Wine prefix created at `~/.wine/` or `~/.whisky/` (if not using Whisky)

---

### 4.2 Runtime Configuration Artifacts

**Command:** `./scripts/build-patch-and-launch.sh --migration-mode full`

**Artifacts created in `~/.paicom/`:**
```
~/.paicom/
├── models/
│   ├── vosk-model-small-en-us-0.15/     (Speech model)
│   ├── vosk-model-en-us-zamia-0.6/      (Alternative model)
│   └── ...
├── config.json                          (Runtime configuration)
└── logs/                                (Runtime diagnostics)
```

**Characteristics:**
- User-local configuration directory
- Models extracted from Vosk zips during first launch
- Logs capture runtime diagnostics for troubleshooting

---

### 4.3 Wine/Whisky Prefix Artifacts

**Default Wine prefix:** `~/.wine/`
**Whisky prefix:** `~/.whisky/` (or Whisky app container)

**Contents:**
```
~/.wine/
├── drive_c/
│   ├── Program Files/                   (Installed programs)
│   ├── windows/
│   ├── Users/
│   └── ...
├── system.reg                           (Registry)
├── user.reg                             (User registry)
└── ...
```

**Characteristics:**
- Created automatically by Wine on first run
- Preserved across game launches
- Contains all Windows emulation state
- Large directory (~1-2 GB after game installation)

---

## 5. Test Artifacts

### 5.1 Test Output

**Command:** `dotnet test CrossPlatformPatcher.Tests/CrossPlatformPatcher.Tests.csproj -c Release`

**Output structure:**
```
CrossPlatformPatcher.Tests/
└── bin/
    └── Release/
        └── net8.0/
            ├── CrossPlatformPatcher.Tests.dll
            ├── CrossPlatformPatcher.Tests.pdb
            ├── *.dll                           (Test dependencies)
            └── ...
```

**Console output** shows test results:
```
Passed! - Failed: 0, Passed: 36, Skipped: 0
```

---

## 6. Archive Artifacts

### 6.1 Building a .dmg Distribution Disk Image

To distribute SetupWizard.app or documents:

```sh
# Example: Create a disk image containing SetupWizard.app
hdiutil create -volname "CrossPlatformPatcher" \
  -srcfolder SetupWizardMacApp/build/Release/ \
  -ov -format UDZO \
  -imagekey zlib-level=9 \
  CrossPlatformPatcher.dmg
```

**Output:**
```
CrossPlatformPatcher.dmg                (~50-100 MB, compressed)
```

**User experience:**
1. Download the .dmg
2. Double-click to mount
3. Drag SetupWizard.app to Applications
4. Launch from Applications folder

---

## 7. Size Reference

| Artifact | Size | Notes |
|----------|------|-------|
| `bin/Debug/` | 50-100 MB | With debug symbols |
| `bin/Release/` | 40-60 MB | Optimized, fewer symbols |
| `publish/*/CrossPlatformPatcher*` | 80-100 MB each | Self-contained, single file |
| `SetupWizard.app` | 50-80 MB | Native macOS app, unsigned |
| `.dmg` (SetupWizard) | 40-60 MB | Compressed disk image |
| `~/.wine/` (after PAIcom) | 1-2 GB | Windows emulation prefix |
| `~/.paicom/` (with models) | 500 MB - 1 GB | Speech models |

---

## 8. Cleanup

### 8.1 Remove Local Build Artifacts

```sh
# Remove bin/obj directories
rm -rf bin/ obj/

# Remove published binaries
rm -rf publish/

# Remove SetupWizard.app build
rm -rf SetupWizardMacApp/build/ SetupWizardMacApp/.build/
```

### 8.2 Remove Runtime Artifacts

```sh
# Remove patched executable
rm -f PAIcom_Player_Folder/PAIcom_patched.exe

# Remove user configuration (CAUTION: Deletes settings!)
rm -rf ~/.paicom/

# Remove Wine prefix (CAUTION: Deletes all game state!)
rm -rf ~/.wine/
```

---

## Related Documentation

- [Build System](BUILD_SYSTEM.md) — Build commands and workflows
- [Installer Guide](INSTALLER_GUIDE.md) — macOS .app code signing and distribution
- [macOS Setup](SETUP_MAC.md) — User-facing setup guide
