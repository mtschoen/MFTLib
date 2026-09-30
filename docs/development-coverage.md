# Coverage procedures

### Test coverage

**Managed (C#):** Run `scripts/run-coverage.ps1` - builds, runs all tests (including admin with UAC prompt), and reports coverage:
```powershell
.\scripts\run-coverage.ps1                  # full run with admin tests (UAC prompt)
.\scripts\run-coverage.ps1 -NonInteractive  # skip admin tests (CI / headless)
```

The Windows CI publisher validates coverage before posting `pr-crew/coverage`.
A drop of more than 10 percentage points from the latest successful main-line
coverage status, or zero covered executable lines in a tested namespace, posts
a nonnumeric error status and fails the publishing step. Failed collection,
missing/malformed reports, and unavailable baseline lookup also fail closed.
The baseline follows main's first-parent history (up to 100 commits), using
the latest successful coverage status on the nearest measured commit; it is
not a hard-coded percentage. The tested namespace checks cover MFTLib,
MFTLib.Index, TestProgram, and Benchmark; extend that list and its regression
fixtures when adding another tested executable namespace.

Run `pwsh -NoProfile -File scripts/test-coverage-status.ps1` for the offline
publisher regression checks. On a rejected run, inspect the `windows-coverage`
artifact: `MFTLib.Tests/coverage.xml`, `MFTLib.Tests/coverage-report/`, and
`MFTLib.Tests/coverage-run.stdout.log` plus `coverage-run.stderr.log`. Collection
output is captured raw and printed after the child exits. Preserve the artifact
and rerun CI to distinguish a transient measurement failure from a reproducible
drop. An error is not a trusted low-coverage reading and still blocks the gate.
The collector-exit race is an unverified hypothesis; this guard does not repair
hit collection or change the admin coverage merge path.

**Native (C++):** Microsoft.CodeCoverage.Console via `scripts/native-coverage.ps1`:
```powershell
.\scripts\native-coverage.ps1           # cobertura XML output
.\scripts\native-coverage.ps1 -HtmlReport  # also generate HTML
```

The native DLL must be built Debug|x64 (linked with `/PROFILE`) for instrumentation. The script handles build, instrument, test, and report automatically. Settings in `native-coverage.runsettings`.

The USN journal tests need admin. `scripts/native-coverage-elevated.ps1` self-elevates, runs `native-coverage.ps1` hidden, and streams results live to `native-coverage-elevated.log` at the repository root while the visible parent prints new log lines plus a heartbeat (every 30 seconds by default, configurable via `-HeartbeatSeconds` with a 2-second polling granularity). Pass `-TimeoutSeconds <int>` (default 1800) to adjust the warning threshold when running on slower hardware (the parent warns but continues waiting as long as the child process remains alive).
