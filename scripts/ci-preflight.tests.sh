#!/usr/bin/env bash
# Offline dispatcher contracts with gate and executable boundary doubles.
set -uo pipefail
root="$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)"
source "$root/tools/ci-preflight.sh" || exit 1

failures=0
checks=0
assert_contains() {
    checks=$((checks + 1))
    if [[ "$1" != *"$2"* ]]; then
        printf 'FAIL: missing %s in %s\n' "$2" "$1" >&2
        failures=$((failures + 1))
    fi
}
assert_equal() {
    checks=$((checks + 1))
    if [[ "$1" != "$2" ]]; then
        printf 'FAIL: expected %s, got %s\n' "$1" "$2" >&2
        failures=$((failures + 1))
    fi
}
preflight_platform() { printf 'windows'; }
preflight_execute() {
    printf 'EXECUTED %s\n' "$1"
    [[ "$1" != "${failing_gate:-}" ]]
}
preflight_changed_paths() { printf '%s\n' "$changed_paths"; return "$inspection_status"; }
run_case() {
    output="$(preflight_main "$@" 2>&1)"
    status=$?
}

for arguments in '' '--unknown' '--only' '--changed-from' '--only missing' '--all --only docs' '--all --all'; do
    # Intentional splitting models these literal CLI argument vectors.
    read -r -a vector <<< "$arguments"
    run_case "${vector[@]}"
    assert_equal 2 "$status"
done

failing_gate=build-script
run_case --all
assert_equal 1 "$status"
assert_contains "$output" 'FAIL build-script'
assert_contains "$output" 'PASS windows'
assert_contains "$output" 'PASS aislop'
assert_contains "$output" 'SKIP linux: requires linux (host windows)'
assert_contains "$output" 'PREFLIGHT FAIL'
failing_gate=''

run_case --only docs --only coverage-status
assert_equal 0 "$status"
assert_contains "$output" 'Preflight; host windows; selection only'
assert_contains "$output" 'PASS docs'
assert_contains "$output" 'PASS coverage-status'
assert_contains "$output" 'PREFLIGHT PASS (selected host gates only)'
if [[ "$output" == *'EXECUTED windows'* ]]; then failures=$((failures + 1)); fi

for changed_paths in 'docs/quality-gates.md' '.aislop/fork-commit' 'AGENTS.md' $'docs/page.md\nREADME.md'; do
    inspection_status=0
    run_case --changed-from main
    assert_equal 0 "$status"
    assert_contains "$output" 'SKIP windows: workflow docs-only/pin-only filter'
    assert_contains "$output" 'PASS docs'
    assert_contains "$output" 'PASS aislop'
done
for changed_paths in '' 'nested/README.md' 'MFTLib/Code.cs' $'docs/page.md\nnew.cs'; do
    run_case --changed-from main
    assert_contains "$output" 'PASS windows'
done
inspection_status=1
changed_paths='docs/page.md'
run_case --changed-from missing-reference
assert_contains "$output" 'changed-file inspection failed; running all gates'
assert_contains "$output" 'PASS windows'

preflight_platform() { printf 'linux'; }
failing_gate=linux-package-build
run_case --all
assert_equal 1 "$status"
assert_contains "$output" 'FAIL linux-package-build'
assert_contains "$output" 'PASS linux-package-check'
assert_contains "$output" 'PASS linux-package-smoke'
failing_gate=''
run_case --all
assert_equal 0 "$status"
assert_contains "$output" 'SKIP windows: requires windows (host linux)'
assert_contains "$output" 'SKIP aislop: requires windows (host linux)'
assert_contains "$output" 'PASS linux-package-build'
assert_contains "$output" 'PASS linux-package-check'
assert_contains "$output" 'PASS linux-package-smoke'

for selection in only all; do
    output="$(
        # Restore the real executor; double only external script launchers.
        source "$root/tools/ci-preflight.sh"
        preflight_platform() { printf 'windows'; }
        pwsh() {
            printf 'INVOKED %s\n' "$*"
            if [[ "$*" == *'scripts/build-windows.tests.ps1'* ]]; then return 23; fi
        }
        bash() { printf 'INVOKED %s\n' "$*"; }
        dotnet() { printf 'INVOKED %s\n' "$*"; }
        if [[ "$selection" == only ]]; then
            preflight_main --only build-script
        else
            preflight_main --all
        fi
    )"
    status=$?
    assert_equal 1 "$status"
    assert_contains "$output" 'INVOKED -NoProfile -File scripts/build-windows.tests.ps1'
    assert_contains "$output" 'FAIL build-script: exit 23'
    assert_contains "$output" 'INVOKED -NoProfile -File scripts/ci-aislop.tests.ps1'
done

printf 'ci-preflight.tests.sh: %s checks, %s failures\n' "$checks" "$failures"
[[ "$failures" -eq 0 ]]
