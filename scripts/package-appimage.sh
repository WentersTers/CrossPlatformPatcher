#!/bin/bash
#
# package-appimage.sh
# Packages the linux-x64 SetupWizard build into an AppImage.
#
# Prerequisites:
#   - appimagetool (https://github.com/AppImage/AppImageKit/releases)
#   - The linux-x64 build must exist (run build-setupwizard-all.sh first)
#
# Usage: bash scripts/package-appimage.sh [path-to-appimagetool]
#

set -euo pipefail

SCRIPT_DIR="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
PROJECT_DIR="$(cd "$SCRIPT_DIR/.." && pwd)"

# Config
PUBLISH_DIR="$PROJECT_DIR/SetupWizardWindows/bin/Release/net8.0/linux-x64/publish"
APPIMAGETOOL="${1:-appimagetool-x86_64.AppImage}"
APP_DIR="$PROJECT_DIR/SetupWizard.AppDir"
OUTPUT_NAME="SetupWizard-x86_64.AppImage"

echo "=============================================="
echo "  SetupWizard AppImage Packaging"
echo "=============================================="
echo ""

# Check prerequisites
if [ ! -d "$PUBLISH_DIR" ]; then
    echo "ERROR: Linux build not found at: $PUBLISH_DIR"
    echo "Run 'bash scripts/build-setupwizard-all.sh' first."
    exit 1
fi

if [ ! -f "$APPIMAGETOOL" ] && ! command -v appimagetool &> /dev/null; then
    echo "WARNING: appimagetool not found at '$APPIMAGETOOL' or in PATH."
    echo "Download it from: https://github.com/AppImage/AppImageKit/releases"
    echo "Then run: bash $0 /path/to/appimagetool-x86_64.AppImage"
    exit 1
fi

# Use system appimagetool if available and no path provided
if [ ! -f "$APPIMAGETOOL" ]; then
    APPIMAGETOOL="$(command -v appimagetool)"
fi

echo "Using appimagetool: $APPIMAGETOOL"
echo ""

# Clean any previous AppDir
rm -rf "$APP_DIR"

# Create AppDir structure
echo "Creating AppDir structure..."
mkdir -p "$APP_DIR/usr/bin"

# Copy published binaries
echo "Copying binaries from: $PUBLISH_DIR"
cp -r "$PUBLISH_DIR"/* "$APP_DIR/usr/bin/"

# Create AppRun entry script
cat > "$APP_DIR/AppRun" << 'RUNEOF'
#!/bin/bash
exec "$APPDIR/usr/bin/SetupWizard" "$@"
RUNEOF
chmod +x "$APP_DIR/AppRun"

# Create Desktop entry
cat > "$APP_DIR/SetupWizard.desktop" << 'DESKTOPEOF'
[Desktop Entry]
Name=CrossPlatformPatcher SetupWizard
Comment=Install and configure PAIcom for cross-platform play
Exec=SetupWizard
Icon=SetupWizard
Terminal=false
Type=Application
Categories=Game;Utility;
DESKTOPEOF

# Create a placeholder icon (256x256 PNG)
# A real icon should be placed here
if [ ! -f "$APP_DIR/SetupWizard.png" ]; then
    echo "NOTE: No SetupWizard.png found, creating a minimal placeholder."
    echo "Replace 'SetupWizard.png' with a proper 256x256 icon."
    # Create minimal 1x1 transparent PNG as placeholder
    printf '\x89PNG\r\n\x1a\n\x00\x00\x00\rIHDR\x00\x00\x00\x01\x00\x00\x00\x01\x08\x06\x00\x00\x00\x1f\x15\xc4\x89\x00\x00\x00\x0eIDATx\x9cc\xf8\x0f\x00\x00\x01\x01\x00\x05\x18\xd8N\x00\x00\x00\x00IEND\xae\x42\x60\x82' > "$APP_DIR/SetupWizard.png"
fi

# Build the AppImage
echo ""
echo "Building AppImage..."
ARCH=x86_64 "$APPIMAGETOOL" "$APP_DIR" "$OUTPUT_NAME"

echo ""
echo "✓ AppImage created: $OUTPUT_NAME"
echo ""
echo "You can run it with:"
echo "  chmod +x $OUTPUT_NAME && ./$OUTPUT_NAME"
