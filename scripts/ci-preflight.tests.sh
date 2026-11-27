#!/usr/bin/env bash
# Offline dispatcher contracts with gate and executable boundary doubles.
set -uo pipefail
root="$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)"
source "$root/tools/ci-preflight.sh" || exit 1
source "$root/scripts/changed-files.sh" || exit 1

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
failing_gate='script-syntax'
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
assert_contains "$output" 'PASS init-pester'
run_case --only init
assert_equal 0 "$status"
assert_contains "$output" 'PASS init-windows'
assert_contains "$output" 'PASS init-pester'
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
    decision="$(changed_files_skip_build <<< "$paths")"
    decision_status=$?
    assert_equal 0 "$decision_status"
    assert_equal "$expected" "$decision"
    changed_paths="$paths"
    inspection_status=0
    run_case --changed-from main
    assert_equal 0 "$status"
    if [[ "$expected" == true ]]; then
        assert_contains "$output" 'SKIP windows: workflow docs-only/pin-only filter'
        assert_contains "$output" 'SKIP init-windows: changed paths exercise none of its inputs'
        assert_contains "$output" 'SKIP generate-test-report: changed paths exercise none of its inputs'
    else
        assert_contains "$output" 'PASS windows'
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

# Path scopes: each tooling self-test is selected by its own inputs; unknown, unsafe or empty input selects all.
all_path_scopes='preflight-tests build-script aislop-tests coverage-child coverage-status generate-test-report release-offline init sync linux-package-tests'
check_scope_case() {
    local expected="$1" paths="$2" selection
    selection="$(changed_files_path_scopes <<< "$paths")"
    assert_equal 0 "$?"
    selection="${selection//$'\n'/ }"
    selection="${selection% }"
    if [[ "$expected" != "$selection" ]]; then
        printf 'FAIL: path scopes for [%s]\n' "$paths" >&2
    fi
    assert_equal "$expected" "$selection"
}
while IFS='|' read -r expected paths; do
    [[ "$expected" == all ]] && expected="$all_path_scopes"
    check_scope_case "$expected" "$(printf '%b' "$paths")"
done <<'CASES'
|MFTLib/Index/FileIndex.cs
|MFTLib/Index/FileIndex.cs\ndocs/page.md\nREADME.md
|MFTLib.Tests/Index/FileIndexTests.cs\nMFTLibTestExtensions/JournalIsolation.cs
|MFTLibNative/src/parse.cpp\nMFTLibNative/CMakeLists.txt
|SampleProgram.Direct/Program.cs\nSampleProgram.Shared/Host.cs\nSampleProgram.Watch/Program.cs
|Benchmark/baselines/report.json\nExternalAnnotations/MFTLib.xml\nDirectory.Build.targets
|.plan\n.maintenance.json\nLICENSE.txt\nMFTLib.sln.DotSettings\nTEST-REPORT.md
|scripts/package-smoke/Program.cs\nscripts/package-smoke/Directory.Build.props\nscripts/package-smoke/packages.lock.json
build-script|MFTLibNative/MFTLibNative.vcxproj
build-script|MFTLib/MFTLib.csproj
build-script|scripts/package-smoke/PackageSmoke.csproj
build-script|Directory.Build.props\nMFTLib.sln\nMFTLibNative.Linux.props\nnative-coverage.runsettings
build-script coverage-status|MFTLib.Tests/MFTLib.Tests.csproj
preflight-tests coverage-child|scripts/ci-post-clean-destructive.ps1
build-script coverage-child|scripts/build-windows.tests.ps1
build-script coverage-child|scripts/coverage-log.ps1
build-script coverage-child init|scripts/build-windows.ps1
build-script coverage-child release-offline init|scripts/windows-tools.ps1
aislop-tests coverage-child|scripts/ci-aislop.ps1
aislop-tests|.gitignore
coverage-child|scripts/native-coverage-elevated.ps1
coverage-child|scripts/smoke-package-windows.ps1
coverage-child coverage-status generate-test-report|scripts/coverage-status.ps1
build-script coverage-child coverage-status|scripts/run-coverage.ps1
coverage-child coverage-status|scripts/coverage-linux.sh
coverage-child generate-test-report|scripts/generate-test-report.tests.ps1
coverage-child release-offline|scripts/test-release-notes.ps1
build-script coverage-child release-offline|scripts/release.ps1
release-offline|CHANGELOG.md
init|scripts/optional-provisioning/Program.cs
init|scripts/optional-provisioning/tests/AdapterTests.cs
init|init.bat
build-script init|init.sh
build-script init|init.ps1
coverage-child init|scripts/build-linux.sh
coverage-child sync|scripts/sync_consumers.sh
coverage-child linux-package-tests|scripts/check-linux-native.tests.sh
build-script coverage-child coverage-status|scripts/test-coverage-status.ps1\nMFTLib/Code.cs\nMFTLib/MFTLib.csproj
all|
all|\n
all|
all|.gitea/workflows/test.yml
all|.gitea/workflows/aislop.yml
all|scripts/changed-files.sh
all|scripts/ci-changed-files.sh
all|tools/ci-preflight.sh
all|scripts/ci-preflight.tests.sh
all|newdir/file.txt
all|scripts/new-tool.py
coverage-child|scripts/package-smoke/Fixture.PS1
coverage-child init|scripts/optional-provisioning/Fixture.PS1
coverage-child|scripts/Tool.SH\nscripts/nested/Tool.Cmd
all|scripts/package-smoke/Notes.txt
all|.editorconfig
all|MFTLib/Code.cs\nunknown.txt
all|mftlib/Code.cs
all|Scripts/build-windows.ps1
all|MFTLib\\Code.cs
all|/MFTLib/Code.cs
all|MFTLib/../scripts/x.ps1
all|MFTLib//Code.cs
all|MFTLib/
CASES
selection="$(printf 'MFTLib/Code.cs\n' | bash "$root/scripts/changed-files.sh" --unknown-option 2> /dev/null)"
assert_equal 2 "$?"
assert_equal '' "$selection"
# The documented no-argument CLI and the --path-scopes CLI each get one real launch of the executable.
cli_output="$(printf 'docs/page.md\n' | bash "$root/scripts/changed-files.sh")"
assert_equal 0 "$?"
assert_equal true "$cli_output"
cli_output="$(printf 'CHANGELOG.md\n' | bash "$root/scripts/changed-files.sh" --path-scopes)"
assert_equal 0 "$?"
assert_equal release-offline "$cli_output"

