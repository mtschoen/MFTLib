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
assert_contains "$output" 'PASS coverage-child'
assert_contains "$output" 'PASS windows'
assert_contains "$output" 'PASS windows-package-smoke'
assert_contains "$output" 'PASS aislop'
assert_contains "$output" 'SKIP linux: requires linux (host windows)'
assert_contains "$output" 'PREFLIGHT FAIL'
failing_gate=''

failing_gate=coverage-child
run_case --only build-script
assert_equal 1 "$status"
assert_contains "$output" 'FAIL coverage-child'
failing_gate=''

run_case --only windows
assert_equal 0 "$status"
assert_contains "$output" 'PASS windows'
assert_contains "$output" 'PASS windows-package-smoke'
failing_gate=windows-package-smoke
run_case --only windows
assert_equal 1 "$status"
assert_contains "$output" 'FAIL windows-package-smoke'
failing_gate=''

run_case --only docs --only coverage-status
assert_equal 0 "$status"
assert_contains "$output" 'Preflight; host windows; selection only'
assert_contains "$output" 'PASS docs'
assert_contains "$output" 'PASS coverage-status'
assert_contains "$output" 'PREFLIGHT PASS (selected host gates only)'
if [[ "$output" == *'EXECUTED windows'* ]]; then failures=$((failures + 1)); fi

check_path_case() {
    local expected="$1" paths="$2" decision decision_status
    decision="$(printf '%s' "$paths" | bash "$root/scripts/changed-files.sh")"
    decision_status=$?
    assert_equal 0 "$decision_status"
    assert_equal "$expected" "$decision"
    changed_paths="$paths"
    inspection_status=0
    run_case --changed-from main
    assert_equal 0 "$status"
    if [[ "$expected" == true ]]; then
        assert_contains "$output" 'SKIP windows: workflow docs-only/pin-only filter'
    else
        assert_contains "$output" 'PASS windows'
    fi
    assert_contains "$output" 'PASS docs'
    assert_contains "$output" 'PASS aislop'
}

for paths in \
    'docs/quality-gates.md' '.aislop/fork-commit' 'AGENTS.md' \
    $'docs/page.md\nREADME.md' 'Release Notes.md' ' README.md' \
    'docs/design notes.txt' 'docs/nested/UPPER.MD' \
    $'docs/page.md\r\nREADME.md\r\n' $'\ndocs/page.md\n'; do
    check_path_case true "$paths"
done
for paths in \
    '' $'\n\r\n' '   ' $'\t\r\n' $'docs/page.md\n   \n' \
    '.md' 'nested/README.md' 'MFTLib/Code.cs' $'docs/page.md\nnew.cs' \
    'Docs/page.md' 'DOCS/page.md' 'README.MD' '.AISLOP/fork-commit' \
    '.aislop/FORK-COMMIT' '.aislop/fork-commit-extra' \
    ' docs/page.md' 'README.md ' 'docs\page.md' 'nested\README.md' \
    './README.md' '/README.md' 'docs/../Code.cs' 'docs/./page.md' \
    'docs//page.md' 'docs/' $'docs/tab\tname.md' '"docs/quoted\\nname.md"'; do
    check_path_case false "$paths"
done

# A sourceable classifier must not inherit case-insensitive matching.
decision="$(
    shopt -s nocasematch
    source "$root/scripts/changed-files.sh" || exit 2
    printf 'Docs/page.md\n' | changed_files_skip_build
)"
assert_equal 0 "$?"
assert_equal false "$decision"
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
assert_contains "$output" 'SKIP windows-package-smoke: requires windows (host linux)'
assert_contains "$output" 'SKIP coverage-child: requires windows (host linux)'
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
    assert_contains "$output" 'INVOKED -NoProfile -File scripts/test-native-coverage-elevated.ps1'
    if [[ "$selection" == all ]]; then
        assert_contains "$output" 'INVOKED -NoProfile -File scripts/smoke-package-windows.ps1'
    fi
done

scratch="$(mktemp -d)" || exit 1
trap 'rm -rf -- "$scratch"' EXIT
git_log="$scratch/git calls.txt"
export GITHUB_OUTPUT="$scratch/actions output.txt"
before_commit=1111111111111111111111111111111111111111
after_commit=2222222222222222222222222222222222222222
zero_commit=0000000000000000000000000000000000000000

