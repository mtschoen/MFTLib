#!/usr/bin/env bash
set -eu
set -o pipefail

ROOT="$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)"
BUILD_DIR="$ROOT/build/linux"

# --native-output-supplied: CI restored build/linux/libMFTLibNative.so from the native build cache. Skip only the
# native build, and only when the library is present and non-empty; otherwise build as usual.
build_linux_native() {
    local supplied="$1"
    shift
    if [ "$supplied" = true ] && [ -s "$BUILD_DIR/libMFTLibNative.so" ]; then
        echo "Using the supplied native output in $BUILD_DIR; skipping the native build."
    else
        if [ "$supplied" = true ]; then
            echo "Supplied native output is missing or empty; building natively."
        fi
        mkdir -p "$BUILD_DIR"
        cmake -S "$ROOT/MFTLibNative" -B "$BUILD_DIR" -G Ninja -DCMAKE_BUILD_TYPE=Release "$@"
        cmake --build "$BUILD_DIR"
    fi
}

build_linux_main() {
    local native_output_supplied=false
    if [ "${1:-}" = "--native-output-supplied" ]; then
        native_output_supplied=true
        shift
    fi
    build_linux_native "$native_output_supplied" "$@"

    smoke_work="$(mktemp -d)"
    trap 'rm -rf "$smoke_work"' EXIT

    echo
    echo "Restoring the managed Smoke tier against build/linux (MFTLibNativeLinuxFlavor=Release)..."
    dotnet restore "$ROOT/MFTLib.Tests/MFTLib.Tests.csproj" -p:MFTLibNativeLinuxFlavor=Release "-p:MFTLibSmokeArtifactsDirectory=$smoke_work" --locked-mode

    echo
    echo "Running the managed Smoke tier against build/linux (MFTLibNativeLinuxFlavor=Release)..."
    dotnet test "$ROOT/MFTLib.Tests/MFTLib.Tests.csproj" -c Release -p:MFTLibNativeLinuxFlavor=Release "-p:MFTLibSmokeArtifactsDirectory=$smoke_work" --no-restore --filter TestCategory=Smoke
}

if [[ "${BASH_SOURCE[0]}" == "$0" ]]; then
    build_linux_main "$@"
fi
