# Live Testing

Authoritative guide for live runtime testing against a running PAIcom instance.

## Purpose

Live testing keeps the game process running while commands are injected through the normal runtime flow. This avoids the old restart-per-test pattern and gives methods enough time to initialize.

## Primary Workflow

```sh
./build-patch-and-launch.sh --migration-mode full --runtime-diagnostic
```

For command injection testing (without voice capture), use:

```sh
./build-patch-and-launch.sh --migration-mode full --test-commands --test-duration 30
```

## Script-Based Live Method Test Harness

```sh
./live-method-tester.sh
```

This harness:
1. Starts PAIcom once.
2. Waits for startup/initialization.
3. Sends a fixed sequence of test commands.
4. Captures method-level pass/fail outcomes.
5. Writes a summary report.

## Environment Variables

```sh
PAICOM_LIVE_METHOD_TEST=1
PAICOM_LIVE_TEST_LOG=/absolute/path/to/live-method-test.log
```

## Output Files

All key logs are written under `PAIcom_Player_Folder/diagnostics/`:
- `live-method-test-report.txt`
- `live-method-test.log`
- `paicom-live-startup.log`

Launcher/runtime diagnostics are also written in the player folder:
- `launcher.log`
- `launcher-runtime.log`

## Quick Validation Commands

```sh
# Build + tests
./build-and-test.sh

# Build project only
dotnet build CrossPlatformPatcher.csproj -c Release

# Run test project
dotnet test CrossPlatformPatcher.Tests/CrossPlatformPatcher.Tests.csproj -c Release

# Documentation parity smoke check
./build-patch-and-launch.sh --migration-mode full --no-launch
```

## Troubleshooting

- If methods are not logged, verify initialization completed before issuing commands.
- If no voice behavior appears, review `launcher-runtime.log` for `[oww]` and `[compat][speech]` lines.
- If dispatch fails in test-command mode, validate command manifests and handler discovery logs.

## Related Docs

- `TEST_COMMANDS.md` for command injection details.
- `README.md` for the canonical end-to-end build/patch workflow.