# Local --changed-from applies the same path scopes as CI.
check_gate_selection() {
    local paths="$1" gate
    shift
    changed_paths="$paths"
    inspection_status=0
    run_case --changed-from main
    assert_equal 0 "$status"
    assert_contains "$output" 'PASS windows'
    assert_contains "$output" 'PASS docs'
    assert_contains "$output" 'PASS aislop'
    for gate in preflight-tests build-script aislop-tests coverage-child coverage-status generate-test-report release-offline init-windows sync; do
        if [[ " $* " == *" $gate "* ]]; then
            assert_contains "$output" "PASS $gate"
        else
            assert_contains "$output" "SKIP $gate: changed paths exercise none of its inputs"
        fi
    done
}
check_gate_selection 'MFTLib/Index/FileIndex.cs'
check_gate_selection $'MFTLib/Index/FileIndex.cs\ndocs/page.md'
check_gate_selection 'scripts/ci-aislop.ps1' aislop-tests coverage-child
check_gate_selection 'init.bat' init-windows
check_gate_selection 'scripts/sync_consumers.sh' coverage-child sync
check_gate_selection 'scripts/test-release-offline.sh' coverage-child release-offline
check_gate_selection 'tools/ci-preflight.sh' preflight-tests build-script aislop-tests coverage-child coverage-status generate-test-report release-offline init-windows sync
check_gate_selection 'unknown.txt' preflight-tests build-script aislop-tests coverage-child coverage-status generate-test-report release-offline init-windows sync
changed_paths='CHANGELOG.md'
run_case --changed-from main
assert_equal 0 "$status"
assert_contains "$output" 'SKIP windows: workflow docs-only/pin-only filter'
assert_contains "$output" 'PASS release-offline'
changed_paths='docs/Example.csproj'
run_case --changed-from main
assert_equal 0 "$status"
assert_contains "$output" 'SKIP windows: workflow docs-only/pin-only filter'
assert_contains "$output" 'PASS build-script'
assert_contains "$output" 'SKIP aislop-tests: changed paths exercise none of its inputs'
changed_paths='MFTLib/Code.cs'
for selection in all only; do
    if [[ "$selection" == all ]]; then run_case --all; else run_case --only build-script; fi
    assert_contains "$output" 'PASS build-script'
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
assert_contains "$output" 'PASS build-script'
assert_contains "$output" 'PASS init-windows'

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

