#!/usr/bin/env bash
# One conservative repository-relative path rule for CI and local preflight.

# Tooling self-test scopes, in output order; docs/quality-gates.md records how each input set was derived.
changed_files_all_path_scopes=(
    preflight-tests build-script aislop-tests coverage-child coverage-status
    generate-test-report release-offline init sync linux-package-tests
)

# Pairs of a case-sensitive glob ("*" also matches "/") and the scope it selects. A path collects every
# matching rule's scope; "all" selects every scope and "none" marks a known path that selects no scope.
# A path that matches no rule selects every scope.
changed_files_path_scope_rules=(
    '.gitea/workflows/*' all
    'scripts/changed-files.sh' all
    'scripts/ci-changed-files.sh' all
    'scripts/ci-preflight.tests.sh' all
    'tools/ci-preflight.sh' all
    'scripts/ci-post-clean-destructive.ps1' preflight-tests
    'scripts/build-windows.ps1' build-script
    'scripts/build-windows.tests.ps1' build-script
    'scripts/windows-tools.ps1' build-script
    'scripts/coverage-child.ps1' build-script
    'scripts/coverage-log.ps1' build-script
    'scripts/native-coverage.ps1' build-script
    'scripts/run-coverage.ps1' build-script
    'scripts/release.ps1' build-script
    'scripts/Test-ReleasePackages.ps1' build-script
    'init.ps1' build-script
    'init.sh' build-script
    'MFTLib.sln' build-script
    'Directory.Build.props' build-script
    'MFTLibNative.Linux.props' build-script
    'native-coverage.runsettings' build-script
    '*.csproj' build-script
    '*.vcxproj' build-script
    'scripts/ci-aislop.ps1' aislop-tests
    'scripts/ci-aislop.tests.ps1' aislop-tests
    'scripts/ci-aislop-evidence.ps1' aislop-tests
    'scripts/ci-aislop-link-repair.ps1' aislop-tests
    '.gitignore' aislop-tests
    'scripts/coverage-status.ps1' coverage-status
    'scripts/test-coverage-status.ps1' coverage-status
    'scripts/run-coverage.ps1' coverage-status
    'scripts/coverage-linux.sh' coverage-status
    'MFTLib.Tests/MFTLib.Tests.csproj' coverage-status
    'scripts/generate-test-report.ps1' generate-test-report
    'scripts/generate-test-report.tests.ps1' generate-test-report
    'scripts/coverage-status.ps1' generate-test-report
    'scripts/test-release-offline.sh' release-offline
    'scripts/test-release-package-validation.ps1' release-offline
    'scripts/test-release-notes.ps1' release-offline
    'scripts/Test-ReleasePackages.ps1' release-offline
    'scripts/WindowsNativeInstructions.ps1' release-offline
    'scripts/windows-tools.ps1' release-offline
    'scripts/release.ps1' release-offline
    'CHANGELOG.md' release-offline
    'init.ps1' init
    'init.sh' init
    'init.bat' init
    'scripts/build-windows.ps1' init
    'scripts/windows-tools.ps1' init
    'scripts/build-linux.sh' init
    'scripts/optional-provisioning/*' init
    'scripts/sync_consumers.sh' sync
    'scripts/sync_consumers.tests.sh' sync
    'scripts/mftlib_submodules.sh' sync
    'scripts/auto_bump_pull_request.sh' sync
    'scripts/check-linux-native.sh' linux-package-tests
    'scripts/check-linux-native.tests.sh' linux-package-tests
    'MFTLib/*' none
    'MFTLib.Tests/*' none
    'MFTLibTestExtensions/*' none
    'MFTLibNative/*' none
    'SampleProgram.Direct/*' none
    'SampleProgram.Shared/*' none
    'SampleProgram.Watch/*' none
    'Benchmark/*' none
    'ExternalAnnotations/*' none
    'scripts/package-smoke/Directory.Build.props' none
    'scripts/package-smoke/Program.cs' none
    'scripts/package-smoke/packages.lock.json' none
    'Directory.Build.targets' none
    'MFTLib.sln.DotSettings' none
    'LICENSE.txt' none
    '.plan' none
    '.maintenance.json' none
)

changed_files_path_is_unsafe() {
    case "$1" in
        *\\*|/*|./*|../*|*/./*|*/../*|*//*|*/|*[[:cntrl:]]*) return 0 ;;
    esac
    return 1
}

changed_files_path_is_docs() {
    [[ "$1" =~ ^(\.aislop/fork-commit|docs/.+|[^/]+\.md)$ ]]
}

changed_files_skip_build() (
    export LC_ALL=C
    shopt -u nocasematch
    local path observed=false
    while IFS= read -r path || [[ -n "$path" ]]; do
        path="${path%$'\r'}"
        [[ -z "$path" ]] && continue
        observed=true
        if changed_files_path_is_unsafe "$path" || ! changed_files_path_is_docs "$path"; then
            printf 'false\n'
            return 0
        fi
    done
    printf '%s\n' "$observed"
)

# Prints the selected tooling self-test scopes, one per line in changed_files_all_path_scopes order.
changed_files_path_scopes() (
    export LC_ALL=C
    shopt -u nocasematch
    local path pattern scope index known observed=false
    local -A selected=()
    while IFS= read -r path || [[ -n "$path" ]]; do
        path="${path%$'\r'}"
        [[ -z "$path" ]] && continue
        observed=true
        if changed_files_path_is_unsafe "$path"; then
            selected[all]=1
            break
        fi
        known=false
        if changed_files_path_is_docs "$path"; then known=true; fi
        # The Windows coverage-child enumeration matches script extensions case-insensitively.
        case "$path:${path,,}" in
            scripts/*:*.ps1|scripts/*:*.psm1|scripts/*:*.sh|scripts/*:*.bat|scripts/*:*.cmd)
                known=true
                selected[coverage-child]=1 ;;
        esac
        for ((index = 0; index < ${#changed_files_path_scope_rules[@]}; index += 2)); do
            pattern="${changed_files_path_scope_rules[index]}"
            # The unquoted right-hand side is the rule's glob.
            if [[ "$path" == $pattern ]]; then
                known=true
                selected["${changed_files_path_scope_rules[index + 1]}"]=1
            fi
        done
        if [[ "$known" == false ]]; then
            selected[all]=1
            break
        fi
    done
    for scope in "${changed_files_all_path_scopes[@]}"; do
        if [[ "$observed" == false || -n "${selected[all]:-}" || -n "${selected[$scope]:-}" ]]; then
            printf '%s\n' "$scope"
        fi
    done
)

changed_files_main() {
    case "$#:${1:-}" in
        0:) changed_files_skip_build ;;
        1:--path-scopes) changed_files_path_scopes ;;
        *) printf 'usage: changed-files.sh [--path-scopes]\n' >&2; return 2 ;;
    esac
}

if [[ "${BASH_SOURCE[0]}" == "$0" ]]; then
    changed_files_main "$@"
fi
