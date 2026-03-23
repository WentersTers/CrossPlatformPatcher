#!/bin/bash
# Code Quality Validation Hook
# Runs at SessionEnd to validate tests, build, .gitignore, and instructions
# Sends warnings to agent via JSON output

set -e

PROJECT_ROOT="$(cd "$(dirname "${BASH_SOURCE[0]}")/../.." && pwd)"
cd "$PROJECT_ROOT"

WARNINGS=()
CHECKS_PASSED=0
CHECKS_TOTAL=4

# 1. Check if tests pass
if ! dotnet test CrossPlatformPatcher.Tests/CrossPlatformPatcher.Tests.csproj -c Release --verbosity minimal &>/dev/null; then
  WARNINGS+=("❌ Tests did not pass. Run: dotnet test CrossPlatformPatcher.Tests/CrossPlatformPatcher.Tests.csproj -c Release")
else
  ((CHECKS_PASSED++))
fi

# 2. Check if build succeeds
if ! dotnet build CrossPlatformPatcher.csproj -c Release &>/dev/null; then
  WARNINGS+=("❌ Build failed. Run: dotnet build CrossPlatformPatcher.csproj -c Release")
else
  ((CHECKS_PASSED++))
fi

# 3. Check .gitignore for tracked artifacts (bin/, obj/, publish/)
TRACKED_ARTIFACTS=$(git ls-files bin/ obj/ publish/ 2>/dev/null | wc -l)
if [ "$TRACKED_ARTIFACTS" -gt 0 ]; then
  WARNINGS+=("⚠️  Found $(echo $TRACKED_ARTIFACTS) tracked build artifacts. Add to .gitignore: bin/, obj/, publish/, *.onnx (if local)")
else
  ((CHECKS_PASSED++))
fi

# 4. Validate copilot-instructions.md exists and is non-empty
if [ ! -s .github/copilot-instructions.md ]; then
  WARNINGS+=("⚠️  copilot-instructions.md is missing or empty. Update with project guidelines.")
else
  ((CHECKS_PASSED++))
fi

# Build system message
if [ ${#WARNINGS[@]} -eq 0 ]; then
  SYSTEM_MESSAGE="✅ Code quality checks passed (${CHECKS_PASSED}/${CHECKS_TOTAL}). Ready to conclude session."
else
  SYSTEM_MESSAGE="⚠️  Code quality review for session end (${CHECKS_PASSED}/${CHECKS_TOTAL} checks passed):\n$(printf '%s\n' "${WARNINGS[@]}")"
fi

# Output JSON for agent consumption
cat <<EOF
{
  "hookSpecificOutput": {
    "hookEventName": "Stop",
    "systemMessage": "$SYSTEM_MESSAGE"
  }
}
EOF
