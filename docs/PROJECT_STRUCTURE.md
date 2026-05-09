# Project Structure

This file explains where core behavior lives and which files to touch for specific changes.

## Core Directories

- `Core/`: patcher-side implementation that rewrites the target assembly and generates launchers.
- `PAIcom.OWW/`: runtime helper library injected/loaded by patched output; canonical runtime speech/dispatch behavior.
- `CrossPlatformPatcher.Tests/`: unit/integration tests, including runtime-helper parity checks.
- `PAIcom_Player_Folder/`: local runtime sandbox for patched output, launchers, logs, and command assets.
- `docs/`: active documentation and archive.

## Important Entry Points

- `Program.cs`: CLI surface for patching and OpenWakeWord tuning options.
- `Core/AssemblyPatcher.cs`: patch orchestration, IL rewrites, resource embedding.
- `Core/OpenWakeWordCompatibilityPatcher.cs`: injects runtime helper hooks and call contracts.
- `Core/ProcessStartCompatibilityPatcher.cs`: Process.Start rewrite + suppression diagnostics.
- `Core/LauncherGenerator.cs`: generates `run.sh`, `run.bat`, setup launchers, and migration behavior defaults.

## Runtime Speech Path (Canonical)

- `PAIcom.OWW/OpenWakeWordHelper.cs`:
  - wake detection handoff to STT
  - lock-window audio queueing and guardrails
  - command resolution/dispatch and fallback script execution
  - runtime timing markers (`[oww-timing]`)
- `PAIcom.OWW/VoskSpeechRecognizer.cs`:
  - Vosk initialization/model discovery
  - audio chunk processing
  - final-result + partial-result fallback handling
- `PAIcom.OWW/OpenWakeWordSettings.cs`:
  - architecture-aware defaults (arm64 profile)
  - environment variable overrides

## Runtime/Helper Parity

Critical duplicated helper behavior in `Core/OpenWakeWordHelper.cs` and `PAIcom.OWW/OpenWakeWordHelper.cs` is validated by:

- `CrossPlatformPatcher.Tests/RuntimeHelperParityTests.cs`

Run it directly:

```bash
DOTNET_CLI_TELEMETRY_OPTOUT=1 dotnet test CrossPlatformPatcher.Tests/CrossPlatformPatcher.Tests.csproj -c Release --filter "FullyQualifiedName~RuntimeHelperParityTests"
```

## Common Workflows

- Full validation: `./build-and-test.sh`
- Build only: `dotnet build CrossPlatformPatcher.csproj -c Release`
- Tests only: `dotnet test CrossPlatformPatcher.Tests/CrossPlatformPatcher.Tests.csproj -c Release`
- Patch + launch flow: `./build-patch-and-launch.sh --migration-mode full`

## Logs and Diagnostics

- Runtime log: `PAIcom_Player_Folder/launcher-runtime.log`
- Launcher log: `PAIcom_Player_Folder/launcher.log`
- Timing markers: search for `[oww-timing]` in runtime logs.