run_ci_case() {
    local expected="$1"
    : > "$git_log"
    printf 'existing=value\n' > "$GITHUB_OUTPUT"
    output="$(
        source "$root/scripts/ci-changed-files.sh" || exit 2
        git() {
            printf '%s\n' "$*" >> "$git_log"
            case "$1" in
                fetch)
                    printf '%s\n' 'docs/fetch-diagnostic.md'
                    return "$fetch_status" ;;
                diff)
                    printf '%s' "$diff_paths"
                    return "$diff_status" ;;
                *) printf 'Unexpected git command: %s\n' "$*" >&2; return 97 ;;
            esac
        }
        ci_changed_files_main
    )"
    status=$?
    assert_equal 0 "$status"
    assert_equal "$(printf 'existing=value\nskip_build=%s' "$expected")" "$(cat "$GITHUB_OUTPUT")"
}

export GITHUB_EVENT_NAME=pull_request GITHUB_BASE_REF=main
export EVENT_BEFORE="$before_commit" EVENT_AFTER="$after_commit"
fetch_status=0 diff_status=0 diff_paths=$'docs/page.md\nREADME.md\n'
run_ci_case true
assert_equal $'fetch --depth=1 origin main\ndiff --no-renames --name-only FETCH_HEAD HEAD --' "$(cat "$git_log")"

GITHUB_EVENT_NAME=push
run_ci_case true
assert_equal "$(printf 'fetch --depth=1 origin %s\ndiff --no-renames --name-only %s %s --' "$before_commit" "$before_commit" "$after_commit")" "$(cat "$git_log")"

for GITHUB_EVENT_NAME in pull_request push; do
    for diff_paths in '' 'MFTLib/Code.cs' $'docs/page.md\nnew.cs' 'Docs/page.md'; do
        run_ci_case false
    done
    diff_paths='docs/page.md'
    diff_status=23
    run_ci_case false
    assert_contains "$(cat "$git_log")" 'diff --no-renames --name-only'
    diff_status=0
    fetch_status=24
    run_ci_case false
    if [[ "$GITHUB_EVENT_NAME" == pull_request ]]; then
        assert_equal 'fetch --depth=1 origin main' "$(cat "$git_log")"
    else
        assert_equal "fetch --depth=1 origin $before_commit" "$(cat "$git_log")"
    fi
    fetch_status=0
done

for GITHUB_EVENT_NAME in '' workflow_dispatch unknown; do
    run_ci_case false
    assert_equal '' "$(cat "$git_log")"
done
GITHUB_EVENT_NAME=pull_request
GITHUB_BASE_REF=''
run_ci_case false
assert_equal '' "$(cat "$git_log")"
GITHUB_BASE_REF=missing-reference
fetch_status=1
run_ci_case false
assert_equal 'fetch --depth=1 origin missing-reference' "$(cat "$git_log")"
fetch_status=0
GITHUB_BASE_REF=main
GITHUB_EVENT_NAME=push
for invalid_commit in '' "$zero_commit" missing-reference; do
    EVENT_BEFORE="$invalid_commit" EVENT_AFTER="$after_commit"
    run_ci_case false
    assert_equal '' "$(cat "$git_log")"
    EVENT_BEFORE="$before_commit" EVENT_AFTER="$invalid_commit"
    run_ci_case false
    assert_equal '' "$(cat "$git_log")"
done

# Direct CLI proves execution guard, env inputs, append semantics, and spaced path.
# Unknown event cannot legitimately invoke Git.
git() {
    printf 'Unexpected CLI git command: %s\n' "$*" >> "$git_log"
    return 97
}
export -f git
export git_log
GITHUB_EVENT_NAME=unknown
: > "$git_log"
printf 'existing=value\n' > "$GITHUB_OUTPUT"
output="$(bash "$root/scripts/ci-changed-files.sh")"
assert_equal 0 "$?"
assert_equal $'existing=value\nskip_build=false' "$(cat "$GITHUB_OUTPUT")"
assert_equal '' "$(cat "$git_log")"
output="$(GITHUB_OUTPUT="$scratch/missing/output" bash "$root/scripts/ci-changed-files.sh" 2>&1)"
status=$?
assert_equal 1 "$status"
output="$(GITHUB_OUTPUT='' bash "$root/scripts/ci-changed-files.sh" 2>&1)"
status=$?
assert_equal 1 "$status"
assert_equal '' "$(cat "$git_log")"
unset -f git

