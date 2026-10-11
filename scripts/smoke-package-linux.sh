#!/usr/bin/env bash
# smoke-package-linux.sh <libMFTLibNative.so>  -  proves a Linux consumer can load an MFT dump from the package.
#
# Packs MFTLib on Linux with the given Release library (a test-only pack under version 0.0.0-package-smoke, never
# published), restores it from a local feed into scripts/package-smoke with an empty package cache, and runs that
# console project. The host must resolve the library from the package's runtimes folder. Pack and consumer
# intermediates and outputs land in a temporary directory, so a run from a Windows checkout under WSL leaves the
# checkout untouched.
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
# Redirect the restore and build intermediates out of the checkout. This cannot use BaseIntermediateOutputPath:
# that would move the SDK's default obj/** exclusion along with it, and a Windows-built checkout's existing
# MFTLib/obj generated sources would then compile a second time (duplicate attribute errors).
dotnet pack "$ROOT/MFTLib/MFTLib.csproj" -c Release -p:Platform=x64 "-p:Version=$VERSION" \
    "-p:MFTLibLinuxNativeLibrary=$LIBRARY" "-p:PackageOutputPath=$FEED" \
    "-p:MSBuildProjectExtensionsPath=$WORK/pack-obj/" "-p:IntermediateOutputPath=$WORK/pack-int/" \
    "-p:BaseOutputPath=$WORK/pack-bin/" "-p:NuspecOutputPath=$WORK/pack-nuspec/" --nologo

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
