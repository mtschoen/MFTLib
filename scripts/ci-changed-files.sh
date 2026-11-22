#!/usr/bin/env bash
source "$(dirname "${BASH_SOURCE[0]}")/changed-files.sh" || exit 2

ci_changed_paths() {
    local base="${GITHUB_BASE_REF:-}"
    [[ -n "$base" && "$base" != -* ]] || return 1
    git fetch --depth=1 origin "$base" >&2 || return 1
    git diff --no-renames --name-only FETCH_HEAD HEAD --
}

# Writes skip_build and one run_<scope> per tooling self-test scope; any failure selects every scope.
ci_changed_files_main() {
    local changed classification selection skip_build=false scope value outputs
    local selected=" ${changed_files_all_path_scopes[*]} "
    if [[ "${GITHUB_EVENT_NAME:-}" != pull_request ]]; then
        printf 'Path filtering applies only to pull requests; running the job in full.\n' >&2
    elif changed="$(ci_changed_paths)"; then
        printf 'Changed files:\n%s\n' "$changed" >&2
        if classification="$(changed_files_skip_build <<< "$changed")" &&
            selection="$(changed_files_path_scopes <<< "$changed")"; then
            if [[ "$classification" == true ]]; then
                skip_build=true
            fi
            selected=" ${selection//$'\n'/ } "
        else
            printf 'Changed-file classification failed; running the job in full.\n' >&2
        fi
    else
        printf 'Changed-file inspection unavailable or failed; running the job in full.\n' >&2
    fi
    if [[ -z "${GITHUB_OUTPUT:-}" ]]; then
        printf 'GITHUB_OUTPUT is required.\n' >&2
        return 1
    fi
    outputs="skip_build=$skip_build"
    for scope in "${changed_files_all_path_scopes[@]}"; do
        value=false
        if [[ "$selected" == *" $scope "* ]]; then value=true; fi
        outputs+=$'\n'"run_${scope//-/_}=$value"
    done
    printf '%s\n' "$outputs" >> "$GITHUB_OUTPUT" || return 1
    printf '%s\n' "$outputs"
}

if [[ "${BASH_SOURCE[0]}" == "$0" ]]; then
    ci_changed_files_main
fi
