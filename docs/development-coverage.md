# Coverage procedures

### Test coverage

**Managed (C#):** Run `scripts/run-coverage.ps1` - builds, runs all tests (including admin with UAC prompt), and reports coverage:
```powershell
pwsh -NoProfile -File .\scripts\run-coverage.ps1                  # full run with admin tests (UAC prompt)
pwsh -NoProfile -File .\scripts\run-coverage.ps1 -NonInteractive  # skip admin tests (CI / headless)
```

The Windows coverage parents use PowerShell 7 (`pwsh`). Both delegate child execution to `scripts/coverage-child.ps1`; `scripts/coverage-log.ps1` owns incremental log reading. The launcher passes target, argument vector, working directory, log/marker paths, and environment as JSON data. An elevated run opens a visible UAC console and streams its log to the waiting parent. The child writes its exit code to a temporary marker and renames it into place on completion, including terminating-error paths. The parent also checks process liveness and exit status; an exited process without valid completion is a failed run.

Managed admin tests retain `TestCategory=RequiresAdmin`, merge the non-admin `coverage.json`, and write Cobertura. The child explicitly receives `VSTEST_TESTHOST_SHUTDOWN_TIMEOUT=120000`. A failed admin child fails coverage even when `coverage.xml` exists; reporting is skipped and `admin-test-output.log` plus coverage intermediates remain for diagnosis. A successful run cleans the managed intermediate log and reports as before. `-NonInteractive` runs non-admin tests directly and never launches an elevated child.

Run `pwsh -NoProfile -File scripts/test-native-coverage-elevated.ps1` for both callers' offline regression checks and non-elevated fixture-child smokes. These checks do not run live-volume tests or UAC. Native `-NoElevation -ForceNonAdmin -TargetScript <fixture>` exercises the same shared protocol. Native `-TimeoutSeconds` remains a warning threshold while the child is alive, and `-HeartbeatSeconds` remains evaluated at two-second polling granularity. Logs are drained through the last unterminated line before completion is reported.

The Windows CI publisher validates coverage before posting `pr-crew/coverage`.
A drop of more than 10 percentage points from the latest successful main-line
coverage status, or zero covered executable lines in a tested namespace, posts
a nonnumeric error status and fails the publishing step. Failed collection,
missing/malformed reports, and unavailable baseline lookup also fail closed.
The baseline follows main's first-parent history (up to 100 commits), using
the latest successful coverage status on the nearest measured commit; it is
not a hard-coded percentage. The tested namespace checks cover MFTLib,
MFTLib.Index, MFTLib.NativeInterop, SampleProgram.Direct, SampleProgram.Watch, Benchmark, and MFTLibTestExtensions; extend that list
and its regression fixtures when adding another tested executable namespace.

MFTLibTestExtensions is additionally required to have complete line, branch, and
executable-method coverage. The publisher checks the assembly's raw Cobertura
line hits, branch counts, and method records; partial coverage, absent evidence,
and malformed evidence fail closed even when aggregate coverage would pass.

Attach the coverage artifact (managed TRX plus commit.txt). Each coverage run copies its
TRX file (the `<Counters>` element carries total, passed, failed, skipped) and writes
`commit.txt` (`git rev-parse HEAD`) into the directory the artifact already uploads,
`MFTLib.Tests/coverage-report/` on Windows and `coverage-report/` on Linux. Both are
deleted before the test run, and the job fails if git exits nonzero. Windows copies the
non-admin pass only.

Run `pwsh -NoProfile -File scripts/test-coverage-status.ps1` for the offline
publisher regression checks. On a rejected run, inspect the `windows-coverage`
artifact: `MFTLib.Tests/coverage.xml`, `MFTLib.Tests/coverage-report/`, and
`MFTLib.Tests/coverage-run.stdout.log` plus `coverage-run.stderr.log`. Collection
output is captured raw and printed after the child exits. Preserve the artifact
and rerun CI to distinguish a transient measurement failure from a reproducible
drop. An error is not a trusted low-coverage reading and still blocks the gate.
The publisher guard does not repair hit collection or change the admin coverage
merge path. The known cause of a passing run with missing hits is vstest killing
the test host 100 ms after the run ends: coverlet writes each module's hit file
from a ProcessExit handler, so a kill mid-flush leaves later modules partly or
wholly unrecorded (Benchmark 33.45%, SampleProgram.Watch 0%, every test green).
`run-coverage.ps1` (in its parent environment and child request data) and `coverage-linux.sh` therefore set
`VSTEST_TESTHOST_SHUTDOWN_TIMEOUT`, and `test-coverage-status.ps1` fails if either
drops it. Setting that variable to `1` reproduces the signature locally.

`MFTLib.Tests.csproj` sets coverlet's `SingleHit`, so every coverage run records a
line or branch once instead of counting it. Coverlet's default recorder performs one
locked increment on a process-wide counter array per sequence point; with the suite's
class-level parallelism, threads running the same instrumented loops (name hashing
and folding, per character) contend for those cache lines and run several times
slower. The full Linux suite on a 16-core host took 66 to 83 seconds and a
name-pool growth scan exceeded its 10 second hang guard after burning 5.4 seconds
of CPU it needs 1.1 seconds for alone; with `SingleHit` the suite takes 42 seconds.
Every gate reads hits only as zero or nonzero, so the measurement is unchanged.
`test-coverage-status.ps1` fails if the project drops the setting.

**Native (C++):** Microsoft.CodeCoverage.Console via `scripts/native-coverage.ps1`:
```powershell
.\scripts\native-coverage.ps1           # cobertura XML output
.\scripts\native-coverage.ps1 -HtmlReport  # also generate HTML
```

Native coverage resolves `Microsoft.CodeCoverage.Console.exe` through `scripts/windows-tools.ps1` in the Visual Studio installation selected by the shared checked discovery. The executable must exist under `Common7/IDE/Extensions/Microsoft/CodeCoverage.Console`; a missing coverage component fails with its expected path. Ordinary builds and init require MSBuild, not the optional coverage component. `scripts/native-coverage.ps1` calls `scripts/build-windows.ps1 -Configuration Debug`, including restore, before instrumenting `MFTLib.Tests/bin/x64/Debug/net10.0/MFTLibNative.dll`. Debug|x64 links with `/PROFILE`; `Directory.Build.targets` copies the root native DLL and PDB into the test output. Collection uses `native-coverage.runsettings`; `-HtmlReport` adds HTML reporting.

The USN journal tests need admin. Run `pwsh -NoProfile -File .\scripts\native-coverage-elevated.ps1` to self-elevate, run `native-coverage.ps1` in a visible elevated console window that closes when the run ends, and stream results live to `native-coverage-elevated.log` at the repository root while the visible parent prints new log lines plus a heartbeat (every 30 seconds by default, configurable via `-HeartbeatSeconds` with a 2-second polling granularity). Pass `-TimeoutSeconds <int>` (default 1800) to adjust the warning threshold when running on slower hardware (the parent warns but continues waiting as long as the child process remains alive).

**Coverage exclusions:** `[ExcludeFromCodeCoverage(Justification = "...")]` is allowed only on a one-line public pass-through shim whose single call forwards to an internal member that has its own tests, such as the Windows-only entry points listed in [API decisions](api-decisions.md) ("Windows-only annotations sit on the member that reaches Windows"). Every use carries a one-line Justification naming the target. Never use it on a member with logic, a branch or a seam, and never to hide a gap.
