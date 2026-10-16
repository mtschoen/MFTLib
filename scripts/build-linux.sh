#!/usr/bin/env bash
set -eu
set -o pipefail

ROOT="$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)"
BUILD_DIR="$ROOT/build/linux"

mkdir -p "$BUILD_DIR"
cmake -S "$ROOT/MFTLibNative" -B "$BUILD_DIR" -G Ninja -DCMAKE_BUILD_TYPE=Release "$@"
cmake --build "$BUILD_DIR"

SMOKE_WORK="$(mktemp -d)"
trap 'rm -rf "$SMOKE_WORK"' EXIT

echo
echo "Running the managed Smoke tier against build/linux (MFTLibNativeLinuxFlavor=Release)..."
dotnet test "$ROOT/MFTLib.Tests/MFTLib.Tests.csproj" -c Release -p:MFTLibNativeLinuxFlavor=Release "-p:MFTLibSmokeArtifactsDirectory=$SMOKE_WORK" --filter TestCategory=Smoke
