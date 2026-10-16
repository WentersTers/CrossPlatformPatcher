#!/usr/bin/env bash

set -euo pipefail

SCRIPT_DIR="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
REPO_ROOT="$(cd "$SCRIPT_DIR/.." && pwd)"
PROJECT_FILE="$REPO_ROOT/CrossPlatformPatcher.csproj"
TEST_PROJECT_FILE="$REPO_ROOT/CrossPlatformPatcher.Tests/CrossPlatformPatcher.Tests.csproj"
PARITY_FILTER="FullyQualifiedName~RuntimeHelperParityTests"
PATCH_WORKFLOW_ARGS=(--migration-mode full --no-launch)

printf '\n==> Runtime helper parity check\n'
dotnet test "$TEST_PROJECT_FILE" -c Release --filter "$PARITY_FILTER"

printf '\n==> Building patcher\n'
dotnet build "$PROJECT_FILE" -c Release

printf '\n==> Running full test suite\n'
dotnet test "$TEST_PROJECT_FILE" -c Release

if [[ "${RUN_PATCH_WORKFLOW:-0}" == "1" ]]; then
    if [[ -x "$SCRIPT_DIR/build-patch-and-launch.sh" ]]; then
        printf '\n==> Running patch workflow (%s)\n' "${PATCH_WORKFLOW_ARGS[*]}"
        "$SCRIPT_DIR/build-patch-and-launch.sh" "${PATCH_WORKFLOW_ARGS[@]}"
    elif [[ -f "$SCRIPT_DIR/build-patch-and-launch.sh" ]]; then
        printf '\n==> Running patch workflow via bash (%s)\n' "${PATCH_WORKFLOW_ARGS[*]}"
        bash "$SCRIPT_DIR/build-patch-and-launch.sh" "${PATCH_WORKFLOW_ARGS[@]}"
    else
        printf '\n[build-and-test] build-patch-and-launch.sh not found; skipping patch workflow.\n'
    fi
else
    printf '\n[build-and-test] Skipping patch workflow. Set RUN_PATCH_WORKFLOW=1 to enable.\n'
fi

printf '\n[build-and-test] Completed successfully.\n'
