#!/usr/bin/env bash
# Computes the CI cache key for one native build output, so a change that touches no native input restores the
# output instead of rebuilding it. The key lists the repository inputs the native build reads; the regression test
# scripts/native-build-cache.tests.sh pins that list and checks the compiler's own dependency list against it.
#
# Usage: bash scripts/native-build-cache-key.sh <windows-release|windows-debug|linux-coverage|linux-release>
# Environment: CACHE_EPOCH (optional, manual invalidation), GITHUB_OUTPUT (when set, receives key=<value>).
#
# Fails open: any error prints a warning and writes an empty key, which skips the cache and builds natively.
# The key is exact; callers must not use restore-keys, because a prefix match would restore a stale binary.

# Native inputs outside MFTLibNative/ that the build reads: MSBuild imports Directory.Build.props and
# Directory.Build.targets for the vcxproj, MFTLibNative.Linux.props selects the Linux flavor, and the scripts drive
# every native build.
native_cache_key_files() {
    printf '%s\n' \
        Directory.Build.props \
        Directory.Build.targets \
        MFTLibNative.Linux.props \
        scripts/build-linux.sh \
        scripts/build-windows.ps1 \
        scripts/coverage-linux.sh \
        scripts/native-build-cache-key.sh \
        scripts/windows-tools.ps1
}

native_cache_key_flavors() {
    printf '%s\n' windows-release windows-debug linux-coverage linux-release
}

# Prints the toolchain identity for a flavor: MSVC toolsets and Windows SDKs through the checked discovery in
# windows-tools.ps1, or the g++ and cmake version banners on Linux.
native_cache_toolchain() {
    local flavor="$1" root="$2"
    case "$flavor" in
        windows-*)
            # Only Actions builds exclude the runner's unkeyed C++ user property sheet.
            [[ ${GITHUB_ACTIONS:-} == true ]] || return 1
            # MSVC reads additional command-line options from both variables.
            [[ -z ${CL:-} && -z ${_CL_:-} ]] || return 1
            local tools
            tools="$(cygpath -m "$root/scripts/windows-tools.ps1" 2> /dev/null || printf '%s' "$root/scripts/windows-tools.ps1")"
            NATIVE_CACHE_WINDOWS_TOOLS="$tools" pwsh -NoProfile -Command \
                ". \$env:NATIVE_CACHE_WINDOWS_TOOLS; Get-WindowsNativeCacheToolchain"
            ;;
        linux-*)
            local compiler builder override
            [[ ${CXX:-} == g++ ]] || return 1
            for override in CXXFLAGS LDFLAGS CPATH CPLUS_INCLUDE_PATH LIBRARY_PATH COMPILER_PATH GCC_EXEC_PREFIX \
                CMAKE_TOOLCHAIN_FILE CMAKE_CXX_COMPILER_LAUNCHER CMAKE_CXX_LINKER_LAUNCHER; do
                [[ -z ${!override:-} ]] || return 1
            done
            compiler="$("$CXX" --version)" && builder="$(cmake --version)" || return 1
            printf '%s; %s
' "${compiler%%$'
'*}" "${builder%%$'
'*}"
            ;;
        *) return 1 ;;
    esac
}

# The text the key hashes: one line per input. Fails when any input cannot be read.
native_cache_key_material() {
    local flavor="$1" toolchain="$2" root="$3" path hash
    printf 'flavor %s
' "$flavor"
    printf 'toolchain %s
' "$toolchain"
    hash="$(git -C "$root" rev-parse HEAD:MFTLibNative)" || return 1
    printf 'tree MFTLibNative %s
' "$hash"
    while IFS= read -r path; do
        hash="$(git -C "$root" rev-parse "HEAD:$path")" || return 1
        printf 'file %s %s
' "$path" "$hash"
    done < <(native_cache_key_files)
    # CMake and gcov record absolute build paths, so a Linux output only restores at the same workspace path.
    case "$flavor" in
        linux-*) printf 'workspace %s
' "$root" ;;
    esac
}

# native_cache_key <flavor> <toolchain identity> <epoch> <root>: pure over its arguments plus the git objects at HEAD.
native_cache_key() {
    local flavor="$1" toolchain="$2" epoch="$3" root="$4" material digest
    material="$(native_cache_key_material "$flavor" "$toolchain" "$root")" || return 1
    digest="$(printf '%s\nepoch %s\n' "$material" "$epoch" | sha256sum)" || return 1
    digest="${digest%% *}"
    [[ "$digest" =~ ^[0-9a-f]{64}$ ]] || return 1
    printf 'native-%s-%s\n' "$flavor" "${digest:0:32}"
}

native_cache_key_main() {
    local flavor="${1:-}" root toolchain key
    root="$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)"
    if ! native_cache_key_flavors | grep -qxF -- "$flavor"; then
        printf 'Unknown flavor: %s\n' "$flavor" >&2
        return 2
    fi
    if toolchain="$(native_cache_toolchain "$flavor" "$root")" &&
        key="$(native_cache_key "$flavor" "$toolchain" "${CACHE_EPOCH:-}" "$root")"; then
        :
    else
        printf 'Native cache key unavailable; building natively without the cache.\n' >&2
        key=''
    fi
    printf 'key=%s\n' "$key"
    if [[ -n "${GITHUB_OUTPUT:-}" ]]; then
        printf 'key=%s\n' "$key" >> "$GITHUB_OUTPUT"
    fi
}

if [[ "${BASH_SOURCE[0]}" == "$0" ]]; then
    native_cache_key_main "$@"
fi
