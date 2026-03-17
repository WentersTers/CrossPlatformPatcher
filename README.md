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
dotnet publish -r win-x64 -c Release --self-contained true -p:PublishSingleFile=true -o publish/win
```

**Linux:**
```sh
dotnet publish -r linux-x64 -c Release --self-contained true -p:PublishSingleFile=true -o publish/linux
```

**macOS (Intel):**
```sh
dotnet publish -r osx-x64 -c Release --self-contained true -p:PublishSingleFile=true -o publish/osx-x64
```

**macOS (Apple Silicon):**
```sh
dotnet publish -r osx-arm64 -c Release --self-contained true -p:PublishSingleFile=true -o publish/osx-arm64
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

> **Note:** `publish-all.sh` / `publish-all.bat` publish `CrossPlatformPatcher.csproj` for
> all four RIDs (`win-x64`, `linux-x64`, `osx-x64`, `osx-arm64`) into `publish/CrossPlatformPatcher/<RID>/`.

### Use the Patcher

1. Get your own copy of `PAIcom.exe` (purchased from Steam)
2. Run the patcher:
   ```
   CrossPlatformPatcher PAIcom.exe --out PAIcom_patched.exe
   ```
3. The patcher generates:
   - `PAIcom_patched.exe` — the patched game
   - `run.sh` / `run.bat` / `launch.command` — OS-specific launchers
   - `SETUP_LINUX.md` / `SETUP_MAC.md` — setup instructions

4. To run the patched game:
   - **Windows:** Double-click `PAIcom_patched.exe` or `run.bat`
   - **Linux:** `sh run.sh` (requires Wine)
   - **macOS:** Double-click `launch.command` (requires Wine via Whisky or CrossOver)

## What's Inside

### Key Components

| File/Folder | Purpose |
|---|---|
| `Core/` | Patch injection logic |
| `Core/ReferenceAssemblyResolver.cs` | Cross-platform .NET FW 4.8 ref resolution |
| `Core/VoskResourceEmbedder.cs` | Embeds Vosk libs into the patched exe |
| `Core/LauncherGenerator.cs` | Creates OS-specific launcher scripts |
| `Core/HotSwapTemplate.cs` | The injected runtime (Vosk, mic capture, fallbacks) |
| `Core/NativeLibraries/` | Vosk native binaries (Win, Linux, macOS) |
| `Core/ManagedLibraries/` | Vosk & NAudio managed wrappers |
| `publish-all.bat` / `publish-all.sh` | Multi-platform publish script |

### Bundled Dependencies

All dependencies are **embedded** in the final `CrossPlatformPatcher.exe` / binary:

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
1. **Vosk speech input** (primary, cross-platform)
2. **Speech emulation** (Windows Speech APIs via Wine/Mono)
3. **Direct animation methods** (bypasses speech engine)
4. **Command file dispatch** (write to `command_input.txt` manually)
5. **Local animation scripts** (last-ditch fallback)

## Configuration

### Environment Variables

```bash
# Skip microphone input entirely (use file-based dispatch only)
PAICOM_NO_STT=1 ./run.sh

# Verbose logging (includes Vosk diagnostics)
# (Edit SETUP_LINUX.md for Wine configuration)
```

### File-Based Commands

Even without audio, you can manually write commands to `command_input.txt`:
```
echo "open youtube" > command_input.txt
```

The game responds the same way it would to voice commands.

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
- One new type injected: `HotSwapRuntime` (the Vosk/fallback logic)
- Two method call hookpoints (for initialization and command dispatch)
- Embedded Vosk resources (self-extracted at runtime)

**No PAIcom source code or assets are included or distributed.**

## Development Notes

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

Check `hotswap.log` in the patched exe's directory. If you see:
```
[VOSK] NAudio startup failed
```

Wine audio backend needs configuration. See `SETUP_LINUX.md` generated after first patch.

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
xattr -cr /path/to/CrossPlatformPatcher-osx-arm64

# Or remove from individual files:
xattr -d com.apple.quarantine /path/to/CrossPlatformPatcher
xattr -d com.apple.quarantine /path/to/launch.command
xattr -d com.apple.quarantine /path/to/run.sh
```

After clearing quarantine, launch as normal. Refer to the generated `SETUP_MAC.md` for
the full step-by-step walkthrough.

## License

This patcher is provided as-is for personal use with legitimately purchased copies of PAIcom.
See LICENSE in the parent directory.

## Support

- **Vosk documentation:** https://alphacephei.com/vosk/
- **Wine docs:** https://www.winehq.org/
- **NAudio:** https://github.com/naudio/NAudio
