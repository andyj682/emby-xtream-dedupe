#!/usr/bin/env bash
set -euo pipefail

SCRIPT_DIR="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
OUT_DIR="$SCRIPT_DIR/out"
DLL_NAME="Emby.Xtream.Plugin.dll"

# Derive version from the fork's OWN git tags, prefixed "dedupe-v" so they never collide with
# upstream's v* release tags (which live in this repo via the upstream remote). --match keeps
# git describe from ever picking up an upstream tag.
#   On tag dedupe-v1.2.0   -> 1.2.0
#   3 commits after tag    -> 1.2.0.3  (always higher than last release)
#   No dedupe-v tag        -> 0.0.1
GIT_DESC=$(git -C "$SCRIPT_DIR" describe --tags --match 'dedupe-v*' 2>/dev/null || echo "")
if [ -z "$GIT_DESC" ]; then
    VERSION="0.0.1"
elif echo "$GIT_DESC" | grep -qE -- '-[0-9]+-g[0-9a-f]+$'; then
    # N commits after a tag: dedupe-v1.2.0-3-gabcdef -> 1.2.0.3
    BASE=$(echo "$GIT_DESC" | sed 's/^dedupe-v//' | sed 's/-[0-9]*-g[0-9a-f]*$//')
    COMMITS=$(echo "$GIT_DESC" | grep -oE -- '-[0-9]+-g[0-9a-f]+$' | cut -d'-' -f2)
    VERSION="${BASE}.${COMMITS}"
else
    # Exactly on tag: dedupe-v1.2.0 -> 1.2.0
    VERSION="${GIT_DESC#dedupe-v}"
fi

echo "=== Version: $VERSION (from git: $GIT_DESC) ==="

echo ""
echo "=== Running Tests ==="
dotnet test "$SCRIPT_DIR/../Emby.Xtream.Plugin.Tests/" --no-restore -v minimal

echo ""
echo "=== Building Emby.Xtream.Plugin ==="
cd "$SCRIPT_DIR"

dotnet publish -c Release -o "$OUT_DIR" --no-self-contained -p:Version="$VERSION"

echo ""
echo "=== Build output ==="
ls -la "$OUT_DIR/$DLL_NAME"

echo ""
echo "DLL ready at: $OUT_DIR/$DLL_NAME (v$VERSION)"
echo ""
echo "To deploy to Emby:"
echo "  docker cp $OUT_DIR/$DLL_NAME <container>:/config/plugins/"
echo "  docker restart <container>"
