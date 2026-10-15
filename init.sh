#!/usr/bin/env bash
# init.sh - restore this checkout to a working state after `git clean -ffxd`.
#
# A deep clean removes everything not tracked, which is safe by design - but two
# things do not come back on their own: the NuGet restore, and the agent
# instruction files the aislop installer generates. This does both, and reports
# any prerequisite it cannot install for you.
#
# Usage:
#   git clean -ffxd && ./init.sh
#   git clean -ffxd && ./init.sh --build    # also build the native + managed output
#
# Safe to run at any time; every step is idempotent.
# Windows equivalent: init.ps1

set -eu
set -o pipefail

ROOT="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
BUILD=0

for arg in "$@"; do
    case "$arg" in
        --build) BUILD=1 ;;
        -h|--help) sed -n '2,14p' "${BASH_SOURCE[0]}"; exit 0 ;;
        *) echo "unknown flag: $arg" >&2; exit 2 ;;
    esac
done

missing=()
completed=()

have() { command -v "$1" >/dev/null 2>&1; }

echo "Checking prerequisites..."

if have dotnet; then
    printf '  %-20s %s\n' "dotnet SDK" "$(dotnet --version 2>/dev/null)"
else
    printf '  %-20s %s\n' "dotnet SDK" "MISSING"
    missing+=("dotnet SDK 10 - https://dotnet.microsoft.com/download")
fi

for tool in cmake ninja; do
    if have "$tool"; then
        printf '  %-20s %s\n' "$tool" "present"
    else
        printf '  %-20s %s\n' "$tool" "MISSING"
        missing+=("$tool - required to build the native library on Linux")
    fi
done

if have g++; then
    printf '  %-20s %s\n' "g++" "$(g++ -dumpversion 2>/dev/null)"
else
    printf '  %-20s %s\n' "g++" "MISSING"
    missing+=("g++ - required to build the native library on Linux")
fi

if have aislop; then
    printf '  %-20s %s\n' "aislop" "$(aislop --version 2>/dev/null)"
    aislop_available=1
else
    printf '  %-20s %s\n' "aislop" "missing"
    missing+=("aislop - see docs/quality-gates.md for the pinned install command")
    aislop_available=0
fi

if have gcovr; then
    printf '  %-20s %s\n' "gcovr" "present"
else
    printf '  %-20s %s\n' "gcovr" "missing (native coverage reports only)"
    missing+=("gcovr (native coverage reports only) - pip install gcovr")
fi

echo

if ! have dotnet; then
    echo "Cannot continue without the dotnet SDK." >&2
    exit 1
fi

# Restore the managed projects individually rather than MFTLib.sln: the solution
# includes MFTLibNative.vcxproj, which the dotnet CLI cannot load on Linux.
echo "Restoring NuGet packages..."
dotnet restore "$ROOT/MFTLib/MFTLib.csproj"
dotnet restore "$ROOT/MFTLibTestExtensions/MFTLibTestExtensions.csproj"
dotnet restore "$ROOT/MFTLib.Tests/MFTLib.Tests.csproj"
completed+=("NuGet packages restored")

if [ "$aislop_available" -eq 1 ]; then
    echo
    echo "Restoring agent instruction files..."
    aislop hook install claude --project
    completed+=(".claude/AISLOP.md and .claude/CLAUDE.md restored")
else
    echo
    echo "Skipping agent instruction files: aislop is not installed."
    echo "Once installed, run: aislop hook install claude --project"
fi

# Optional provisioning policy and diagnostics belong to scripts/optional-provisioning.
# It uses the already-required SDK; settings writes remain owned by the external tool.
# Only a successful helper result contributes a completed-step message.
if provisioning_message="$(dotnet run --project \
    "$ROOT/scripts/optional-provisioning/OptionalProvisioning.csproj" \
    -c Release -p:Platform=x64 --no-launch-profile --verbosity quiet)"; then
    if [ -n "$provisioning_message" ]; then completed+=("$provisioning_message"); fi
else
    provisioning_status=$?
    if [ "$provisioning_status" -ne 10 ] && [ "$provisioning_status" -ne 20 ]; then
        if [ -n "$provisioning_message" ]; then printf '%s\n' "$provisioning_message" >&2; fi
        printf 'Optional provisioning launcher exited with %s; continuing.\n' "$provisioning_status" >&2
    fi
fi

if [ "$BUILD" -eq 1 ]; then
    if ! have cmake || ! have ninja; then
        echo "Cannot build: cmake and ninja are required." >&2
        exit 1
    fi

    echo
    echo "Building native library..."
    "$ROOT/scripts/build-linux.sh"

    echo
    echo "Building managed projects..."
    dotnet build "$ROOT/MFTLib.Tests/MFTLib.Tests.csproj" -c Release -p:Platform=x64
    completed+=("Native and managed output built")
fi

echo
echo "Done."
for step in "${completed[@]}"; do
    echo "  $step"
done

if [ "${#missing[@]}" -gt 0 ]; then
    echo
    echo "Missing prerequisites (install manually):"
    for item in "${missing[@]}"; do
        echo "  - $item"
    done
fi

if [ "$BUILD" -eq 0 ]; then
    echo
    echo "Next: ./init.sh --build, or bash scripts/coverage-linux.sh for the full test run."
fi
