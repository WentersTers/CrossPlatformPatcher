# CrossPlatformPatcher Quick Reference

## Daily Developer Workflow

```bash
# Full validation (parity + build + tests)
./build-and-test.sh

# Build only
dotnet build CrossPlatformPatcher.csproj -c Release

# Test only
dotnet test CrossPlatformPatcher.Tests/CrossPlatformPatcher.Tests.csproj -c Release

# Runtime-helper parity check only
dotnet test CrossPlatformPatcher.Tests/CrossPlatformPatcher.Tests.csproj -c Release --filter "FullyQualifiedName~RuntimeHelperParityTests"
```

## Patch and Run Flow

```bash
# Build, publish, patch, and launch
./build-patch-and-launch.sh --migration-mode full

# Build/publish/patch only (no launch)
./build-patch-and-launch.sh --migration-mode full --no-launch
```

## Fast Testing Commands

```bash
# Full command test sweep
./quick-test-all.sh

# Manual interactive command testing
./interactive-test.sh

# Specific command test
./test-all-commands.sh --command "hey paicom open the browser"
```

## Runtime Tuning Examples

```bash
# Accuracy-focused
./build-patch-and-launch.sh --migration-mode full --oww-threshold 0.75 --oww-fuzzy-match-confidence 0.75

# Latency-focused
./build-patch-and-launch.sh --migration-mode full --oww-audio-chunk-size 960 --oww-mic-buffer-ms 160
```

## Debug Log Commands

```bash
# Stream runtime log
tail -f PAIcom_Player_Folder/launcher-runtime.log

# Timing markers (wake -> transcript -> dispatch)
grep "\[oww-timing\]" PAIcom_Player_Folder/launcher-runtime.log

# Command dispatch outcomes
grep "\[oww-command\]" PAIcom_Player_Folder/launcher-runtime.log
```

## Source-of-Truth Files

- Runtime speech/dispatch behavior: `PAIcom.OWW/OpenWakeWordHelper.cs`
- Runtime STT lifecycle: `PAIcom.OWW/VoskSpeechRecognizer.cs`
- Runtime defaults: `PAIcom.OWW/OpenWakeWordSettings.cs`
- Patch/injection contract: `Core/OpenWakeWordCompatibilityPatcher.cs`
- Process.Start diagnostics compatibility patch: `Core/ProcessStartCompatibilityPatcher.cs`

## Documentation Index

- Active docs index: [docs/README.md](docs/README.md)
- Project structure: [docs/PROJECT_STRUCTURE.md](docs/PROJECT_STRUCTURE.md)
- Live runtime testing: [LIVE-TESTING.md](LIVE-TESTING.md)
- Command testing: [TEST_COMMANDS.md](TEST_COMMANDS.md)
- Optimization/tuning: [OPTIMIZATION_GUIDE.md](OPTIMIZATION_GUIDE.md)
