#!/usr/bin/env bash
# Fork-owned full build: one DLL for Emby 4.9 and 4.10, tested against both SDKs, plus the
# guards CI runs.
#
# Since the upstream merge through 91f27d3 (their ADR-017) a single DLL, compiled against the 4.9
# SDK, ships for both Emby versions. Emby.Xtream.Plugin/build.sh is shared with upstream and only
# ever tests against 4.9, so this wrapper adds the 4.10 test run and the same load check the
# release runs, without editing the shared script. CI additionally mutation-tests the delete code
# (Stryker), which this does not.
#
# Run from anywhere:  bash build-dedupe.sh
set -euo pipefail

REPO_ROOT="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
PLUGIN_DIR="$REPO_ROOT/Emby.Xtream.Plugin"
TESTS_DIR="$REPO_ROOT/Emby.Xtream.Plugin.Tests"

# Mirrors the derivation in build.sh. Deliberately duplicated rather than factored out: build.sh
# is upstream-shared, and extracting a helper would put fork-specific structure into a file
# upstream actively maintains. Twelve lines is the cheaper divergence.
GIT_DESC=$(git -C "$REPO_ROOT" describe --tags --match 'dedupe-v*' 2>/dev/null || echo "")
if [ -z "$GIT_DESC" ]; then
    VERSION="0.0.1"
elif echo "$GIT_DESC" | grep -qE -- '-[0-9]+-g[0-9a-f]+$'; then
    BASE=$(echo "$GIT_DESC" | sed 's/^dedupe-v//' | sed 's/-[0-9]*-g[0-9a-f]*$//')
    COMMITS=$(echo "$GIT_DESC" | grep -oE -- '-[0-9]+-g[0-9a-f]+$' | cut -d'-' -f2)
    VERSION="${BASE}.${COMMITS}"
else
    VERSION="${GIT_DESC#dedupe-v}"
fi

echo "=== Full build: version $VERSION (from git: ${GIT_DESC:-none}) ==="

# ---------------------------------------------------------------------------
# 1. Delete-site guard. Cheap, independent of the build, and reports before a
#    compile error can hide it — same ordering CI uses. The guard self-tests
#    first: a guard that cannot reject is the same problem as a test that
#    cannot fail.
# ---------------------------------------------------------------------------
if command -v python3 >/dev/null 2>&1; then
    echo ""
    echo "=== Delete-site guard ==="
    python3 "$REPO_ROOT/scripts/test-check-delete-sites.py"
    python3 "$REPO_ROOT/scripts/check-delete-sites.py"
else
    echo ""
    echo "!!! python3 not found — SKIPPING the delete-site guard."
    echo "!!! CI still enforces it, but this run has not checked it."
fi

# ---------------------------------------------------------------------------
# 2. Emby 4.9: tests + publish, through the shared script so its behaviour and
#    this one cannot drift.
# ---------------------------------------------------------------------------
echo ""
echo "=== Emby 4.9 (shared build.sh: tests + publish) ==="
dotnet restore "$TESTS_DIR/"
( cd "$PLUGIN_DIR" && bash build.sh )

# ---------------------------------------------------------------------------
# 3. Emby 4.10: tests only. Nothing is published from this configuration any more: the one DLL
#    from step 2 is what ships for both versions, and a separately built 4.10 DLL would be an
#    artifact no release contains, so testing it on a rig would test the wrong file.
#    No --no-restore: Release_4_10 has its own PackageReferences (System.Text.Json 8.x rather
#    than 6.x) that the restore above did not fetch.
#    Expect exactly ONE more test here than in the 4.9 run: XtreamLiveStreamTests keeps a single
#    [Fact] behind `#if EMBY_4_10`, checking the members ILiveStream has only in that SDK. It was
#    four before upstream removed the conditional code from the plugin itself.
# ---------------------------------------------------------------------------
echo ""
echo "=== Emby 4.10 (tests) ==="
dotnet test "$TESTS_DIR/" -c Release_4_10 -v minimal

# ---------------------------------------------------------------------------
# 4. Load the shipped DLL against both SDKs, the same gate the release workflow uses. The DLL is
#    compiled against 4.9 only, so the compiler never sees a 4.10 interface; this is what catches
#    a 4.10 member it fails to implement. Exits non-zero on any failure.
# ---------------------------------------------------------------------------
echo ""
echo "=== Load check against Emby 4.9 and 4.10 SDKs ==="
for sdk in emby4_9 emby4_10; do
    dotnet run --project "$REPO_ROOT/scripts/sdk-load-check" -- "$PLUGIN_DIR/out/Emby.Xtream.Plugin.dll" "$REPO_ROOT/lib/$sdk"
done

# ---------------------------------------------------------------------------
# 5. Where everything went.
# ---------------------------------------------------------------------------
echo ""
echo "=== Build output (v$VERSION) ==="
ls -la "$PLUGIN_DIR/out/Emby.Xtream.Plugin.dll"

cat <<EOF

The same DLL for Emby 4.9 and 4.10:

  docker cp $PLUGIN_DIR/out/Emby.Xtream.Plugin.dll <container>:/config/plugins/

Then: docker restart <container>

The file is already named Emby.Xtream.Plugin.dll, which is what Emby needs: it names each
plugin's settings file after the DLL, so installing it under any other name gives it a separate,
empty configuration. Releases also publish a -4.10 copy for the update check on older installs;
installed by hand, that copy must be renamed.
EOF