for dispatch_gate in build-script generate-test-report; do
    case "$dispatch_gate" in
        build-script) script='scripts/build-windows.tests.ps1' ;;
        generate-test-report) script='scripts/generate-test-report.ps1' ;;
    esac
    for dispatch_selection in only all; do
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
            if [[ "$dispatch_selection" == only ]]; then
                preflight_main --only "$dispatch_gate"
            else
                preflight_main --all
            fi
        )"
        status=$?
        assert_equal 1 "$status"
        assert_contains "$output" "INVOKED -NoProfile -File $script"
        assert_contains "$output" "FAIL $dispatch_gate: exit 23"
        if [[ "$dispatch_gate" == build-script ]]; then
            assert_contains "$output" 'INVOKED -NoProfile -File scripts/ci-aislop.tests.ps1'
            assert_contains "$output" 'INVOKED -NoProfile -File scripts/test-native-coverage-elevated.ps1'
        fi
        if [[ "$dispatch_selection" == all ]]; then
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
emitted_outputs="$scratch/emitted outputs.txt"
export GITHUB_OUTPUT="$scratch/actions output.txt"

mkdir -p "$scratch/tools" "$scratch/scripts"
cp "$root/tools/ci-preflight.sh" "$scratch/tools/"
output="$(
    exec 2>&1
    source "$scratch/tools/ci-preflight.sh" || exit 2
    preflight_platform() { printf 'windows'; }
    preflight_execute() { :; }
    preflight_changed_paths() { printf 'docs/page.md\n'; }
    preflight_main --changed-from main && preflight_main --all && preflight_main --only docs
)"
assert_equal 0 "$?"
assert_contains "$output" 'changed-file classification failed; running all gates'
assert_equal 2 "$(grep -cx 'PASS windows' <<< "$output")"
assert_equal 2 "$(grep -cx 'PASS build-script' <<< "$output")"
assert_equal 3 "$(grep -cx 'PASS docs' <<< "$output")"

# A classifier that exits, even successfully, cannot end preflight or select fewer gates.
for early_exit in 'exit 9' 'exit 0' 'changed_files_skip_build() { printf "true\n"; return 9; }' 'changed_files_path_scopes() { printf "release-offline\n"; return 9; }'; do
    printf 'source "%s/scripts/changed-files.sh"\n' "$root" > "$scratch/scripts/changed-files.sh"
    printf '%s\n' "$early_exit" >> "$scratch/scripts/changed-files.sh"
    [[ "$early_exit" == exit* ]] && printf '%s\n' "$early_exit" > "$scratch/scripts/changed-files.sh"
    output="$(
        exec 2>&1
        source "$scratch/tools/ci-preflight.sh" || exit 2
        preflight_platform() { printf 'windows'; }
        preflight_execute() { printf 'EXECUTED %s\n' "$1"; }
        preflight_changed_paths() { printf 'docs/page.md\n'; }
        preflight_main --changed-from main
    )"
    assert_equal 0 "$?"
    assert_contains "$output" 'changed-file classification failed; running all gates'
    assert_contains "$output" 'EXECUTED windows'
    assert_contains "$output" 'PASS build-script'
    assert_contains "$output" 'PREFLIGHT PASS'
done

expected_ci_outputs() {
    local skip="$1" selected="$2" scope value
    [[ "$selected" == all ]] && selected="$all_path_scopes"
    printf 'existing=value\nskip_build=%s' "$skip"
    for scope in $all_path_scopes; do
        value=false
        if [[ " $selected " == *" $scope "* ]]; then value=true; fi
        printf '\nrun_%s=%s' "${scope//-/_}" "$value"
    done
}
run_ci_case() {
    local expected="$1" requested_scopes="${2:-all}"
    : > "$git_log"
    printf 'existing=value\n' > "$GITHUB_OUTPUT"
    output="$(
        source "$root/scripts/ci-changed-files.sh" || exit 2
        case "${failing_classifier:-}" in
            skip-build) changed_files_skip_build() { printf 'true\n'; return 9; } ;;
            path-scopes) changed_files_path_scopes() { return 9; } ;;
        esac
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
    assert_equal "$(expected_ci_outputs "$expected" "$requested_scopes")" "$(cat "$GITHUB_OUTPUT")"
}

export GITHUB_EVENT_NAME=pull_request GITHUB_BASE_REF=main
fetch_status=0 diff_status=0 diff_paths=$'docs/page.md\nREADME.md\n'
run_ci_case true none
assert_equal $'fetch --depth=1 origin main\ndiff --no-renames --name-only FETCH_HEAD HEAD --' "$(cat "$git_log")"

