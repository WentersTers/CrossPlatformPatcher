#!/bin/bash
# Pre-Commit Validation Hook
# Checks staged changes for forbidden files, PAIcom source material, and code quality
# Exits with status 0 if all checks pass, non-zero if any check fails

set -e

PROJECT_ROOT="$(cd "$(dirname "${BASH_SOURCE[0]}")/../.." && pwd)"
cd "$PROJECT_ROOT"

ERRORS=()
WARNINGS=()
CHECKS_PASSED=0
CHECKS_TOTAL=5

echo "🔍 Running pre-commit checks..."

# 1. Check for forbidden binary files (.exe, .dll from PAIcom)
echo "  [1/5] Checking for forbidden executable files..."
FORBIDDEN_FILES=$(git diff --cached --name-only | grep -E '\.(exe|dll)$' | grep -iE '(PAIcom|patched)' || true)
if [ -n "$FORBIDDEN_FILES" ]; then
  ERRORS+=("❌ Forbidden executable files detected in staged changes: $FORBIDDEN_FILES")
else
  ((CHECKS_PASSED++))
fi

# 2. Check for decompiled/reverse-engineered folders
echo "  [2/5] Checking for decompiled source material..."
DECOMPILED_MARKERS=$(git diff --cached --name-only | grep -iE '(decompiled|extracted|reverse-engineered|\.g\.cs)' || true)
if [ -n "$DECOMPILED_MARKERS" ]; then
  ERRORS+=("❌ Decompiled/extracted source code detected: $DECOMPILED_MARKERS")
else
  ((CHECKS_PASSED++))
fi

# 3. Check for large binary files (over 10MB)
echo "  [3/5] Checking for oversized files..."
LARGE_FILES=$(git diff --cached --name-only --diff-filter=A | while read file; do
  if [ -f "$file" ]; then
    SIZE=$(stat -f%z "$file" 2>/dev/null || stat -c%s "$file" 2>/dev/null || echo 0)
    if [ "$SIZE" -gt 10485760 ]; then
      echo "$file ($((SIZE / 1048576))MB)"
    fi
  fi
done)
if [ -n "$LARGE_FILES" ]; then
  WARNINGS+=("⚠️  Large files detected (consider using Git LFS): $LARGE_FILES")
else
  ((CHECKS_PASSED++))
fi

# 4. Check code formatting (dotnet format --verify-no-changes)
echo "  [4/5] Checking code formatting..."
if ! dotnet format CrossPlatformPatcher.csproj --verify-no-changes --verbosity quiet &>/dev/null; then
  WARNINGS+=("⚠️  Code formatting issues detected. Run: dotnet format CrossPlatformPatcher.csproj")
else
  ((CHECKS_PASSED++))
fi

# 5. Verify no PAIcom.exe in any form is committed
echo "  [5/5] Checking for PAIcom binaries..."
PAICOM_BINARIES=$(git diff --cached --name-only | grep -i paicom || true)
if [ -n "$PAICOM_BINARIES" ]; then
  ERRORS+=("❌ PAIcom binaries detected in staged changes: $PAICOM_BINARIES. Remove with: git reset HEAD <file>")
else
  ((CHECKS_PASSED++))
fi

# Report results
echo ""
echo "━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━"
if [ ${#ERRORS[@]} -gt 0 ]; then
  echo "❌ PRE-COMMIT FAILED (${CHECKS_PASSED}/${CHECKS_TOTAL} checks passed)"
  echo ""
  for error in "${ERRORS[@]}"; do
    echo "$error"
  done
  [ ${#WARNINGS[@]} -gt 0 ] && echo "" && echo "Warnings:" && for warning in "${WARNINGS[@]}"; do echo "$warning"; done
  exit 1
elif [ ${#WARNINGS[@]} -gt 0 ]; then
  echo "⚠️  PRE-COMMIT PASSED WITH WARNINGS (${CHECKS_PASSED}/${CHECKS_TOTAL})"
  echo ""
  for warning in "${WARNINGS[@]}"; do
    echo "$warning"
  done
  exit 0
else
  echo "✅ PRE-COMMIT PASSED (${CHECKS_PASSED}/${CHECKS_TOTAL} checks)"
  exit 0
fi
