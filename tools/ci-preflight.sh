#!/usr/bin/env bash
# Platform contract: Windows (Git Bash) and Linux; unavailable gates are SKIP.
set -uo pipefail

preflight_platform() {
    case "$(uname -s)" in
        MINGW*|MSYS*|CYGWIN*) printf 'windows' ;;
        Linux*) printf 'linux' ;;
        *) printf 'unsupported' ;;
    esac
}

preflight_changed_paths() {
    # The workflows compare directly with the base tip, not the merge base.
    git diff --no-renames --name-only "$1" -- &&
        git ls-files --others --exclude-standard
}

preflight_execute() {
    case "$1" in
        docs) bash scripts/test-agent-instructions.sh ;;
        preflight-tests) bash scripts/ci-preflight.tests.sh ;;
        build-script) pwsh -NoProfile -File scripts/build-windows.tests.ps1 ;;
        aislop-tests) pwsh -NoProfile -File scripts/ci-aislop.tests.ps1 ;;
        coverage-status) pwsh -NoProfile -File scripts/test-coverage-status.ps1 ;;
        windows) pwsh -NoProfile -File scripts/run-coverage.ps1 -NonInteractive ;;
        windows-package-smoke) pwsh -NoProfile -File scripts/smoke-package-windows.ps1 ;;
        linux) bash scripts/coverage-linux.sh ;;
        sync) bash scripts/sync_consumers.tests.sh ;;
        linux-package-build) bash scripts/build-linux.sh ;;
        linux-package-check) bash scripts/check-linux-native.sh build/linux/libMFTLibNative.so ;;
        linux-package-smoke) bash scripts/smoke-package-linux.sh build/linux/libMFTLibNative.so ;;
        aislop)
            # The scanner requires the build's compiled assemblies for solution analysis.
            pwsh -NoProfile -File scripts/build-windows.ps1 &&
                pwsh -NoProfile -File scripts/ci-aislop.ps1
            ;;
        *) printf 'Unknown gate: %s\n' "$1" >&2; return 2 ;;
    esac
}

preflight_gate() {
    local scope="$1" gate="$2" required_platform="$3" filtered="$4"
    if [[ "$mode" == only && " ${scopes[*]} " != *" $scope "* ]]; then return; fi
    if [[ "$filtered" == yes && "$skip_build" == true ]]; then
        printf 'SKIP %s: workflow docs-only/pin-only filter\n' "$gate"
    elif [[ "$required_platform" != any && "$required_platform" != "$platform" ]]; then
        printf 'SKIP %s: requires %s (host %s)\n' "$gate" "$required_platform" "$platform"
        unavailable=$((unavailable + 1))
    else
        printf 'RUN %s\n' "$gate"
        # A subshell contains scripts' exits and working-directory/environment changes.
        if (preflight_execute "$gate"); then
            printf 'PASS %s\n' "$gate"
            passed=$((passed + 1))
        else
            local gate_status=$?
            printf 'FAIL %s: exit %s\n' "$gate" "$gate_status"
            failed=$((failed + 1))
        fi
    fi
}

preflight_main() {
    local mode='' base='' platform skip_build=false changed file
    local passed=0 failed=0 unavailable=0
    local -a scopes=()
    while [[ $# -gt 0 ]]; do
        case "$1" in
            --all)
                [[ -z "$mode" ]] || return 2
                mode=all; shift ;;
            --changed-from)
                [[ -z "$mode" && $# -ge 2 && "$2" != -* ]] || return 2
                mode=changed; base="$2"; shift 2 ;;
            --only)
                [[ ( -z "$mode" || "$mode" == only ) && $# -ge 2 ]] || return 2
                case "$2" in
                    docs|preflight-tests|build-script|coverage-status|windows|linux|sync|linux-package|aislop) ;;
                    *) printf 'Unknown scope: %s\n' "$2" >&2; return 2 ;;
                esac
                mode=only; scopes+=("$2"); shift 2 ;;
            *) printf 'Unknown argument: %s\n' "$1" >&2; return 2 ;;
        esac
    done
    if [[ -z "$mode" ]]; then
        printf 'usage: bash tools/ci-preflight.sh (--all | --changed-from <ref> | --only <scope>)\n' >&2
        return 2
    fi
    platform="$(preflight_platform)"
    printf 'Preflight; host %s; selection %s\n' "$platform" "$mode"
    if [[ "$mode" == changed ]]; then
        if changed="$(preflight_changed_paths "$base")"; then
            if [[ -n "$changed" ]]; then
                skip_build=true
                while IFS= read -r file; do
                    file="${file%$'\r'}"
                    [[ -z "$file" ]] && continue
                    if [[ ! "$file" =~ ^(\.aislop/fork-commit|docs/.*|[^/]+\.md)$ ]]; then
                        skip_build=false
                        break
                    fi
                done <<< "$changed"
            fi
        else
            printf 'changed-file inspection failed; running all gates\n'
        fi
    fi
    preflight_gate preflight-tests preflight-tests any no
    preflight_gate docs docs any no
    preflight_gate build-script build-script windows yes
    preflight_gate build-script aislop-tests windows yes
    preflight_gate coverage-status coverage-status any yes
    preflight_gate windows windows windows yes
    preflight_gate windows windows-package-smoke windows yes
    preflight_gate sync sync any yes
    preflight_gate linux linux linux yes
    preflight_gate linux-package linux-package-build linux yes
    preflight_gate linux-package linux-package-check linux yes
    preflight_gate linux-package linux-package-smoke linux yes
    preflight_gate aislop aislop windows no
    if [[ "$mode" != only ]]; then
        printf 'SKIP coverage-publication: requires Gitea credentials and main-line status baseline (all hosts)\n'
        printf 'SKIP post-clean-init: destructive push-to-main check in CI checkout (Windows only)\n'
        printf 'SKIP sync-publication: manual workflow writes consumer repositories (all hosts)\n'
        printf 'SKIP actions-infrastructure: runner provisioning, caches and artifact uploads (all hosts)\n'
    fi
    if [[ "$failed" -gt 0 ]]; then
        printf 'PREFLIGHT FAIL: %s passed, %s failed, %s unavailable host gates\n' "$passed" "$failed" "$unavailable"
        return 1
    fi
    printf 'PREFLIGHT PASS (selected host gates only): %s passed, %s unavailable host gates; CI verdict remains partial\n' "$passed" "$unavailable"
}

if [[ "${BASH_SOURCE[0]}" == "$0" ]]; then
    repository_root="$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)"
    cd "$repository_root" || exit 2
    preflight_main "$@"
fi
