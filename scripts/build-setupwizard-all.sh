#!/bin/bash
#
# build-setupwizard-all.sh
# Builds the cross-platform SetupWizard for all target platforms.
#
# Usage: bash scripts/build-setupwizard-all.sh [--no-restore]
#

set -euo pipefail

SCRIPT_DIR="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
PROJECT_DIR="$(cd "$SCRIPT_DIR/.." && pwd)"
SETUP_CSPROJ="$PROJECT_DIR/SetupWizardWindows/SetupWizardWindows.csproj"
RESTORE_FLAG="${1:+--no-restore}"

echo "=============================================="
echo "  CrossPlatformPatcher SetupWizard Build"
echo "=============================================="
echo ""

build_platform() {
    local rid="$1"
    local description="$2"

    echo "--- Building for: $description ($rid) ---"
    dotnet publish "$SETUP_CSPROJ" \
        -c Release \
        -r "$rid" \
        --self-contained true \
        $RESTORE_FLAG \
        -p:PublishSingleFile=true

    echo "✓ Build complete: $rid"
    echo ""
}

# Build for all target platforms
build_platform "win-x64"     "Windows x64"
build_platform "linux-x64"   "Linux x64"
build_platform "osx-x64"     "macOS x64 (Intel)"
build_platform "osx-arm64"   "macOS ARM64 (Apple Silicon)"

echo "=============================================="
echo "  All builds completed successfully!"
echo "=============================================="
echo ""
echo "Published binaries:"
echo "  $PROJECT_DIR/SetupWizardWindows/bin/Release/net8.0/win-x64/publish/"
echo "  $PROJECT_DIR/SetupWizardWindows/bin/Release/net8.0/linux-x64/publish/"
echo "  $PROJECT_DIR/SetupWizardWindows/bin/Release/net8.0/osx-x64/publish/"
echo "  $PROJECT_DIR/SetupWizardWindows/bin/Release/net8.0/osx-arm64/publish/"
