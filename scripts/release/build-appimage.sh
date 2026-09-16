#!/bin/bash
# build-appimage.sh — AppImage pipeline extension (v0.1.1 content).
#
# Gates mirror release-win.ps1: deterministic publish (done on the build
# host before this runs), reproducible squashfs, smoke test, stage+hash.
#
# Reproducibility contract: fixed SOURCE_DATE_EPOCH (clamps mtimes),
# -all-root (uid/gid), pinned --runtime-file (no network at build time).
# AppImage is then byte-identical across rebuilds; the script proves it by
# building twice and comparing. If the two hashes ever differ, the script
# fails and the release falls back to a single-build hash with the
# determinism claim scoped to Windows only (see release notes).
#
# Usage (from repo root, inside WSL):
#   bash scripts/release/build-appimage.sh <publish-dir> <work-dir> <out-appimage>
#
# Env:
#   SOURCE_DATE_EPOCH  fixed build timestamp (default 1757980800 = 2026-09-16Z)
#   APPIMAGETOOL       path to appimagetool AppImage (default /tmp/appimage-tools/...)
#   RUNTIME_FILE       pinned type2 runtime (downloaded once if absent)
set -euo pipefail

PUBLISH_DIR="${1:?publish dir required}"
WORK_DIR="${2:?work dir required}"
OUT_IMAGE="${3:?output AppImage path required}"
EPOCH="${SOURCE_DATE_EPOCH:-1757980800}"
APPIMAGETOOL="${APPIMAGETOOL:-/tmp/appimage-tools/appimagetool-x86_64.AppImage}"
RUNTIME_FILE="${RUNTIME_FILE:-/tmp/appimage-tools/runtime-x86_64}"
export SOURCE_DATE_EPOCH="$EPOCH"

APPDIR="$WORK_DIR/AppDir"
mkdir -p "$APPDIR/usr/bin"

echo "== staging AppDir (epoch $EPOCH) =="
rm -rf "$APPDIR"
mkdir -p "$APPDIR/usr/bin"
cp "$PUBLISH_DIR/CrossPlatformPatcher" "$APPDIR/usr/bin/"
cp "$PUBLISH_DIR"/*.so* "$APPDIR/usr/bin/" 2>/dev/null || true
cp "$PUBLISH_DIR"/*.dll "$APPDIR/usr/bin/" 2>/dev/null || true
chmod +x "$APPDIR/usr/bin/CrossPlatformPatcher"
touch -d "@$EPOCH" "$APPDIR/usr/bin/CrossPlatformPatcher"

cat > "$APPDIR/AppRun" << 'RUNEOF'
#!/bin/sh
exec "$APPDIR/usr/bin/CrossPlatformPatcher" "$@"
RUNEOF
chmod +x "$APPDIR/AppRun"

cat > "$APPDIR/CrossPlatformPatcher.desktop" << 'DESKTOPEOF'
[Desktop Entry]
Name=CrossPlatformPatcher
Comment=Patch PAIcom with the offline voice layer
Exec=CrossPlatformPatcher
Icon=CrossPlatformPatcher
Terminal=true
Type=Application
Categories=Game;Utility;
DESKTOPEOF

if [ ! -f "$APPDIR/CrossPlatformPatcher.png" ]; then
  # Placeholder 1x1 PNG. Replace with a proper product-neutral icon.
  printf '\x89PNG\r\n\x1a\n\x00\x00\x00\rIHDR\x00\x00\x00\x01\x00\x00\x00\x01\x08\x06\x00\x00\x00\x1f\x15\xc4\x89\x00\x00\x00\x0eIDATx\x9cc\xf8\x0f\x00\x00\x01\x01\x00\x05\x18\xd8N\x00\x00\x00\x00IEND\xae\x42\x60\x82' > "$APPDIR/CrossPlatformPatcher.png"
fi
touch -d "@$EPOCH" "$APPDIR/AppRun" "$APPDIR/CrossPlatformPatcher.desktop" "$APPDIR/CrossPlatformPatcher.png"

if [ ! -f "$RUNTIME_FILE" ]; then
  echo "== first run: letting appimagetool fetch the runtime =="
  ARCH=x86_64 "$APPIMAGETOOL" --comp zstd "$APPDIR" "$WORK_DIR/first.AppImage" > "$WORK_DIR/first.log" 2>&1 || {
    echo "appimagetool first run failed:"; tail -20 "$WORK_DIR/first.log"; exit 1;
  }
  CANDIDATE="$(find ~/.cache -name 'runtime-*' -newer "$APPDIR/AppRun" 2>/dev/null | head -1 || true)"
  if [ -z "$CANDIDATE" ]; then
    echo "WARNING: runtime cache not found; continuing single-build (no pin)."
    cp "$WORK_DIR/first.AppImage" "$OUT_IMAGE"
    sha256sum "$OUT_IMAGE" | tee "$OUT_IMAGE.sha256"
    echo "SINGLE-BUILD (runtime unpinned)"
    exit 0
  fi
  cp "$CANDIDATE" "$RUNTIME_FILE"
  echo "pinned runtime: $RUNTIME_FILE"
fi

build_once() {
  local dest="$1"
  rm -f "$dest"
  ARCH=x86_64 "$APPIMAGETOOL" --comp zstd \
    --runtime-file "$RUNTIME_FILE" \
    --mksquashfs-opt "-all-root" \
    "$APPDIR" "$dest" > "$WORK_DIR/build.log" 2>&1 || {
    echo "appimagetool failed:"; tail -20 "$WORK_DIR/build.log"; exit 1;
  }
}

echo "== reproducible build A =="
build_once "$WORK_DIR/A.AppImage"
echo "== reproducible build B =="
build_once "$WORK_DIR/B.AppImage"

HA="$(sha256sum "$WORK_DIR/A.AppImage" | cut -d' ' -f1)"
HB="$(sha256sum "$WORK_DIR/B.AppImage" | cut -d' ' -f1)"
echo "A=$HA"
echo "B=$HB"
if [ "$HA" != "$HB" ]; then
  echo "APPIMAGE-NONDETERMINISTIC (hash covers this build only)"
  cp "$WORK_DIR/A.AppImage" "$OUT_IMAGE"
  sha256sum "$OUT_IMAGE" | tee "$OUT_IMAGE.sha256"
  exit 2
fi
cp "$WORK_DIR/A.AppImage" "$OUT_IMAGE"
sha256sum "$OUT_IMAGE" | tee "$OUT_IMAGE.sha256"
echo "APPIMAGE-DETERMINISTIC-PASS"
