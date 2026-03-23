#!/usr/bin/env sh
# publish-all.sh
# Publishes CrossPlatformPatcher for all supported runtime identifiers:
#   win-x64, linux-x64, osx-x64, osx-arm64
# Run from the repository root:  sh publish-all.sh
set -e

OUT="publish"
PROJECT="CrossPlatformPatcher.csproj"
declare -A RIDS=("win-x64:W-x64" "linux-x64:L-x64" "osx-x64:M-x64" "osx-arm64:M-Arm")
COMMON="-c Release --self-contained true -p:PublishSingleFile=true"

for RID in "${!RIDS[@]}"; do
    SUFFIX="${RIDS[$RID]}"
    echo ""
    echo "==> Publishing CrossPlatformPatcher for $RID (CrossPlatformPatcher-$SUFFIX) ..."
    dotnet publish "$PROJECT" $COMMON -r "$RID" -p:AssemblyName="CrossPlatformPatcher-$SUFFIX" -o "$OUT/CrossPlatformPatcher/$RID"
    echo "    Done: $OUT/CrossPlatformPatcher/$RID/CrossPlatformPatcher-$SUFFIX"
done

echo ""
echo "All builds complete:"
echo "  $OUT/CrossPlatformPatcher/win-x64/CrossPlatformPatcher-W-x64"
echo "  $OUT/CrossPlatformPatcher/linux-x64/CrossPlatformPatcher-L-x64"
echo "  $OUT/CrossPlatformPatcher/osx-x64/CrossPlatformPatcher-M-x64"
echo "  $OUT/CrossPlatformPatcher/osx-arm64/CrossPlatformPatcher-M-Arm"
