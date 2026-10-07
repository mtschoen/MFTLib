#!/usr/bin/env bash
# smoke-package-linux.sh <libMFTLibNative.so>  -  proves a Linux consumer can load an MFT dump from the package.
#
# Packs MFTLib on Linux with the given Release library (a test-only pack under version 0.0.0-package-smoke, never
# published), restores it from a local feed into scripts/package-smoke with an empty package cache, and runs that
# console project. The host must resolve the library from the package's runtimes folder.
# A run from a Windows checkout under WSL rewrites MFTLib/obj with a Linux restore; restore on Windows again afterwards.
set -eu
set -o pipefail

if [ "$#" -ne 1 ]; then
    echo "usage: smoke-package-linux.sh <libMFTLibNative.so>" >&2
    exit 2
fi

ROOT="$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)"
LIBRARY="$(cd "$(dirname "$1")" && pwd)/$(basename "$1")"
VERSION="0.0.0-package-smoke"
WORK="$(mktemp -d)"
trap 'rm -rf "$WORK"' EXIT

FEED="$WORK/feed"
mkdir -p "$FEED"

echo "==> packing MFTLib $VERSION with $LIBRARY"
dotnet pack "$ROOT/MFTLib/MFTLib.csproj" -c Release -p:Platform=x64 "-p:Version=$VERSION" \
    "-p:MFTLibLinuxNativeLibrary=$LIBRARY" "-p:PackageOutputPath=$FEED" --nologo

# Entry names are stored uncompressed in a zip, so grep finds them without unzip.
grep -aq 'runtimes/linux-x64/native/libMFTLibNative.so' "$FEED/MFTLib.$VERSION.nupkg" || {
    echo "error: the test package lacks runtimes/linux-x64/native/libMFTLibNative.so" >&2
    exit 1
}

echo "==> restoring and running the consumer project from the local feed"
export NUGET_PACKAGES="$WORK/packages"
dotnet run --project "$ROOT/scripts/package-smoke/PackageSmoke.csproj" -c Release \
    "-p:MFTLibPackageVersion=$VERSION" "-p:RestoreSources=$FEED;https://api.nuget.org/v3/index.json" \
    "-p:BaseIntermediateOutputPath=$WORK/obj/" "-p:BaseOutputPath=$WORK/bin/" --nologo
