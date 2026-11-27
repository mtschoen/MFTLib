#!/usr/bin/env bash
# Offline assertions on the benchmark report workflow and the CI that must ignore its commits, with negative controls.
set -uo pipefail
root="$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)"
workflow="$root/.gitea/workflows/benchmark-report.yml"
scratch="$(mktemp -d)"
trap 'rm -rf "$scratch"' EXIT

failures=0
checks=0
expect() {
    checks=$((checks + 1))
    if ! "${@:2}" > /dev/null 2>&1; then
        printf 'FAIL: %s\n' "$1" >&2
        failures=$((failures + 1))
    fi
}

# Prints one line per violated rule of the workflow at $1 (empty when it conforms).
workflow_violations() {
    local file="$1" top
    # Top-level trigger keys sit two spaces below "on:".
    top="$(awk '/^on:/ { inside = 1; next } /^[a-z]/ { inside = 0 } inside && /^  [a-z_]+:/ { print $1 }' "$file")"
    grep -qx 'schedule:' <<< "$top" || echo 'no schedule trigger'
    grep -qx 'workflow_dispatch:' <<< "$top" || echo 'no manual trigger'
    if grep -Eqx 'push:|pull_request:' <<< "$top"; then echo 'a push or pull request trigger could loop on the bot commit'; fi
    grep -Eq '^    - cron: ' "$file" || echo 'no cron entry'
    grep -qx '    runs-on: benchmark-quiet-host' "$file" || echo 'runs-on does not name the benchmark host label'
    grep -q "github.ref == 'refs/heads/main'" "$file" || echo 'the job is not limited to main'
    grep -q 'token: ${{ secrets.CI_REPORT_TOKEN }}' "$file" || echo 'checkout does not use the report bot token'
    grep -q 'GITEA_TOKEN: ${{ secrets.CI_REPORT_TOKEN }}' "$file" || echo 'issue and push token missing'
    grep -q 'SERVER_URL: ${{ github.server_url }}' "$file" || echo 'server url is not taken from the workflow'
    grep -q 'REPOSITORY: ${{ github.repository }}' "$file" || echo 'repository is not taken from the workflow'
    grep -q "steps.decide.outputs.run == 'true'" "$file" || echo 'later steps are not gated on the decision'
    grep -q "Action = 'Decide'" "$file" || echo 'no decide step'
    grep -q 'cancel-in-progress: false' "$file" || echo 'a running capture may be cancelled'
}

violations="$(workflow_violations "$workflow")"
expect 'the workflow conforms' test -z "$violations"
[[ -z "$violations" ]] || printf '%s\n' "$violations" >&2

mutated_violations() {
    sed "$1" "$workflow" > "$scratch/mutated.yml"
    workflow_violations "$scratch/mutated.yml"
}
expect 'negative control: a push trigger is rejected' test -n "$(mutated_violations 's/^  workflow_dispatch:/  push:\n  workflow_dispatch:/')"
expect 'negative control: a generic runner label is rejected' test -n "$(mutated_violations 's/benchmark-quiet-host/windows-latest/')"
expect 'negative control: a missing schedule is rejected' test -n "$(mutated_violations 's/^  schedule:/  scheduled:/')"
expect 'negative control: a hard-coded server is rejected' test -n "$(mutated_violations 's/SERVER_URL: .*/SERVER_URL: x/')"

# The script the workflow calls guards against the bot's own commits and has a default bot identity.
expect 'the refresh script skips bot-only commits' grep -q 'only report bot commits follow the measured revision' "$root/scripts/benchmark-report-refresh.ps1"
expect 'the refresh script names the same bot as publish-report' grep -q "BotName = 'test-report-bot'" "$root/scripts/benchmark-report-refresh.ps1"
expect 'publish-report still skips the bot' grep -q "github.actor != 'test-report-bot'" "$root/.gitea/workflows/test.yml"
for script in benchmark-report-refresh.ps1 benchmark-quiet-gate.ps1 benchmark-report-capture.ps1 benchmark-report-issue.ps1; do
    expect "the workflow's scripts exist: $script" test -f "$root/scripts/$script"
done

# Other CI ignores the report commits as it ignores TEST-REPORT.md.
expect 'aislop ignores baseline-only pushes' grep -q "paths-ignore: \['TEST-REPORT.md', 'Benchmark/baselines/\*\*'\]" "$root/.gitea/workflows/aislop.yml"
expect 'sync-consumers ignores baseline-only pushes' grep -q "'Benchmark/baselines/\*\*'" "$root/.gitea/workflows/sync-consumers.yml"
expect 'test.yml has no path filters that could leave required checks pending' test "$(grep -E -c '^[[:space:]]+paths(-ignore)?:' "$root/.gitea/workflows/test.yml")" -eq 0

if ((failures > 0)); then
    printf '%d of %d checks failed\n' "$failures" "$checks" >&2
    exit 1
fi
printf 'benchmark report workflow tests passed: %d checks\n' "$checks"
