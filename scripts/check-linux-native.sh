#!/usr/bin/env bash
# check-linux-native.sh <libMFTLibNative.so>  -  acceptance checks for the library the package ships.
#
# Fails unless the library is a 64-bit x86-64 ELF shared object built without coverage
# instrumentation, carries no RPATH or RUNPATH, and exports the native entry points the managed
# dump source calls. Prints the glibc and libstdc++ symbol versions it requires; those measured
# versions are the floor the package README states.
set -eu
set -o pipefail

if [ "$#" -ne 1 ]; then
    echo "usage: check-linux-native.sh <libMFTLibNative.so>" >&2
    exit 2
fi

LIBRARY="$1"
if [ ! -f "$LIBRARY" ]; then
    echo "error: library not found: $LIBRARY" >&2
    exit 1
fi

HEADER="$(readelf -h "$LIBRARY")"
DESCRIPTION="$(grep -E '^ *(Class|Type|Machine):' <<<"$HEADER" | sed -E 's/^ *//; s/ +/ /g' | paste -sd ';' -)"
if ! grep -Eq '^ *Class: +ELF64$' <<<"$HEADER" || ! grep -Eq '^ *Type: +DYN' <<<"$HEADER" ||
   ! grep -Eq '^ *Machine: +Advanced Micro Devices X86-64$' <<<"$HEADER"; then
    echo "error: not an x86-64 ELF shared object: $DESCRIPTION" >&2
    exit 1
fi

SYMBOLS="$(nm -D "$LIBRARY")"
if grep -q '__gcov' <<<"$SYMBOLS"; then
    echo "error: the library is coverage instrumented (nm -D shows __gcov symbols)." >&2
    exit 1
fi

if readelf -d "$LIBRARY" | grep -Eq '\((RPATH|RUNPATH)\)'; then
    echo "error: the library carries an RPATH or RUNPATH." >&2
    readelf -d "$LIBRARY" | grep -E '\((RPATH|RUNPATH)\)' >&2
    exit 1
fi

for entry in OpenMftDumpInput ParseMftDumpInput CloseMftDumpInput; do
    if ! grep -Eq " T $entry\$" <<<"$SYMBOLS"; then
        echo "error: the library does not export $entry." >&2
        exit 1
    fi
done

# The highest version of each versioned symbol family the library imports.
highest_version() {
    local prefix="$1"
    objdump -T "$LIBRARY" | grep -Eo "${prefix}_[0-9][0-9.]*" | sed "s/^${prefix}_//" | sort -uV | tail -n 1
}

GLIBC="$(highest_version GLIBC)"
GLIBCXX="$(highest_version GLIBCXX)"
if [ -z "$GLIBC" ] || [ -z "$GLIBCXX" ]; then
    echo "error: could not measure the GLIBC and GLIBCXX symbol versions." >&2
    exit 1
fi

echo "library: $LIBRARY"
echo "file: $DESCRIPTION"
echo "glibc: $GLIBC"
echo "glibcxx: $GLIBCXX"
echo "check-linux-native: passed"
