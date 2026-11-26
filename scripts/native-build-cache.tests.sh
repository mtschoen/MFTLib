#!/usr/bin/env bash
# Regression test for the CI native build cache: pins the key input list, proves each input and flavor changes the
# key, checks the compiler's own dependency list for a native input outside the key, and exercises the
# "native output supplied" input of the Linux build scripts against stubbed tools.
set -euo pipefail

root="$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)"
# shellcheck source=native-build-cache-key.sh
source "$root/scripts/native-build-cache-key.sh"

scratch="$(mktemp -d)"
trap 'rm -rf "$scratch"' EXIT

fail() { printf 'FAIL: %s\n' "$1" >&2; exit 1; }
assert_equal() { [[ "$1" == "$2" ]] || fail "$3: expected [$1], got [$2]"; }
assert_differs() { [[ "$1" != "$2" ]] || fail "$3: values should differ ($1)"; }

# 1. The key input list is pinned. A new native input outside this list must be added here and in the key script.
expected_files='Directory.Build.props
Directory.Build.targets
MFTLibNative.Linux.props
scripts/build-linux.sh
scripts/build-windows.ps1
scripts/coverage-linux.sh
scripts/native-build-cache-key.sh
scripts/windows-tools.ps1'
assert_equal "$expected_files" "$(native_cache_key_files)" 'key input files'
assert_equal 'windows-release
windows-debug
linux-coverage
linux-release' "$(native_cache_key_flavors)" 'key flavors'
while IFS= read -r path; do
    git -C "$root" ls-files --error-unmatch -- "$path" > /dev/null || fail "key input $path is not tracked"
done < <(native_cache_key_files)

# 2. Every workflow use of the key script names a known flavor.
while IFS= read -r flavor; do
    native_cache_key_flavors | grep -qxF -- "$flavor" || fail "workflow names unknown flavor $flavor"
done < <(tr -d '\r' < "$root/.gitea/workflows/test.yml" | sed -n 's/.*native-build-cache-key\.sh \([a-z-]*\)$/\1/p')

