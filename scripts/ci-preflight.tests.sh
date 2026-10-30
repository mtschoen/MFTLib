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

run_case --only scripts
assert_equal 0 "$status"
assert_contains "$output" 'PASS script-syntax'
assert_contains "$output" 'PASS script-syntax-tests'
failing_gate=script-syntax
run_case --only scripts
assert_equal 1 "$status"
assert_contains "$output" 'FAIL script-syntax'
assert_contains "$output" 'PASS script-syntax-tests'
failing_gate=''

run_case --only generate-test-report
assert_equal 0 "$status"
assert_contains "$output" 'PASS generate-test-report'

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
assert_contains "$output" 'PASS init-windows'
run_case --only init
assert_equal 0 "$status"
assert_contains "$output" 'PASS init-windows'
assert_contains "$output" 'SKIP init-linux: requires linux (host windows)'
failing_gate=init-windows
run_case --only windows
assert_equal 1 "$status"
assert_contains "$output" 'FAIL init-windows'
assert_contains "$output" 'PASS windows-package-smoke'
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
        assert_contains "$output" 'SKIP init-windows: workflow docs-only/pin-only filter'
        assert_contains "$output" 'SKIP generate-test-report: workflow docs-only/pin-only filter'
    else
        assert_contains "$output" 'PASS windows'
        assert_contains "$output" 'PASS generate-test-report'
    fi
    assert_contains "$output" 'PASS docs'
    assert_contains "$output" 'PASS script-syntax'
    assert_contains "$output" 'PASS script-syntax-tests'
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
failing_gate=linux-package-tests
run_case --only linux-package
assert_equal 1 "$status"
assert_contains "$output" 'FAIL linux-package-tests'
assert_contains "$output" 'PASS linux-package-build'
failing_gate=''
failing_gate=linux-package-build
run_case --all
assert_equal 1 "$status"
assert_contains "$output" 'FAIL linux-package-build'
assert_contains "$output" 'PASS linux-package-check'
assert_contains "$output" 'PASS linux-package-smoke'
failing_gate=''
run_case --all
assert_equal 0 "$status"
assert_contains "$output" 'PASS generate-test-report'
assert_contains "$output" 'SKIP windows: requires windows (host linux)'
assert_contains "$output" 'SKIP windows-package-smoke: requires windows (host linux)'
assert_contains "$output" 'SKIP coverage-child: requires windows (host linux)'
assert_contains "$output" 'SKIP aislop: requires windows (host linux)'
assert_contains "$output" 'PASS linux-package-build'
assert_contains "$output" 'PASS linux-package-check'
assert_contains "$output" 'PASS linux-package-smoke'
run_case --only init
assert_equal 0 "$status"
assert_contains "$output" 'PASS init-linux'
assert_contains "$output" 'SKIP init-windows: requires windows (host linux)'
run_case --only linux
assert_contains "$output" 'PASS init-linux'
assert_contains "$output" 'PASS linux'

run_case --only scripts
assert_equal 0 "$status"
assert_contains "$output" 'SKIP script-syntax: requires windows (host linux)'
assert_contains "$output" 'SKIP script-syntax-tests: requires windows (host linux)'

for gate in build-script generate-test-report; do
    case "$gate" in
        build-script) script='scripts/build-windows.tests.ps1' ;;
        generate-test-report) script='scripts/generate-test-report.ps1' ;;
    esac
    for selection in only all; do
        output="$(
            # Restore the real executor; double only external script launchers.
            source "$root/tools/ci-preflight.sh"
            preflight_platform() { printf 'windows'; }
            pwsh() {
                printf 'INVOKED %s\n' "$*"
                if [[ "${3:-}" == "$script" ]]; then
                    [[ "$#" -eq 3 && "$1" == -NoProfile && "$2" == -File ]] || return 97
                    return 23
                fi
                return 0
            }
            bash() { printf 'INVOKED %s\n' "$*"; }
            dotnet() { printf 'INVOKED %s\n' "$*"; }
            if [[ "$selection" == only ]]; then
                preflight_main --only "$gate"
            else
                preflight_main --all
            fi
        )"
        status=$?
        assert_equal 1 "$status"
        assert_contains "$output" "INVOKED -NoProfile -File $script"
        assert_contains "$output" "FAIL $gate: exit 23"
        if [[ "$gate" == build-script ]]; then
            assert_contains "$output" 'INVOKED -NoProfile -File scripts/ci-aislop.tests.ps1'
            assert_contains "$output" 'INVOKED -NoProfile -File scripts/test-native-coverage-elevated.ps1'
        fi
        if [[ "$selection" == all ]]; then
            assert_contains "$output" 'INVOKED -NoProfile -File scripts/smoke-package-windows.ps1'
        fi
    done
