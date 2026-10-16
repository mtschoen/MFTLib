#!/usr/bin/env bash
# Offline release guards shared by CI and local preflight; requires pwsh.
set -uo pipefail

repository_root="$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)" || exit 1
cd "$repository_root" || exit 1
failed=0
for regression in test-release-package-validation.ps1 test-release-notes.ps1; do
    printf 'RUN %s\n' "$regression"
    if pwsh -NoProfile -File "scripts/$regression"; then
        printf 'PASS %s\n' "$regression"
    else
        regression_status=$?
        printf 'FAIL %s: exit %s\n' "$regression" "$regression_status"
        failed=$((failed + 1))
    fi
done
if [[ "$failed" -gt 0 ]]; then
    printf 'RELEASE OFFLINE FAIL: %s failed\n' "$failed"
    exit 1
fi
printf 'RELEASE OFFLINE PASS\n'
