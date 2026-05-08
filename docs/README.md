# Documentation Map

This folder is the active documentation index for developers and AI agents working in this repository.

## Start Here

- [Root README](../README.md): product overview, build/publish/launcher usage, runtime tuning.
- [Project Structure](PROJECT_STRUCTURE.md): code layout, critical entry points, and feature ownership.
- [Quick Start](QUICK_START.md): fastest way to build and test for all platforms.

## Build, Deploy & Installation

**Critical reading for building and distributing the patcher:**

- [Build System](BUILD_SYSTEM.md): complete build pipeline (primary macOS workflows; Linux/Windows resupport in progress)
- [Build Outputs](BUILD_OUTPUTS.md): detailed structure of all build artifacts
- [Installer Guide](INSTALLER_GUIDE.md): macOS native `.app` code signing and distribution
- [macOS Setup](SETUP_MAC.md): user-facing macOS setup documentation

Use [build-and-test.sh](../scripts/build-and-test.sh) for the standard developer validation workflow.

## Active Development Workflow

**Testing and runtime behavior:**

- [Live Testing](LIVE-TESTING.md): runtime speech/dispatch testing while the game is running.
- [Command Injection Guide](COMMAND_INJECTION_GUIDE.md): scripted command tests and injection flow.
- [Test Commands](TEST_COMMANDS.md): command testing options and examples.
- [Test Runner Guide](TEST_RUNNER_GUIDE.md): deep-dive on automated command tests.
- [Optimization Guide](OPTIMIZATION_GUIDE.md): tuning latency/accuracy and diagnostics.
- [Cross-Platform Integration](CROSS-PLATFORM-INTEGRATION.md): launcher/runtime behavior across OS targets.

## Feature & Reference Documentation

**Command behavior and implementation details:**

- [Keyword Matching](KEYWORD_MATCHING.md): keyword and phrase matching strategy.
- [Command Categories](COMMAND_CATEGORIES.md): command taxonomy and coverage.
- [Animation Script Documentation](ANIMATION_SCRIPT_DOCUMENTATION.md): animation command scripting details.
- [Animation Methods Reference](ANIMATION_METHODS_REFERENCE.md): animation method notes and lookup reference.

## Quick Command Reference

For the fastest command-level cheat sheet, see [Quick Reference](../QUICK_REFERENCE.md).

### Minimal 3-Command Workflow

```sh
# 1. Build and run all tests
./scripts/build-and-test.sh

# 2. Build, patch, and launch PAIcom
./scripts/build-patch-and-launch.sh --migration-mode full

# 3. Build macOS SetupWizard.app (optional)
cd SetupWizardMacApp && ./build.sh
```

## Archive

Historical documentation is under [docs/archive/](archive).

- [2026-05 Archive](archive/2026-05/README.md): docs superseded in May 2026
- [2026-04 Archive](archive/2026-04/README.md): earlier superseded docs

**Use archived docs for historical context only,** not as implementation source-of-truth. Active documentation in this folder takes precedence.
