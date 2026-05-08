#!/bin/bash
# Build script for SetupWizard macOS app

set -euo pipefail

SCRIPT_DIR="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
PROJECT_ROOT="$(cd "$SCRIPT_DIR/.." && pwd)"

# Configuration
BUILD_DIR="$SCRIPT_DIR/build"
RELEASE_DIR="$BUILD_DIR/Release"
APP_NAME="SetupWizard"
BUNDLE_ID="com.github.crossplatformpatcher.setupwizard"

echo "Building SetupWizard macOS app..."
echo "Script dir: $SCRIPT_DIR"
echo "Project root: $PROJECT_ROOT"

# Create build directory
mkdir -p "$BUILD_DIR"

# Build using Swift Package Manager
echo "Compiling with swiftpm..."
cd "$SCRIPT_DIR"
swift build -c release --static-swift-stdlib

# The executable is now at .build/release/SetupWizard
# Create .app bundle structure
echo "Creating app bundle..."
mkdir -p "$RELEASE_DIR/$APP_NAME.app/Contents/MacOS"
mkdir -p "$RELEASE_DIR/$APP_NAME.app/Contents/Resources"
mkdir -p "$RELEASE_DIR/$APP_NAME.app/Contents/Frameworks"

# Copy executable
cp ".build/release/$APP_NAME" "$RELEASE_DIR/$APP_NAME.app/Contents/MacOS/$APP_NAME"
chmod +x "$RELEASE_DIR/$APP_NAME.app/Contents/MacOS/$APP_NAME"

# Copy winetricks resource if it exists
if [ -f "$SCRIPT_DIR/Resources/winetricks" ]; then
    echo "Bundling winetricks..."
    cp "$SCRIPT_DIR/Resources/winetricks" "$RELEASE_DIR/$APP_NAME.app/Contents/Resources/"
    chmod +x "$RELEASE_DIR/$APP_NAME.app/Contents/Resources/winetricks"
fi

# Create Info.plist
cat > "$RELEASE_DIR/$APP_NAME.app/Contents/Info.plist" <<EOF
<?xml version="1.0" encoding="UTF-8"?>
<!DOCTYPE plist PUBLIC "-//Apple//DTD PLIST 1.0//EN" "http://www.apple.com/DTDs/PropertyList-1.0.dtd">
<plist version="1.0">
<dict>
    <key>CFBundleDevelopmentRegion</key>
    <string>en</string>
    <key>CFBundleExecutable</key>
    <string>$APP_NAME</string>
    <key>CFBundleIdentifier</key>
    <string>$BUNDLE_ID</string>
    <key>CFBundleInfoDictionaryVersion</key>
    <string>6.0</string>
    <key>CFBundleName</key>
    <string>$APP_NAME</string>
    <key>CFBundlePackageType</key>
    <string>APPL</string>
    <key>CFBundleShortVersionString</key>
    <string>1.0</string>
    <key>CFBundleVersion</key>
    <string>1</string>
    <key>NSPrincipalClass</key>
    <string>NSApplication</string>
    <key>NSHighResolutionCapable</key>
    <true/>
    <key>NSHumanReadableCopyright</key>
    <string>© 2026 CrossPlatformPatcher Contributors</string>
    <key>NSSupportsAutomaticGraphicsSwitching</key>
    <true/>
</dict>
</plist>
EOF

# Code sign the app bundle (required for execution on macOS with SIP enabled)
echo "Code signing app bundle..."
codesign --force --deep --sign - "$RELEASE_DIR/$APP_NAME.app"

echo "✓ App bundle created: $RELEASE_DIR/$APP_NAME.app"
echo ""
echo "Build complete!"
echo "App location: $RELEASE_DIR/$APP_NAME.app"
echo ""
echo "To test: open \"$RELEASE_DIR/$APP_NAME.app\""
