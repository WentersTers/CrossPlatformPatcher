# Documentation Map

This folder is the active documentation index for developers and AI agents working in this repository.

## Start Here

- [Root README](../README.md): product overview, build/publish/launcher usage, runtime tuning.
- [Project Structure](PROJECT_STRUCTURE.md): code layout, critical entry points, and feature ownership.
- [Quick Reference](../QUICK_REFERENCE.md): fastest command-level cheat sheet for daily work.

## Active Workflow Docs

- [Live Testing](../LIVE-TESTING.md): runtime speech/dispatch testing while the game is running.
- [Command Injection Guide](../COMMAND_INJECTION_GUIDE.md): scripted command tests and injection flow.
- [Test Commands](../TEST_COMMANDS.md): command testing options and examples.
- [Test Runner Guide](../TEST_RUNNER_GUIDE.md): deep-dive on automated command tests.
- [Optimization Guide](../OPTIMIZATION_GUIDE.md): tuning latency/accuracy and diagnostics.
- [Cross-Platform Integration](../CROSS-PLATFORM-INTEGRATION.md): launcher/runtime behavior across OS targets.

## Feature/Reference Docs

- [Keyword Matching](../KEYWORD_MATCHING.md): keyword and phrase matching strategy.
- [Command Categories](../COMMAND_CATEGORIES.md): command taxonomy and coverage.
- [Animation Script Documentation](../ANIMATION_SCRIPT_DOCUMENTATION.md): animation command scripting details.
- [Animation Methods Reference](../ANIMATION_METHODS_REFERENCE.md): animation method notes and lookup reference.

## Build/Test Workflow

Use [build-and-test.sh](../build-and-test.sh) for the standard developer validation path:

1. Runtime-helper parity check (Core vs PAIcom.OWW critical methods)
2. Release build
3. Full Release tests

Set `RUN_PATCH_WORKFLOW=1` to also run patch workflow validation (`--migration-mode full --no-launch`).

## Archive

Historical and superseded documents are under [docs/archive](archive).

- [Archive Index (2026-04)](archive/2026-04/README.md): moved/superseded docs with rationale.

Use archived docs for research context, not as implementation source-of-truth.
