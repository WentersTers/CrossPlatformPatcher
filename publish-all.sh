#!/usr/bin/env sh
# publish-all.sh
# Publishes both product variants:
#   1) PAIcomPatcher.HotSwap.Win (win-x64 only)
#   2) PAIcomPatcher.Compat (win-x64, linux-x64, osx-x64, osx-arm64)
# Run from the repository root:  sh publish-all.sh
set -e

OUT="publish"
COMPAT_RIDS="win-x64 linux-x64 osx-x64 osx-arm64"
COMMON="-c Release --self-contained true -p:PublishSingleFile=true"

echo ""
echo "==> Publishing PAIcomPatcher.HotSwap.Win for win-x64 ..."
dotnet publish "PAIcomPatcher.HotSwap.Win.csproj" $COMMON -r "win-x64" -o "$OUT/PAIcomPatcher.HotSwap.Win/win-x64"
echo "    Done: $OUT/PAIcomPatcher.HotSwap.Win/win-x64"

for RID in $COMPAT_RIDS; do
    echo ""
    echo "==> Publishing PAIcomPatcher.Compat for $RID ..."
    dotnet publish "PAIcomPatcher.Compat.csproj" $COMMON -r "$RID" -o "$OUT/PAIcomPatcher.Compat/$RID"
    echo "    Done: $OUT/PAIcomPatcher.Compat/$RID"
done

echo ""
echo "All builds complete:"
echo "  $OUT/PAIcomPatcher.HotSwap.Win/win-x64"
for RID in $COMPAT_RIDS; do
    echo "  $OUT/PAIcomPatcher.Compat/$RID"
done
