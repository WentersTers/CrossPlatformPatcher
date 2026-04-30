#!/usr/bin/env bash

set -euo pipefail

SCRIPT_DIR="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
if [[ "$(uname -s 2>/dev/null || echo unknown)" != "Darwin" ]]; then
    echo "This script must be run on macOS." >&2
    exit 1
fi

require_tool() {
    command -v "$1" >/dev/null 2>&1 || {
        echo "Required tool not found: $1" >&2
        exit 1
    }
}

require_tool pkgbuild
require_tool productbuild
require_tool curl
require_tool python3

PACKAGE_VERSION="$(git -C "$SCRIPT_DIR" describe --tags --always --dirty 2>/dev/null || true)"
PACKAGE_VERSION="${PACKAGE_VERSION// /}"
PACKAGE_VERSION="${PACKAGE_VERSION:-0.0.0}"

BUILD_ROOT="$SCRIPT_DIR/build/mac-pkg"
PAYLOAD_ROOT="$BUILD_ROOT/payload"
PKG_DIR="$BUILD_ROOT/packages"
DIST_DIR="$BUILD_ROOT/dist"
INSTALL_ROOT="$PAYLOAD_ROOT/Applications/CrossPlatformPatcher"
COMPONENT_PKG="$PKG_DIR/CrossPlatformPatcher-SetupWizard.component.pkg"
FINAL_PKG="$DIST_DIR/CrossPlatformPatcher-SetupWizard-$PACKAGE_VERSION.pkg"
DIST_XML="$BUILD_ROOT/Distribution.xml"
IDENTIFIER="com.github.crossplatformpatcher.setupwizard"

rm -rf "$BUILD_ROOT"
mkdir -p "$INSTALL_ROOT" "$PKG_DIR" "$DIST_DIR"

copy_if_exists() {
    local source_path="$1"
    local destination_name="$2"
    if [[ -f "$source_path" ]]; then
        cp "$source_path" "$INSTALL_ROOT/$destination_name"
        chmod +x "$INSTALL_ROOT/$destination_name" 2>/dev/null || true
    fi
}

copy_if_exists "$SCRIPT_DIR/pkg-scripts/SetupWizard.command" "SetupWizard.command"
copy_if_exists "$SCRIPT_DIR/SETUP_MAC.md" "SETUP_MAC.md"
copy_if_exists "$SCRIPT_DIR/SETUP_LINUX.md" "SETUP_LINUX.md"
copy_if_exists "$SCRIPT_DIR/setup-wizard.sh" "setup-wizard.sh"
copy_if_exists "$SCRIPT_DIR/setup.command" "setup.command"
copy_if_exists "$SCRIPT_DIR/launch.command" "launch.command"
copy_if_exists "$SCRIPT_DIR/run.sh" "run.sh"

cat > "$DIST_XML" <<EOF
<?xml version="1.0" encoding="utf-8"?>
<installer-gui-script minSpecVersion="1">
    <title>CrossPlatformPatcher Setup Wizard</title>
    <options customize="never" allow-external-scripts="false"/>
    <choices-outline>
        <line choice="default"/>
    </choices-outline>
    <choice id="default" title="CrossPlatformPatcher Setup Wizard">
        <pkg-ref id="$IDENTIFIER"/>
    </choice>
    <pkg-ref id="$IDENTIFIER" version="$PACKAGE_VERSION">CrossPlatformPatcher-SetupWizard.component.pkg</pkg-ref>
</installer-gui-script>
EOF

pkgbuild \
    --root "$PAYLOAD_ROOT" \
    --scripts "$SCRIPT_DIR/pkg-scripts" \
    --identifier "$IDENTIFIER" \
    --version "$PACKAGE_VERSION" \
    --install-location "/" \
    "$COMPONENT_PKG"

productbuild \
    --distribution "$DIST_XML" \
    --package-path "$PKG_DIR" \
    "$FINAL_PKG"

echo "Created package: $FINAL_PKG"