done

for scenario in success build-failure scan-failure; do
    output="$(
        source "$root/tools/ci-preflight.sh"
        preflight_platform() { printf 'windows'; }
        pwsh() {
            printf 'INVOKED %s\n' "$*"
            case "$scenario:$*" in
                'build-failure:'*'scripts/build-windows.ps1'*) return 23 ;;
                'scan-failure:'*'scripts/ci-aislop.ps1'*) return 17 ;;
            esac
            return 0
        }
        preflight_main --only aislop
    )"
    status=$?
    assert_contains "$output" 'INVOKED -NoProfile -File scripts/build-windows.ps1'
    if [[ "$scenario" == build-failure ]]; then
        assert_equal 1 "$status"
        assert_contains "$output" 'FAIL aislop: exit 23'
        if [[ "$output" == *'INVOKED -NoProfile -File scripts/ci-aislop.ps1'* ]]; then
            printf 'FAIL: scanner ran after build failure\n' >&2
            failures=$((failures + 1))
        fi
    else
        assert_contains "$output" 'INVOKED -NoProfile -File scripts/ci-aislop.ps1'
        calls="$(printf '%s\n' "$output" | grep '^INVOKED ')"
        assert_equal $'INVOKED -NoProfile -File scripts/build-windows.ps1\nINVOKED -NoProfile -File scripts/ci-aislop.ps1' "$calls"
        if [[ "$scenario" == scan-failure ]]; then
            assert_equal 1 "$status"
            assert_contains "$output" 'FAIL aislop: exit 17'
        else
            assert_equal 0 "$status"
            assert_contains "$output" 'PASS aislop'
        fi
    fi
done

output="$(
    source "$root/tools/ci-preflight.sh"
    preflight_platform() { printf 'linux'; }
    dotnet() {
        printf 'INVOKED %s\n' "$*"
        [[ "$1" != run ]]
    }
    preflight_main --only init
)"
status=$?
assert_equal 1 "$status"
assert_contains "$output" 'INVOKED restore scripts/optional-provisioning/tests/OptionalProvisioning.Tests.csproj --locked-mode'
assert_contains "$output" 'INVOKED run --project scripts/optional-provisioning/tests/OptionalProvisioning.Tests.csproj -c Release -p:Platform=x64 --no-restore'
assert_contains "$output" 'FAIL init-linux: exit 1'

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
        { sub(/\r$/, "") }
        /^  [a-z][a-z-]*:$/ { selected = ($0 == "  " requested ":") }
        selected { print }
    ' "$root/.gitea/workflows/test.yml"
}
block="$(workflow_job windows)"
prefix="${block%%      - name: Determine changed files*}"
assert_contains "$prefix" 'run: pwsh -NoProfile -File scripts/test-script-syntax.ps1'
assert_contains "$prefix" 'run: pwsh -NoProfile -File scripts/script-syntax.tests.ps1'
assert_equal 0 "$(printf '%s\n' "$prefix" | grep -c 'if:')"
block="$(workflow_job linux-package)"
assert_contains "$block" 'run: bash scripts/check-linux-native.tests.sh'
for job in windows linux linux-package; do
    block="$(workflow_job "$job")"
    assert_equal 1 "$(printf '%s\n' "$block" | grep -c 'id: changed-files')"
    assert_equal 1 "$(printf '%s\n' "$block" | grep -c 'bash scripts/ci-changed-files.sh')"
    assert_contains "$block" 'EVENT_BEFORE: ${{ github.event.before }}'
    assert_contains "$block" 'EVENT_AFTER: ${{ github.event.after }}'
    for obsolete in 'check_files()' 'git fetch' 'git diff' '-notmatch'; do
        assert_equal 0 "$(printf '%s\n' "$block" | grep -F -c -- "$obsolete")"
    done
done
# Every step after a job's detector must be guarded by skip_build (exempt: none); upload and status steps also run always().
unguarded_steps() {
    workflow_job "$1" | awk '
        function flush() {
            if (name != "" && seen && index(condition, "steps.changed-files.outputs.skip_build != '\''true'\''") == 0) print name
            else if (name ~ /^(Upload|Post coverage status)/ && seen && index(condition, "always()") == 0) print name
            name = ""; condition = ""
        }
        /^      - / { flush() }
        /^      - name: / { name = substr($0, 15) }
        /^        id: changed-files/ { seen = 2 }
        /^        if: / { condition = $0 }
        /^      - name: Determine changed files/ { name = "" }
        END { flush() }
    '
}
for job in windows linux linux-package; do
    assert_equal '' "$(unguarded_steps "$job")"
