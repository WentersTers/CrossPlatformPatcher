#!/usr/bin/env bash

set -euo pipefail

SCRIPT_DIR="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
ZIP_DIR="$SCRIPT_DIR/VoskModels/zips"
OUT_DIR="$SCRIPT_DIR/PAIcom_Player_Folder/models"

mkdir -p "$ZIP_DIR"
mkdir -p "$OUT_DIR"

if ! command -v unzip >/dev/null 2>&1; then
    echo "[model-prep] unzip not found; skipping Vosk model extraction"
    exit 0
fi

shopt -s nullglob
zips=("$ZIP_DIR"/*.zip)

if [[ ${#zips[@]} -eq 0 ]]; then
    echo "[model-prep] No model zip files found in $ZIP_DIR"
    exit 0
fi

processed=0
for zip_path in "${zips[@]}"; do
    zip_name="$(basename "$zip_path" .zip)"
    tmp_dir="$OUT_DIR/.tmp-$zip_name"

    rm -rf "$tmp_dir"
    mkdir -p "$tmp_dir"

    unzip -oq "$zip_path" -d "$tmp_dir"

    # Remove Finder metadata directories that can confuse model detection.
    find "$tmp_dir" -type d -name "__MACOSX" -prune -exec rm -rf {} + 2>/dev/null || true

    model_src=""
    while IFS= read -r candidate; do
        if [[ -d "$candidate/am" && -d "$candidate/conf" && -d "$candidate/graph" ]]; then
            model_src="$candidate"
            break
        fi
    done < <(find "$tmp_dir" -type d | sort)

    if [[ -z "$model_src" ]]; then
        echo "[model-prep] Warning: no valid Vosk model dir (am/conf/graph) found in $(basename "$zip_path"); skipping"
        rm -rf "$tmp_dir"
        continue
    fi

    model_name="$(basename "$model_src")"

    model_dest="$OUT_DIR/$model_name"
    rm -rf "$model_dest"
    mv "$model_src" "$model_dest"
    rm -rf "$tmp_dir"

    echo "[model-prep] Installed model: $model_name (from $(basename "$zip_path"))"
    processed=$((processed + 1))
done

echo "[model-prep] Prepared $processed model archive(s) into $OUT_DIR"