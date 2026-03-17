#!/usr/bin/env sh
# publish-all.sh
# Publishes CrossPlatformPatcher for all supported runtime identifiers:
#   win-x64, linux-x64, osx-x64, osx-arm64
# Run from the repository root:  sh publish-all.sh
set -e

OUT="publish"
PROJECT="CrossPlatformPatcher.csproj"
RIDS="win-x64 linux-x64 osx-x64 osx-arm64"
COMMON="-c Release --self-contained true -p:PublishSingleFile=true"

for RID in $RIDS; do
    echo ""
    echo "==> Publishing CrossPlatformPatcher for $RID ..."
    dotnet publish "$PROJECT" $COMMON -r "$RID" -o "$OUT/CrossPlatformPatcher/$RID"
    echo "    Done: $OUT/CrossPlatformPatcher/$RID"
done

echo ""
echo "All builds complete:"
for RID in $RIDS; do
    echo "  $OUT/CrossPlatformPatcher/$RID"
done
