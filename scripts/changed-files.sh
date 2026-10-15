#!/usr/bin/env bash
# One conservative repository-relative path rule for CI and local preflight.
changed_files_skip_build() (
    export LC_ALL=C
    shopt -u nocasematch
    local path observed=false
    while IFS= read -r path || [[ -n "$path" ]]; do
        path="${path%$'\r'}"
        [[ -z "$path" ]] && continue
        observed=true
        case "$path" in
            *\\*|/*|./*|../*|*/./*|*/../*|*//*|*/|*[[:cntrl:]]*)
                printf 'false\n'
                return 0
                ;;
        esac
        if [[ ! "$path" =~ ^(\.aislop/fork-commit|docs/.+|[^/]+\.md)$ ]]; then
            printf 'false\n'
            return 0
        fi
    done
    printf '%s\n' "$observed"
)

if [[ "${BASH_SOURCE[0]}" == "$0" ]]; then
    changed_files_skip_build
fi
