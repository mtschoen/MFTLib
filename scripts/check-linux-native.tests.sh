#!/usr/bin/env bash
set -euo pipefail
root="$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)"
temporary="$(mktemp -d)"
trap 'rm -rf "$temporary"' EXIT
library="$temporary/library with spaces.so"
: > "$library"

readelf() {
    case "$1" in
        -h)
            printf '  Class: %s\n  Type: DYN (Shared object file)\n  Machine: Advanced Micro Devices X86-64\n' \
                "${fixture_class:-ELF64}" ;;
        -d) printf '%s\n' "${fixture_dynamic:-no dynamic paths}" ;;
        *) return 97 ;;
    esac
}
nm() {
    printf '00000000 T %s\n' OpenMftDumpInput ParseMftDumpInput CloseMftDumpInput \
        ParseMFTRecordsWithProgress FreeMftResult
    if [[ "${fixture_missing_export:-false}" != true ]]; then
        printf '00000000 T GetMftNativeAbiVersion\n'
    fi
    if [[ "${fixture_coverage:-false}" == true ]]; then printf ' U __gcov_init\n'; fi
}
objdump() {
    printf '%s\n' "${fixture_versions:-GLIBC_2.9 GLIBC_2.33 GLIBC_2.9 GLIBCXX_3.4.9 GLIBCXX_3.4.22}"
}
export -f readelf nm objdump
export fixture_class fixture_coverage fixture_dynamic fixture_missing_export fixture_versions

check_case() {
    local expected_status="$1" expected_text="$2" output status=0
    shift 2
    output="$(bash "$root/scripts/check-linux-native.sh" "$@" 2>&1)" || status=$?
    if [[ "$status" != "$expected_status" || "$output" != *"$expected_text"* ]]; then
        printf 'Expected status %s and %s; got %s: %s\n' \
            "$expected_status" "$expected_text" "$status" "$output" >&2
        exit 1
    fi
}

check_case 0 $'glibc: 2.33\nglibcxx: 3.4.22' "$library"
check_case 2 'usage:'
check_case 1 'library not found' "$temporary/absent.so"
fixture_class=ELF32 check_case 1 'not an x86-64 ELF shared object' "$library"
fixture_coverage=true check_case 1 'coverage instrumented' "$library"
fixture_dynamic=' (RPATH) /fixture' check_case 1 'RPATH or RUNPATH' "$library"
fixture_dynamic=' (RUNPATH) /fixture' check_case 1 'RPATH or RUNPATH' "$library"
fixture_missing_export=true check_case 1 'does not export GetMftNativeAbiVersion' "$library"
# With pipefail, a missing family causes the current version pipeline to exit 1.
fixture_versions='GLIBC_2.33' check_case 1 '' "$library"
fixture_versions='GLIBCXX_3.4.22' check_case 1 '' "$library"
printf 'Linux native acceptance regressions passed.\n'
