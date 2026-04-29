#!/usr/bin/env bash
set -euo pipefail

ROOT="$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)"
BUILD_DIR="$ROOT/build/linux"

mkdir -p "$BUILD_DIR"
cmake -S "$ROOT/MFTLibNative" -B "$BUILD_DIR" -G Ninja -DBUILD_TESTING=ON -DCMAKE_BUILD_TYPE=Release "$@"
cmake --build "$BUILD_DIR"

echo
echo "Running smoke test..."
LD_LIBRARY_PATH="$BUILD_DIR" "$BUILD_DIR/test/linux_smoke_test"
