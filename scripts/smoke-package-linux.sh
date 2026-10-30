#!/usr/bin/env bash
# smoke-package-linux.sh <libMFTLibNative.so>  -  proves a Linux consumer can load an MFT dump from the package.
#
# Packs MFTLib on Linux with the given Release library (a test-only pack under version 0.0.0-package-smoke, never
# published), restores it from a local feed into a temporary consumer with an empty package cache, and runs that
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

echo "==> restoring MFTLib $VERSION with $LIBRARY"
# Redirect the restore and build intermediates out of the checkout. This cannot use BaseIntermediateOutputPath:
# that would move the SDK's default obj/** exclusion along with it, and a Windows-built checkout's existing
# MFTLib/obj generated sources would then compile a second time (duplicate attribute errors).
dotnet restore "$ROOT/MFTLib/MFTLib.csproj" -p:Platform=x64 "-p:Version=$VERSION" \
    "-p:MSBuildProjectExtensionsPath=$WORK/pack-obj/" --locked-mode

echo "==> packing MFTLib $VERSION with $LIBRARY"
dotnet pack "$ROOT/MFTLib/MFTLib.csproj" -c Release -p:Platform=x64 "-p:Version=$VERSION" \
    "-p:MFTLibLinuxNativeLibrary=$LIBRARY" "-p:PackageOutputPath=$FEED" \
    "-p:MSBuildProjectExtensionsPath=$WORK/pack-obj/" "-p:IntermediateOutputPath=$WORK/pack-int/" \
    "-p:BaseOutputPath=$WORK/pack-bin/" "-p:NuspecOutputPath=$WORK/pack-nuspec/" --no-restore --nologo

# Entry names are stored uncompressed in a zip, so grep finds them without unzip.
grep -aq 'runtimes/linux-x64/native/libMFTLibNative.so' "$FEED/MFTLib.$VERSION.nupkg" || {
    echo "error: the test package lacks runtimes/linux-x64/native/libMFTLibNative.so" >&2
    exit 1
}

CONSUMER_PROJECT_DIRECTORY="$WORK/consumer/scripts/package-smoke"
CONSUMER_FIXTURE_DIRECTORY="$WORK/consumer/MFTLib.Tests/TestSupport"
mkdir -p "$CONSUMER_PROJECT_DIRECTORY" "$CONSUMER_FIXTURE_DIRECTORY"
cp "$ROOT/scripts/package-smoke/PackageSmoke.csproj" \
    "$ROOT/scripts/package-smoke/Program.cs" \
    "$ROOT/scripts/package-smoke/Directory.Build.props" \
    "$ROOT/scripts/package-smoke/packages.lock.json" \
    "$CONSUMER_PROJECT_DIRECTORY/"
cp "$ROOT/MFTLib.Tests/TestSupport/MftDumpFixture.cs" "$CONSUMER_FIXTURE_DIRECTORY/"

python3 - "$FEED/MFTLib.$VERSION.nupkg" "$CONSUMER_PROJECT_DIRECTORY/packages.lock.json" <<'PY'
import base64
import hashlib
import json
from pathlib import Path
import sys

package_path = Path(sys.argv[1])
lock_path = Path(sys.argv[2])
content_hash = base64.b64encode(hashlib.sha512(package_path.read_bytes()).digest()).decode("ascii")
lock_data = json.loads(lock_path.read_text(encoding="utf-8"))
lock_data["dependencies"]["net10.0"]["MFTLib"]["contentHash"] = content_hash
lock_path.write_text(json.dumps(lock_data, indent=2) + "\n", encoding="utf-8")
PY

echo "==> restoring and running the consumer project from the local feed"
export NUGET_PACKAGES="$WORK/packages"
dotnet restore "$CONSUMER_PROJECT_DIRECTORY/PackageSmoke.csproj" \
    "-p:MFTLibPackageVersion=$VERSION" "-p:RestoreSources=$FEED" \
    "-p:BaseIntermediateOutputPath=$WORK/obj/" "-p:BaseOutputPath=$WORK/bin/" --locked-mode
dotnet run --project "$CONSUMER_PROJECT_DIRECTORY/PackageSmoke.csproj" -c Release \
    "-p:MFTLibPackageVersion=$VERSION" "-p:RestoreSources=$FEED" \
    "-p:BaseIntermediateOutputPath=$WORK/obj/" "-p:BaseOutputPath=$WORK/bin/" --no-restore --nologo