# 3. On Linux, the preprocessor's dependency list shows no source reads a file outside MFTLibNative/. Headers that do
#    not exist on this host (windows.h) are ignored; this does not inspect Windows-only includes.
check_dependency_rule() {
    local native_dir="$1" rule="$2" dependency resolved
    local -a dependencies
    rule=${rule//$'\\\n'/ }
    # Accept plain Make words only. An escaped name must not become two apparently missing headers.
    if [[ $rule != 'native: '* || $rule == *\\* || $rule == *'$'* || $rule == *$'\n'* ]]; then
        printf 'Unsupported dependency rule: %s\n' "$rule" >&2
        return 1
    fi
    read -ra dependencies <<< "${rule#native:}"
    for dependency in "${dependencies[@]}"; do
        [[ -f "$dependency" ]] || continue
        resolved="$(realpath -- "$dependency")" || return 1
        [[ "$resolved" == "$native_dir/"* ]] || { printf 'outside the key: %s\n' "$resolved" >&2; return 1; }
    done
}

check_native_dependencies() (
    local source rule
    cd "$1" || return 1
    git ls-files -z -- 'MFTLibNative/*.cpp' > "$scratch/sources" || return 1
    while IFS= read -r -d '' source; do
        rule=$(g++ -std=c++17 -DAISLOP_TU_FRAGMENT -DAISLOP_TU_FRAGMENT_SYNTHETIC -MM -MG \
            -MT native -I MFTLibNative "$source") || return 1
        check_dependency_rule "$PWD/MFTLibNative" "$rule" || return 1
    done < "$scratch/sources"
)

# Use each translation unit's configured command, including target and per-source options.
check_configured_dependencies() (
    local native_dir="$1/MFTLibNative" build_dir="$2" directory command rule
    shift 2
    CXX=g++ cmake -S "$native_dir" -B "$build_dir" -G Ninja -DCMAKE_EXPORT_COMPILE_COMMANDS=ON "$@" >/dev/null || return 1
    jq -ej '.[] | .directory, "\u0000", .command, "\u0000"' "$build_dir/compile_commands.json" > "$build_dir/commands" || return 1
    while IFS= read -r -d '' directory && IFS= read -r -d '' command; do
        cd "$directory" || return 1
        rule=$(sh -c "$command -MM -MT native -MF -") || return 1
        check_dependency_rule "$native_dir" "$rule" || return 1
    done < "$build_dir/commands"
)
for description in MFTLibNative/MFTLibNative.vcxproj MFTLibNative/CMakeLists.txt; do
    if grep -qF -e '../' -e "..\\" "$root/$description"; then fail "$description reaches outside MFTLibNative/"; fi
done
if command -v g++ > /dev/null; then
    check_native_dependencies "$root" || fail 'Linux native dependency check failed'
    if [[ $OSTYPE == linux* ]]; then
        check_configured_dependencies "$root" "$scratch/release-deps" -DCMAKE_BUILD_TYPE=Release
        check_configured_dependencies "$root" "$scratch/coverage-deps" -DCMAKE_BUILD_TYPE=Debug -DMFTLIB_ENABLE_COVERAGE=ON
    fi
    if (g++() { return 11; }; check_native_dependencies "$root") > /dev/null 2>&1; then
        fail 'failed Linux preprocessing must fail the dependency check'
    fi
else
    printf 'SKIP compiler dependency list: g++ is not installed on this host.\n'
fi

# 4. One real HEAD supplies the native tree and all external blobs.
fixture="$scratch/fixture"
mkdir -p "$fixture/MFTLibNative" "$fixture/scripts"
while IFS= read -r path; do printf '%s\n' "$path" > "$fixture/$path"; done < <(native_cache_key_files)
printf 'int main() {}\n' > "$fixture/MFTLibNative/source.cpp"
git -C "$fixture" init -q
git -C "$fixture" add -A
git -C "$fixture" -c user.name=test -c user.email=test@example.invalid commit -q -m fixture
if command -v g++ > /dev/null; then
    touch "$fixture/review check.h"
    printf '#include "../review check.h"\n' > "$fixture/MFTLibNative/source.cpp"
    if check_native_dependencies "$fixture"; then fail 'out-of-key header with a space was accepted'; fi
    git -C "$fixture" restore MFTLibNative/source.cpp
    rm "$fixture/review check.h"
    if [[ $OSTYPE == linux* ]]; then
        printf '%s\n' 'cmake_minimum_required(VERSION 3.20)' 'project(probe CXX)' 'add_library(probe source.cpp)' > "$fixture/MFTLibNative/CMakeLists.txt"
        touch "$fixture/release-only.h"
        printf '%s\n' '#ifdef NDEBUG' '#include "../release-only.h"' '#endif' > "$fixture/MFTLibNative/source.cpp"
        if check_configured_dependencies "$fixture" "$scratch/probe" -DCMAKE_BUILD_TYPE=Release 2> "$scratch/probe-error"; then
            fail 'out-of-key Release-only header was accepted'
        fi
        grep -Fx "outside the key: $fixture/release-only.h" "$scratch/probe-error" || fail 'Release probe failed for the wrong reason'
    fi
fi
baseline="$(native_cache_key linux-coverage toolchain 1 "$fixture")"
[[ $baseline =~ ^native-linux-coverage-[0-9a-f]{32}$ ]] || fail 'real HEAD did not produce a key'
(
    git() {
        [[ $# == 4 && $1 == -C && $3 == rev-parse && $4 == HEAD:* ]] || return 1
        if [[ ${4#HEAD:} == "${changed_input:-}" ]]; then printf 'changed\n'; else printf 'original\n'; fi
    }
    baseline="$(native_cache_key linux-coverage toolchain 1 "$fixture")"
    assert_equal "$baseline" "$(native_cache_key linux-coverage toolchain 1 "$fixture")" 'key is deterministic'
    comma_key="$(native_cache_key linux-coverage toolchain review,epoch "$fixture")"
    [[ $comma_key =~ ^native-linux-coverage-[0-9a-f]{32}$ ]] || fail "unsafe epoch in key: $comma_key"
    assert_differs "$baseline" "$(native_cache_key linux-release toolchain 1 "$fixture")" 'flavor'
    assert_differs "$baseline" "$(native_cache_key linux-coverage other-toolchain 1 "$fixture")" 'toolchain'
    assert_differs "$baseline" "$(native_cache_key linux-coverage toolchain 2 "$fixture")" 'epoch'
    for path in MFTLibNative $expected_files; do
        changed="$(changed_input="$path" native_cache_key linux-coverage toolchain 1 "$fixture")"
        assert_differs "$baseline" "$changed" "input $path"
    done
    assert_differs "$(native_cache_key linux-release toolchain 1 "$scratch/elsewhere")" \
        "$(native_cache_key linux-release toolchain 1 "$scratch/other")" 'Linux workspace path'
    assert_equal "$(native_cache_key windows-release toolchain 1 "$scratch/elsewhere")" \
        "$(native_cache_key windows-release toolchain 1 "$scratch/other")" 'Windows key ignores the workspace path'
)
# An unavailable key fails open: an empty key and exit status zero, so the workflow skips the cache and builds.
mkdir -p "$scratch/not-a-repository/scripts"
cp "$root/scripts/native-build-cache-key.sh" "$scratch/not-a-repository/scripts/"
CXX=g++ GIT_CEILING_DIRECTORIES="$scratch" GITHUB_OUTPUT="$scratch/output" bash "$scratch/not-a-repository/scripts/native-build-cache-key.sh" linux-release > /dev/null 2>&1
assert_equal 'key=' "$(cat "$scratch/output")" 'unavailable key is empty'
sha256sum() { return 17; }
if native_cache_key linux-release toolchain 1 "$fixture" > /dev/null; then
    fail 'failed hash producer must not publish a cache key'
fi
unset -f sha256sum
for override in 'CXX=clang++' 'CXXFLAGS=-DNDEBUG' 'LDFLAGS=-s' 'CMAKE_TOOLCHAIN_FILE=custom.cmake'; do
    (
        export CXX=g++ "${override?}"
        assert_equal 'key=' "$(native_cache_key_main linux-coverage)" "override $override bypasses cache"
    )
done
(
    unset CL _CL_
    pwsh() { printf '%s\n' 'fixture toolchain'; }
    windows_key() { GITHUB_ACTIONS=true GITHUB_OUTPUT='' native_cache_key_main windows-release; }
    [[ $(windows_key) =~ ^key=native-windows-release-[0-9a-f]{32}$ ]] || fail 'expected a Windows key without overrides'
    assert_equal 'key=' "$(CL=/Od windows_key)" 'CL bypasses cache'
    assert_equal 'key=' "$(_CL_=/Od windows_key)" '_CL_ bypasses cache'
)
unknown_status=0
bash "$root/scripts/native-build-cache-key.sh" unknown-flavor > /dev/null 2>&1 || unknown_status=$?
assert_equal 2 "$unknown_status" 'unknown flavor is rejected'

# 5. The "native output supplied" input of the Linux scripts, against stubbed tools.
stubs="$scratch/stubs"
mkdir -p "$stubs"
for tool in cmake dotnet gcovr; do
    cat > "$stubs/$tool" << 'STUB'
#!/usr/bin/env bash
printf '%s %s\n' "$(basename "$0")" "$*" >> "$STUB_LOG"
previous=''
for argument in "$@"; do
    if [[ "$1" == test && "$previous" == --filter ]]; then printf 'test filter: %s\n' "$argument" >> "$STUB_LOG"; fi
    if [[ "$previous" == --results-directory ]]; then mkdir -p "$argument"; : > "$argument/managed.trx"; fi
    previous="$argument"
done
exit 0
STUB
    chmod +x "$stubs/$tool"
done

run_script() {
    local fixture="$1" script="$2"
    shift 2
    mkdir -p "$fixture/scripts"
    cp "$root/scripts/$script" "$fixture/scripts/"
    : > "$STUB_LOG"
    PATH="$stubs:$PATH" GCOVR="$stubs/gcovr" bash "$fixture/scripts/$script" "$@" > "$scratch/script-output" 2>&1 \
        || { cat "$scratch/script-output" >&2; fail "$script $* failed"; }
}
export STUB_LOG="$scratch/stub.log"
cmake_calls() { grep -c '^cmake ' "$STUB_LOG" || true; }

# shellcheck source=build-linux.sh
source "$root/scripts/build-linux.sh"
# shellcheck source=coverage-linux.sh
source "$root/scripts/coverage-linux.sh"

ROOT="$scratch/release"
BUILD_DIR="$ROOT/build/linux"
cmake() { calls=$((calls + 1)); }
for case_name in no-flag supplied-valid supplied-missing supplied-empty; do
    calls=0
    rm -rf "$BUILD_DIR"
    mkdir -p "$BUILD_DIR"
    case "$case_name" in
        supplied-valid) printf 'library\n' > "$BUILD_DIR/libMFTLibNative.so" ;;
        supplied-empty) : > "$BUILD_DIR/libMFTLibNative.so" ;;
    esac
    if [[ "$case_name" == no-flag ]]; then build_linux_native false; else build_linux_native true; fi
    if [[ "$case_name" == supplied-valid ]]; then
        assert_equal 0 "$calls" 'release: valid supplied output skips cmake'
    else
        assert_equal 2 "$calls" "release: $case_name configures and builds"
    fi
done
printf 'library\n' > "$BUILD_DIR/libMFTLibNative.so"
run_script "$ROOT" build-linux.sh --native-output-supplied
assert_equal 0 "$(cmake_calls)" 'release wrapper: valid supplied output skips cmake'
assert_equal 1 "$(grep -c '^dotnet test ' "$STUB_LOG")" 'release wrapper runs managed tests once'
assert_equal 'test filter: TestCategory=Smoke' "$(grep '^test filter:' "$STUB_LOG")" 'release wrapper selects Smoke'

ROOT="$scratch/coverage"
BUILD_DIR="$ROOT/build/linux-coverage"
ninja() { printf '%s\n' first.cpp.o second.cpp.o; }
prepare_coverage_outputs() {
    local source
    mkdir -p "$BUILD_DIR"
    printf 'library\n' > "$BUILD_DIR/libMFTLibNative.so"
    printf 'cache\n' > "$BUILD_DIR/CMakeCache.txt"
    for source in first second; do
        # An existing object is up to date even when its coverage note was lost.
        if [[ ! -f "$BUILD_DIR/$source.cpp.o" ]]; then
            touch "$BUILD_DIR/$source.cpp.o"
            printf 'notes\n' > "$BUILD_DIR/$source.cpp.gcno"
        fi
    done
}
cmake() {
    calls=$((calls + 1))
    if [[ "$1" == --build ]]; then prepare_coverage_outputs; fi
}
for case_name in no-flag failed-restore supplied-valid supplied-partial-notes supplied-no-notes supplied-no-library supplied-no-cache; do
    calls=0
    rm -rf "$BUILD_DIR"
    prepare_coverage_outputs
    touch "$BUILD_DIR/first.cpp.gcda"
    case "$case_name" in
        supplied-partial-notes) rm "$BUILD_DIR/second.cpp.gcno" ;;
        supplied-no-notes|failed-restore) rm "$BUILD_DIR"/*.gcno ;;
        supplied-no-library) rm "$BUILD_DIR/libMFTLibNative.so" ;;
        supplied-no-cache) rm "$BUILD_DIR/CMakeCache.txt" ;;
    esac
    case "$case_name" in
        no-flag)
            touch "$BUILD_DIR/local-marker"
            GITHUB_ACTIONS=false coverage_linux_native false
            [[ -e "$BUILD_DIR/local-marker" ]] || fail 'local build should be incremental' ;;
        failed-restore) GITHUB_ACTIONS=true coverage_linux_native false ;;
        *) coverage_linux_native true ;;
    esac
    if [[ "$case_name" == supplied-valid ]]; then
        assert_equal 0 "$calls" 'coverage: valid supplied build directory skips cmake'
    else
        assert_equal 2 "$calls" "coverage: $case_name configures and builds"
    fi
    [[ -s "$BUILD_DIR/first.cpp.gcno" && -s "$BUILD_DIR/second.cpp.gcno" ]] || fail "coverage: $case_name left missing notes"
    [[ ! -e "$BUILD_DIR/first.cpp.gcda" ]] || fail "coverage: $case_name clears stale gcda data"
done
ROOT="$scratch/coverage-wrapper"
BUILD_DIR="$ROOT/build/linux-coverage"
prepare_coverage_outputs
printf '#!/usr/bin/env bash\nprintf "%%s\\n" first.cpp.o second.cpp.o\n' > "$stubs/ninja"
chmod +x "$stubs/ninja"
git -C "$ROOT" init -q
git -C "$ROOT" -c user.name=test -c user.email=test@example.invalid commit -q --allow-empty -m fixture
run_script "$ROOT" coverage-linux.sh --native-output-supplied
assert_equal 0 "$(cmake_calls)" 'coverage wrapper: valid supplied build directory skips cmake'
assert_equal 1 "$(grep -c '^dotnet test ' "$STUB_LOG")" 'coverage wrapper still runs the managed tests'
assert_equal "$(git -C "$ROOT" rev-parse HEAD)" "$(cat "$ROOT/coverage-report/commit.txt")" 'coverage wrapper stamps the fixture HEAD'
status=0
PATH="$stubs:$PATH" GCOVR="$stubs/gcovr" bash "$ROOT/scripts/coverage-linux.sh" --bogus > /dev/null 2>&1 || status=$?
assert_equal 2 "$status" 'coverage rejects unknown flags'

printf 'Native build cache: all regression checks passed.\n'