# Exercise the real local input adapter, replacing only its Git boundary.
for scenario in allowed untracked-source diff-failure untracked-failure; do
    : > "$git_log"
    output="$(
        source "$root/tools/ci-preflight.sh" || exit 2
        preflight_platform() { printf 'windows'; }
        preflight_execute() { printf 'EXECUTED %s\n' "$1"; }
        git() {
            printf '%s\n' "$*" >> "$git_log"
            case "$1" in
                diff)
                    printf 'docs/tracked.md\n'
                    [[ "$scenario" != diff-failure ]] ;;
                ls-files)
                    if [[ "$scenario" == untracked-source ]]; then
                        printf 'new.cs\n'
                    else
                        printf 'docs/untracked.md\n'
                    fi
                    [[ "$scenario" != untracked-failure ]] ;;
                *) printf 'Unexpected git command: %s\n' "$*" >&2; return 97 ;;
            esac
        }
        preflight_main --changed-from main
    )"
    assert_equal 0 "$?"
    if [[ "$scenario" == allowed ]]; then
        assert_contains "$output" 'SKIP windows: workflow docs-only/pin-only filter'
    else
        assert_contains "$output" 'PASS windows'
    fi
    if [[ "$scenario" == diff-failure ]]; then
        assert_equal 'diff --no-renames --name-only main --' "$(cat "$git_log")"
    else
        assert_equal $'diff --no-renames --name-only main --\nls-files --others --exclude-standard' "$(cat "$git_log")"
    fi
    if [[ "$scenario" == *failure ]]; then
        assert_contains "$output" 'changed-file inspection failed; running all gates'
    fi
done

workflow_job() {
    awk -v requested="$1" '
        /^  [a-z][a-z-]*:$/ { selected = ($0 == "  " requested ":") }
        selected { print }
    ' "$root/.gitea/workflows/test.yml"
}
for job in windows linux linux-package; do
    block="$(workflow_job "$job")"
    assert_equal 1 "$(printf '%s\n' "$block" | grep -c 'id: changed-files')"
    assert_equal 1 "$(printf '%s\n' "$block" | grep -c 'bash scripts/ci-changed-files.sh')"
    assert_contains "$block" 'EVENT_BEFORE: ${{ github.event.before }}'
    assert_contains "$block" 'EVENT_AFTER: ${{ github.event.after }}'
    for obsolete in 'check_files()' 'git fetch' 'git diff' '-notmatch'; do
        assert_equal 0 "$(printf '%s\n' "$block" | grep -F -c -- "$obsolete")"
    done
    case "$job" in
        windows) expected_guards=20 ;;
        linux) expected_guards=7 ;;
        linux-package) expected_guards=4 ;;
    esac
    assert_equal "$expected_guards" "$(printf '%s\n' "$block" | grep -c "^        if: .*steps.changed-files.outputs.skip_build != 'true'")"
done
block="$(workflow_job windows)"
assert_contains "$block" 'run: bash scripts/ci-preflight.tests.sh'
assert_contains "$block" 'if ($LASTEXITCODE -ne 0) { exit $LASTEXITCODE }'
assert_equal 7 "$(printf '%s\n' "$block" | grep -F -c "if: github.event_name == 'push' && github.ref == 'refs/heads/main' && steps.changed-files.outputs.skip_build != 'true'")"
block="$(workflow_job docs)"
assert_contains "$block" 'bash scripts/test-agent-instructions.sh'
assert_equal 0 "$(printf '%s\n' "$block" | grep -c 'skip_build')"
assert_equal 0 "$(grep -c 'skip_build' "$root/.gitea/workflows/aislop.yml")"
assert_equal 0 "$(grep -E -c '^[[:space:]]+paths(-ignore)?:' "$root/.gitea/workflows/test.yml")"

printf 'ci-preflight.tests.sh: %s checks, %s failures\n' "$checks" "$failures"
[[ "$failures" -eq 0 ]]
