#!/usr/bin/env bash
# coverage-linux.sh — build with --coverage, run native + managed tests, produce reports.
#
# Outputs:
#   coverage-report/index.html             — native (gcovr) HTML
#   coverage-report/summary.txt            — native text summary
#   coverage-report/managed/coverage.cobertura.xml  — managed (coverlet) cobertura
#
# Flags:
#   --no-managed   skip the dotnet test pass (native-only run)
#
# Requires: cmake, ninja, g++ (with gcov), gcovr, dotnet 8 SDK.
set -euo pipefail

ROOT="$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)"
BUILD_DIR="$ROOT/build/linux-coverage"
REPORT_DIR="$ROOT/coverage-report"
MANAGED_REPORT_DIR="$REPORT_DIR/managed"

RUN_MANAGED=1
for arg in "$@"; do
    case "$arg" in
        --no-managed) RUN_MANAGED=0 ;;
        *) echo "unknown flag: $arg" >&2; exit 2 ;;
    esac
done

GCOVR="${GCOVR:-gcovr}"
if ! command -v "$GCOVR" >/dev/null 2>&1; then
    if [ -x "$HOME/.local/bin/gcovr" ]; then
        GCOVR="$HOME/.local/bin/gcovr"
    else
        echo "error: gcovr not found. Install with: pip install --user gcovr" >&2
        exit 1
    fi
fi

# --- Native build + smoke tests -----------------------------------------------

echo "==> [native] configuring (Debug + coverage)"
cmake -S "$ROOT/MFTLibNative" -B "$BUILD_DIR" -G Ninja \
    -DCMAKE_BUILD_TYPE=Debug \
    -DBUILD_TESTING=ON \
    -DMFTLIB_ENABLE_COVERAGE=ON \
    >/dev/null

echo "==> [native] building"
cmake --build "$BUILD_DIR"

echo "==> [native] clearing previous gcda data"
find "$BUILD_DIR" -name '*.gcda' -delete

echo "==> [native] running smoke tests"
LD_LIBRARY_PATH="$BUILD_DIR" "$BUILD_DIR/test/linux_smoke_test"

mkdir -p "$REPORT_DIR"

# --- Managed tests + coverlet -------------------------------------------------

if [ "$RUN_MANAGED" -eq 1 ]; then
    mkdir -p "$MANAGED_REPORT_DIR"

    # Tests excluded on Linux (call Windows-only entry points or use raw volume APIs):
    #   MftResultTests, MftVolumeTests, NativeCoverageTests, UsnJournalSyntheticTests
    #   (UsnJournalSyntheticTests P/Invokes the USN test hooks + usn_journal exports,
    #    all #ifdef _WIN32, so they don't exist in libMFTLibNative.so)
    # Plus 3 individual tests that need Windows-side platform behavior:
    #   ElevationUtilitiesTests.CanSelfElevate_DotnetExe_ReturnsFalse
    #   ElevationUtilitiesTests.TryRunElevated_ProcessExitsZero_ReturnsTrue
    #   MockVolumeTests.GetVolumeHandle_InvalidVolume_ThrowsIOException
    # Coverlet only writes output when the run is green, so failing tests must be filtered.
    FILTER='FullyQualifiedName!~MftResultTests'
    FILTER+='&FullyQualifiedName!~MftVolumeTests'
    FILTER+='&FullyQualifiedName!~NativeCoverageTests'
    FILTER+='&FullyQualifiedName!~UsnJournalSyntheticTests'
    FILTER+='&FullyQualifiedName!=MFTLib.Tests.ElevationUtilitiesTests.CanSelfElevate_DotnetExe_ReturnsFalse'
    FILTER+='&FullyQualifiedName!=MFTLib.Tests.ElevationUtilitiesTests.TryRunElevated_ProcessExitsZero_ReturnsTrue'
    FILTER+='&FullyQualifiedName!=MFTLib.Tests.MockVolumeTests.GetVolumeHandle_InvalidVolume_ThrowsIOException'

    echo
    echo "==> [managed] dotnet test with coverlet"
    dotnet test "$ROOT/MFTLib.Tests/MFTLib.Tests.csproj" \
        --filter "$FILTER" \
        --logger "console;verbosity=minimal" \
        /p:CollectCoverage=true \
        /p:CoverletOutput="$MANAGED_REPORT_DIR/coverage.cobertura.xml" \
        /p:CoverletOutputFormat=cobertura
fi

# --- Native coverage report ---------------------------------------------------

# gcovr's --gcov-ignore-parse-errors workaround for a known gcc bug with
# multithreaded coverage data: https://gcc.gnu.org/bugzilla/show_bug.cgi?id=68080
GCOVR_ARGS=(
    -r "$ROOT/MFTLibNative"
    --object-directory "$BUILD_DIR"
    --filter "MFTLibNative/"
    --exclude '.*/test/linux_smoke_test\.cpp'
    --gcov-ignore-parse-errors=negative_hits.warn_once_per_file
)

echo
echo "==> [native] coverage summary"
"$GCOVR" "${GCOVR_ARGS[@]}" --print-summary 2>/dev/null | tee "$REPORT_DIR/summary.txt"

echo
echo "==> [native] generating HTML report"
"$GCOVR" "${GCOVR_ARGS[@]}" \
    --html-details "$REPORT_DIR/index.html" \
    >/dev/null 2>&1

# --- Managed coverage summary (parse cobertura XML) ---------------------------

if [ "$RUN_MANAGED" -eq 1 ] && [ -f "$MANAGED_REPORT_DIR/coverage.cobertura.xml" ]; then
    echo
    echo "==> [managed] coverage summary"
    python3 - <<EOF
import xml.etree.ElementTree as ET
t = ET.parse("$MANAGED_REPORT_DIR/coverage.cobertura.xml").getroot()
lr = float(t.get("line-rate")) * 100
br = float(t.get("branch-rate")) * 100
print(f'lines:    {lr:.2f}% ({t.get("lines-covered")}/{t.get("lines-valid")})')
print(f'branches: {br:.2f}% ({t.get("branches-covered")}/{t.get("branches-valid")})')
EOF
fi

# --- Final summary ------------------------------------------------------------

echo
echo "==================================================================="
echo "Reports:"
echo "  Native HTML:       $REPORT_DIR/index.html"
echo "  Native summary:    $REPORT_DIR/summary.txt"
if [ "$RUN_MANAGED" -eq 1 ]; then
    echo "  Managed cobertura: $MANAGED_REPORT_DIR/coverage.cobertura.xml"
fi
echo "==================================================================="
