#!/usr/bin/env bash
source "$(dirname "${BASH_SOURCE[0]}")/changed-files.sh" || exit 2

ci_changed_paths() {
    local base head
    case "${GITHUB_EVENT_NAME:-}" in
        pull_request)
            base="${GITHUB_BASE_REF:-}"
            [[ -n "$base" && "$base" != -* ]] || return 1
            git fetch --depth=1 origin "$base" >&2 || return 1
            base=FETCH_HEAD
            head=HEAD
            ;;
        push)
            base="${EVENT_BEFORE:-}"
            head="${EVENT_AFTER:-}"
            [[ "$base" =~ ^[[:xdigit:]]{40}$ && "$head" =~ ^[[:xdigit:]]{40}$ ]] || return 1
            [[ "$base" != 0000000000000000000000000000000000000000 &&
               "$head" != 0000000000000000000000000000000000000000 ]] || return 1
            git fetch --depth=1 origin "$base" >&2 || return 1
            ;;
        *) return 1 ;;
    esac
    git diff --no-renames --name-only "$base" "$head" --
}

ci_changed_files_main() {
    local changed classification skip_build=false
    if changed="$(ci_changed_paths)"; then
        printf 'Changed files:\n%s\n' "$changed" >&2
        if classification="$(changed_files_skip_build <<< "$changed")"; then
            if [[ "$classification" == true ]]; then
                skip_build=true
            fi
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
    printf 'skip_build=%s\n' "$skip_build" >> "$GITHUB_OUTPUT" || return 1
    printf 'skip_build=%s\n' "$skip_build"
}

if [[ "${BASH_SOURCE[0]}" == "$0" ]]; then
    ci_changed_files_main
fi