done
real_root="$root"
mkdir -p "$scratch/negative/.gitea/workflows"
root="$scratch/negative"
sed "0,/^        if: steps.changed-files.outputs.skip_build != 'true'\$/s//        if: true/" "$real_root/.gitea/workflows/test.yml" > "$root/.gitea/workflows/test.yml"
assert_equal 'Setup MSBuild' "$(unguarded_steps windows)"
sed 's/always() && //' "$real_root/.gitea/workflows/test.yml" > "$root/.gitea/workflows/test.yml"
assert_equal 'Upload coverage artifact' "$(unguarded_steps linux | head -n 1)"
root="$real_root"
block="$(workflow_job windows)"
assert_contains "$block" 'run: bash scripts/ci-preflight.tests.sh'
assert_contains "$block" 'if ($LASTEXITCODE -ne 0) { exit $LASTEXITCODE }'
assert_contains "$block" "- name: Verify post-clean initialization (destructive)
        if: github.event_name == 'push' && github.ref == 'refs/heads/main' && steps.changed-files.outputs.skip_build != 'true'
        shell: pwsh
        run: pwsh -NoProfile -File scripts/ci-post-clean-destructive.ps1"
assert_equal 0 "$(printf '%s\n' "$block" | grep -c 'git clean')"
block="$(workflow_job docs)"
assert_contains "$block" 'bash scripts/test-agent-instructions.sh'
assert_equal 0 "$(printf '%s\n' "$block" | grep -c 'skip_build')"
assert_equal 0 "$(grep -c 'skip_build' "$root/.gitea/workflows/aislop.yml")"
assert_equal 0 "$(grep -E -c '^[[:space:]]+paths(-ignore)?:' "$root/.gitea/workflows/test.yml")"

if [[ "$OSTYPE" == msys* ]] && command -v pwsh > /dev/null; then
    fixture="$(mktemp -d)"
    git -C "$fixture" init -q
    printf 'exit 0\n' > "$fixture/init.ps1"
    printf '@exit /b 0\r\n' > "$fixture/init.bat"
    git -C "$fixture" add -A
    git -C "$fixture" -c user.email=ci@example.invalid -c user.name=ci commit -q -m fixture
    printf 'untracked\n' > "$fixture/untracked.txt"
    pwsh -NoProfile -File "$root/scripts/ci-post-clean-destructive.ps1" -RepositoryRoot "$(cygpath -m "$fixture")" > /dev/null
    assert_equal 0 "$?"
    assert_equal false "$([[ -e "$fixture/untracked.txt" ]] && echo true || echo false)"
    printf 'exit 3\n' > "$fixture/init.ps1"
    output="$(pwsh -NoProfile -File "$root/scripts/ci-post-clean-destructive.ps1" -RepositoryRoot "$(cygpath -m "$fixture")")"
    assert_equal 1 "$?"
    assert_contains "$output" 'Dirty tree after git clean -ffxd'
    printf 'exit 0\n' > "$fixture/init.ps1"
    fake_git="$(mktemp -d)"
    printf '%s\n' 'if ($args[0] -eq "status") {' \
        '    $n = 1 + [int](Get-Content -LiteralPath "$PSScriptRoot/count" -ErrorAction SilentlyContinue)' \
        '    Set-Content -LiteralPath "$PSScriptRoot/count" -Value $n' \
        '    if ($n -eq [int]$env:FAIL_STATUS_AT) { exit 128 }' \
        '}' '& $env:REAL_GIT @args' 'exit $LASTEXITCODE' > "$fake_git/git.ps1"
    for observation in 1 2 3 4 5; do
        rm -f "$fake_git/count"
        output="$(REAL_GIT="$(cygpath -m "$(command -v git)")" FAIL_STATUS_AT=$observation PATH="$fake_git:$PATH" \
            pwsh -NoProfile -File "$root/scripts/ci-post-clean-destructive.ps1" -RepositoryRoot "$(cygpath -m "$fixture")" 2>&1)"
        assert_equal 1 "$?"
        assert_contains "$output" 'git status exited with code 128'
        assert_equal "$observation" "$(tr -d '\r\n' < "$fake_git/count")"
    done
    rm -rf "$fake_git"
    printf 'exit 0
' > "$fixture/init.ps1"
    git -C "$fixture" config status.showUntrackedFiles invalid
    output="$(pwsh -NoProfile -File "$root/scripts/ci-post-clean-destructive.ps1" -RepositoryRoot "$(cygpath -m "$fixture")" 2>&1)"
    assert_equal 1 "$?"
    assert_contains "$output" 'git status exited with code'
    rm -rf "$fixture"
fi

printf 'ci-preflight.tests.sh: %s checks, %s failures\n' "$checks" "$failures"
[[ "$failures" -eq 0 ]]