for diff_paths in '' $'docs/page.md\nnew.cs' 'Docs/page.md' '.gitea/workflows/test.yml' 'scripts/changed-files.sh'; do
    run_ci_case false
done
diff_paths=$'MFTLib/Code.cs\ndocs/page.md\n'
run_ci_case false none
diff_paths='scripts/build-windows.tests.ps1'
run_ci_case false 'build-script coverage-child'
diff_paths=$'scripts/optional-provisioning/Program.cs\nMFTLib/Code.cs'
run_ci_case false init
diff_paths='CHANGELOG.md'
run_ci_case true release-offline
diff_paths='docs/Example.csproj'
run_ci_case true build-script
diff_paths='docs/page.md'
diff_status=23
run_ci_case false
assert_contains "$(cat "$git_log")" 'diff --no-renames --name-only'
diff_status=0
fetch_status=24
run_ci_case false
assert_equal 'fetch --depth=1 origin main' "$(cat "$git_log")"
fetch_status=0

# A failing classifier runs everything, whatever it printed.
for diff_paths in 'MFTLib/Code.cs' 'docs/page.md'; do
    for failing_classifier in skip-build path-scopes; do
        run_ci_case false
    done
done
failing_classifier=''

# Even a docs-only push with valid endpoints gets the full run without inspecting Git.
export EVENT_BEFORE=1111111111111111111111111111111111111111 EVENT_AFTER=2222222222222222222222222222222222222222
for GITHUB_EVENT_NAME in '' workflow_dispatch unknown push schedule; do
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
assert_equal "$(expected_ci_outputs false all)" "$(cat "$GITHUB_OUTPUT")"
cp "$GITHUB_OUTPUT" "$emitted_outputs"
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
# Step-level guards only: the job-level WINDOWS_CI switch sits at four spaces and is allowed.
assert_equal 0 "$(printf '%s\n' "$prefix" | grep -c -E '^      (- |  )if:')"
block="$(workflow_job linux-package)"
assert_contains "$block" 'run: bash scripts/check-linux-native.tests.sh'
for job in windows linux linux-package; do
    block="$(workflow_job "$job")"
    assert_equal 1 "$(printf '%s\n' "$block" | grep -c 'id: changed-files')"
    assert_contains "$block" 'id: native-key
        continue-on-error: true'
    assert_contains "$block" "id: native-cache
        continue-on-error: true
        if: steps.changed-files.outputs.skip_build != 'true' && steps.native-key.outcome == 'success' && steps.native-key.outputs.key != ''"
    assert_equal 1 "$(printf '%s\n' "$block" | grep -c 'bash scripts/ci-changed-files.sh')"
    for obsolete in 'check_files()' 'git fetch' 'git diff' '-notmatch'; do
        assert_equal 0 "$(printf '%s\n' "$block" | grep -F -c -- "$obsolete")"
    done
done
# The NuGet cache key hashes CACHE_EPOCH: a raw comma-bearing epoch is an invalid actions/cache key.
for job in linux docs; do
    block="$(workflow_job "$job")"
    assert_equal 0 "$(printf '%s\n' "$block" | grep -F -c "linux-nuget-\${{ steps.nuget-hash.outputs.sha }}-")"
    nuget_run="$(printf '%s\n' "$block" | sed -n '/id: nuget-hash/,/^$/p' | sed -n 's/^ *run: //p')"
    nuget_output="$scratch/nuget-output-$job"
    (cd "$root" && GITHUB_OUTPUT="$nuget_output" CACHE_EPOCH='review,epoch' bash -c "$nuget_run")
    assert_equal 1 "$(grep -c -E '^sha=[0-9a-f]{64}$' "$nuget_output")"
done
# Every step after a job's detector is guarded: tooling self-tests by their own run_<scope> output, everything
# else (build, coverage, package, upload, status) by skip_build. Upload and status steps also run always().
step_guards() {
    workflow_job "$1" | awk '
        function flush() {
            if (name != "" && seen) {
                guard = "unguarded"
                if (index(condition, "steps.changed-files.outputs.skip_build != '\''true'\''")) guard = "skip_build"
                else if (match(condition, /steps\.changed-files\.outputs\.run_[a-z_]+ != '\''false'\''/)) {
                    guard = substr(condition, RSTART + 28, RLENGTH - 39)
                    if (condition != "        if: steps.changed-files.outputs." guard " != '\''false'\''") guard = "invalid " condition
                }
                if (name ~ /^(Upload|Post coverage status)/ && index(condition, "always()") == 0) guard = guard " without always()"
                print name ": " guard
            }
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
unemitted_guards() {
    local line guard
    while IFS= read -r line; do
        guard="${line##*: }"
        if ! grep -q "^$guard=" "$emitted_outputs"; then printf '%s\n' "$line"; fi
    done <<< "$(step_guards "$1")"
}
assert_equal 'Setup MSBuild: skip_build
Select runner-provisioned reportgenerator: skip_build
Test the CI preflight dispatcher: run_preflight_tests
Test Windows build script: run_build_script
Test init.ps1 in process (Pester): skip_build
Test optional provisioning and Windows init: run_init
Test pinned aislop checkout gate: run_aislop_tests
Test coverage status guard: run_coverage_status
Test summary report generator: run_generate_test_report
Test shared coverage child runner: run_coverage_child
Test offline release guards: run_release_offline
Compute the native build cache key: skip_build
Cache the native build output: skip_build
Run coverage: skip_build
Smoke Windows NuGet native resolution: skip_build
Upload coverage artifact: skip_build
Post coverage status: skip_build
Verify post-clean initialization (destructive): skip_build' "$(step_guards windows)"
assert_equal 'Test the consumer sync script: run_sync
Test the CI preflight dispatcher: run_preflight_tests
Test the native build cache: skip_build
Test optional provisioning and Linux init: run_init
Compute NuGet cache key: skip_build
Cache NuGet packages: skip_build
Verify Linux init Release x64 output: skip_build
Compute the native build cache key: skip_build
Cache the native coverage build: skip_build
Run coverage: skip_build
Upload coverage artifact: skip_build' "$(step_guards linux)"
assert_equal 'Test Linux native acceptance checks: run_linux_package_tests
Compute the native build cache key: skip_build
Cache the native Release library: skip_build
Build the Release library and run the managed Smoke tier: skip_build
Check the library: skip_build
Smoke test the package: skip_build
Upload library artifact: skip_build' "$(step_guards linux-package)"
for job in windows linux linux-package; do
    assert_equal '' "$(unemitted_guards "$job")"
done
real_root="$root"
mkdir -p "$scratch/negative/.gitea/workflows"
root="$scratch/negative"
sed "0,/^        if: steps.changed-files.outputs.skip_build != 'true'\$/s//        if: true/" "$real_root/.gitea/workflows/test.yml" > "$root/.gitea/workflows/test.yml"
assert_equal 'Setup MSBuild: unguarded' "$(step_guards windows | head -n 1)"
sed 's/always() && //' "$real_root/.gitea/workflows/test.yml" > "$root/.gitea/workflows/test.yml"
assert_equal 'Upload coverage artifact: skip_build without always()' "$(step_guards linux | grep '^Upload')"
sed 's/outputs\.run_build_script /outputs.run_build_scripts /' "$real_root/.gitea/workflows/test.yml" > "$root/.gitea/workflows/test.yml"
assert_equal 'Test Windows build script: run_build_scripts' "$(unemitted_guards windows)"
sed "s/if: steps.changed-files.outputs.run_build_script/if: false \&\& steps.changed-files.outputs.run_build_script/" "$real_root/.gitea/workflows/test.yml" > "$root/.gitea/workflows/test.yml"
assert_equal "Test Windows build script: invalid         if: false && steps.changed-files.outputs.run_build_script != 'false'" "$(unemitted_guards windows)"
root="$real_root"
block="$(workflow_job windows)"
assert_contains "$block" 'run: bash scripts/ci-preflight.tests.sh'
assert_contains "$block" 'run: pwsh -NoProfile -File scripts/test-init-pester.ps1'
assert_contains "$block" "if (\$LASTEXITCODE -ne 0) { exit \$LASTEXITCODE }"
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
block="$(workflow_job publish-report)"
assert_contains "$block" "github.ref == 'refs/heads/main' &&
      (github.event_name == 'schedule' || (github.event_name == 'push' && github.actor != 'test-report-bot'))"

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
    cat > "$fake_git/git.ps1" <<'POWERSHELL'
if ($args[0] -eq "status") {
    $n = 1 + [int](Get-Content -LiteralPath "$PSScriptRoot/count" -ErrorAction SilentlyContinue)
    Set-Content -LiteralPath "$PSScriptRoot/count" -Value $n
    if ($n -eq [int]$env:FAIL_STATUS_AT) { exit 128 }
}
& $env:REAL_GIT @args
exit $LASTEXITCODE
POWERSHELL
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
