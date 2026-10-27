# MFTLib 0.3.0 release runbook

The [CHANGELOG](../CHANGELOG.md) describes the release contract: one `FileIndex`
for broker-backed live volumes, in-process local scans, saved MFT dumps and
enumeration, with a separate `MFTLib.TestExtensions` package and two samples.
Run these steps at `<release commit>`; pins and measurements belong in the release checklist.

## Ordered release steps

1. Confirm Gitea `main` and GitHub `main` both contain `<release commit>`.
   Gitea is canonical; GitHub supplies the public SourceLink history.
2. Complete attended library and consumer smokes at that commit. Record the exact
   library and consumer pins, test counts, coverage and results in the maintainer's release checklist.
   Verify the required checks in [quality gates](quality-gates.md).
   - Complete the [benchmark release checklist](#benchmark-release-checklist) on a quiet machine before the clean-tree release dry run. Record each scenario's comparison result and the measured code revision in the maintainer's release checklist.
3. Run the dry run on a Windows release host with the documented toolchains and WSL distribution.
   For both modes, set `MFTLIB_GITEA_REPOSITORY_URL` to the canonical repository's
   literal HTTPS or SSH URL. Missing input and remote aliases fail before the pipeline starts.

   ```powershell
   .\scripts\release.ps1
   ```

   The [release script](../scripts/release.ps1) requires a clean tree, no existing
   release tag, and the release commit on both mirrors. It cleans the solution,
   restores packages and runs `scripts/run-coverage.ps1 -Configuration Release`.
   It builds the Release linux-x64 library in WSL through `scripts/build-linux.sh`
   and validates it with `scripts/check-linux-native.sh`. The library path is supplied
   to packaging as `-p:MFTLibLinuxNativeLibrary=<path>`; both managed projects are
   packed and validated with `Assert-ReleasePackages` and `Assert-ReleaseSymbolPackages`.
4. Read the printed glibc and libstdc++ symbol versions. Edit README, CHANGELOG and
   [Linux package](linux-package.md) only if the measured floors differ from glibc
   2.33 and GLIBCXX_3.4.22. Repeat validation at the resulting release commit.
5. **Owner only:** publish from the validated release commit:

   ```powershell
   .\scripts\release.ps1 -Publish
   ```

   The script pushes MFTLib to NuGet first, then MFTLib.TestExtensions. It creates
   the release tag and pushes it to Gitea first, then GitHub. `gh release create`
   attaches both packages and symbol packages, using only the extracted `## 0.3.0`
   CHANGELOG section as its notes. A missing version heading fails before publishing;
   the temporary notes file is deleted after release creation.
6. **Owner only:** retire the file-wizard and git-wizard source bridges after their
   NuGet integrations are verified. Use the [consumer bridge retirement checklist](#consumer-bridge-retirement)
   for the complete MFTLib cleanup and the source-build recipe needed until then.

## Consumer bridge retirement

After both consumers use the published packages and their NuGet integrations are
verified, the owner retires these parts together. Counts below describe the current source
snapshot; locate the named code and recount at retirement.

Delete these four whole files:

| File | Size | Responsibility |
| --- | --- | --- |
| `.gitea/workflows/sync-consumers.yml` | 52 lines | Manual consumer pin-publication workflow |
| `scripts/sync_consumers.sh` | 462 lines | Consumer pin-bump publication |
| `scripts/sync_consumers.tests.sh` | 589 lines | Offline consumer-sync fixtures |
| `scripts/mftlib_submodules.sh` | 118 lines | Shared submodule matcher |

The four files total **1,221 lines**. Also make these dependent edits:

- Delete the four-line comment and the three-line `Test the consumer sync script`
  step in the Linux job of `.gitea/workflows/test.yml` (seven lines).
- Update `tools/ci-preflight.sh`: delete the `sync` arm in `preflight_execute`,
  remove `sync` from the accepted scopes in `preflight_main`, delete the
  `preflight_gate sync sync any yes` call, and delete the `sync-publication`
  skip notice. These are three standalone line deletions and one accepted-scope
  edit.
- Delete only the consumer half of `ScannerAndConsumerWorkflows_UseServerContext`
  in `MFTLib.Tests/AgentInstructionsTests.cs`: the read of the deleted workflow
  and its `GITEA_URL` and `GITEA_TOKEN` assertions. Preserve the scanner
  assertions that precede it and rename the method to
  `ScannerWorkflow_UseServerContext` so its name describes the remaining check.
- Update `docs/quality-gates.md`: remove `sync` from the `--only` scope list,
  the `sync` row of the gate table, and the consumer pin-publication skip entry.
  Update the `AgentInstructionsTests` description to describe scanner server context.
- Delete the three `docs/maintained-developer-tools.md` inventory rows for
  `scripts/mftlib_submodules.sh`, `scripts/sync_consumers.sh` and
  `scripts/sync_consumers.tests.sh`; `AgentInstructionsTests` rejects an inventory
  path that does not exist.
- Remove the [active source-build recipe](#active-source-build-recipe) below
  and update its links in this runbook when the bridges are retired.

The four whole files plus the seven workflow lines and three standalone
dispatcher lines total **1,231 deletable lines** before the managed-test removal,
in-place edits, and documentation cleanup. This is the workflow/script subtotal,
not the entire retirement diff.

The `MFTLIB_SYNC_TOKEN` Actions secret also feeds the TEST-REPORT publisher in
`.gitea/workflows/test.yml`, so it must stay when the sync workflow is retired, or the
publisher must be migrated to another credential first.

Keep the scanner fork's separately named consumer-sync workflow untouched.
Preserve MFTLib's `.gitea/workflows/aislop.yml` and its scanner assertions.

Before retirement, check that every repository path above exists. In the retirement
pull request, run `bash scripts/ci-preflight.tests.sh` and
`dotnet test MFTLib.Tests/MFTLib.Tests.csproj -c Release -p:Platform=x64 --filter FullyQualifiedName~AgentInstructionsTests`;
both must pass with the consumer workflow removed. Build before any `--no-build`
test run so copied documents are current.

This checklist describes an owner release action. The scripts and CI scope remain
active until that action; the quality-gate documentation describes what runs now.

### Active source-build recipe

Consumers `file-wizard` and `git-wizard` build MFTLib from a git submodule until
their package migration is complete:

1. Declare a submodule URL resolving to the canonical MFTLib repository on the consumer's forge.
   Both consumers use `external/MFTLib` with the relative URL `../MFTLib.git`, keeping
   the same Gitea instance and owner. The gitlink is the only stored MFTLib revision.
2. Move the pin in each consumer's own pull request so its code is migrated with
   breaking library changes. The `workflow_dispatch`-only `sync-consumers.yml`
   runs `scripts/sync_consumers.sh` by hand, enumerates `schoen/*` repositories and
   opens `chore/mftlib-pin-bump` pull requests as `claude-code`, using the
   `MFTLIB_SYNC_TOKEN` Actions secret. It updates each matching gitlink; `.gitmodules`
   supplies the submodule path. Repositories without an MFTLib submodule are skipped.
3. A fan-out matching zero consumers, or failing to bump any one consumer, exits
   nonzero. Updating nothing must not report success. The workflow supplies `GITEA_URL`
   from its server context; direct script invocations must provide `GITEA_URL` and
   `GITEA_TOKEN`, while optional `GITEA_OWNER` and `SELF_REPO` retain their documented defaults.
4. Populate source with `git submodule update --init --recursive`, including after
   `git clean -ffxd` removes the checked-out submodule content and other untracked files.

Consumer CI checks out the gitlink rather than cloning a separate MFTLib revision:

```yaml
- uses: actions/checkout@v4
  with:
    submodules: recursive
```

MFTLib source sits at `external/MFTLib`. On Linux, its build script drives CMake
and Ninja into the gitignored `build/linux` directory, then runs the managed Smoke tier (`TestCategory=Smoke`, see [SMOKE.md](../SMOKE.md)) against it:

```bash
bash external/MFTLib/scripts/build-linux.sh
dotnet build external/MFTLib/MFTLib/MFTLib.csproj -c Release -p:Platform=x64
dotnet build external/MFTLib/MFTLibTestExtensions/MFTLibTestExtensions.csproj -c Release -p:Platform=x64
```

On Windows, the shared script restores, locates amd64 Visual Studio MSBuild to
build the native `.vcxproj` without WOW64 redirection in host-mode CI, passes an
absolute trailing-backslash `SolutionDir` so native output lands in the submodule's
root `x64/Release` where managed builds find it, then builds the managed projects:

```powershell
.\external\MFTLib\scripts\build-windows.ps1
```

## Package contents and Linux prerequisite

MFTLib contains `lib/net10.0/MFTLib.dll`, `lib/net10.0/MFTLib.xml`,
`runtimes/win-x64/native/MFTLibNative.dll`,
`runtimes/linux-x64/native/libMFTLibNative.so`, `LICENSE.txt` and `README.md`.
MFTLib.TestExtensions contains its managed assembly, license and the same README,
and depends on exactly MFTLib 0.3.0. Both symbol packages must exist.

The Windows x64 native DLL links the Visual C++ runtime statically: Release uses
`MultiThreaded` and Debug uses `MultiThreadedDebug`. `Assert-ReleasePackages`
extracts `runtimes/win-x64/native/MFTLibNative.dll` from the package and runs
`dumpbin /dependents`, rejecting every `MSVCP*` or `VCRUNTIME*` import. Windows
system imports, including Universal CRT API sets, are allowed. Validation requires
the MSVC C++ tools: it resolves dumpbin from PATH or the Visual Studio installer.
A missing tool, failed command or output without dependency filenames fails validation.

The default WSL prerequisite is `wsl -d Ubuntu-24.04`; direct invocation of
`scripts/build-linux-native.ps1 -Distribution <name>` selects another distribution.
`scripts/check-linux-native.sh` requires x86-64 ELF, no `__gcov` symbols, no RPATH
or RUNPATH, and the exports `OpenMftDumpInput`, `ParseMftDumpInput`,
`CloseMftDumpInput`, `ParseMFTRecordsWithProgress`, `FreeMftResult` and
`GetMftNativeAbiVersion`; it prints the highest imported GLIBC and GLIBCXX versions.
See [Linux package](linux-package.md) for the build and package acceptance contract.

## Smoke checks

- Open two indexes through `BrokerSession.CreateIndexSource()`, start and catch up
  their watches, and verify `Changed` events identify each drive's changes.
  Restart one index with `RescanAsync(cancellationToken)` while its sibling keeps watching;
  finish with `StopWatchingAsync(cancellationToken)` on each index and inspect the results.
- Simulate broker death and confirm terminal `WatchFaultKind.Channel` faults;
  a fresh session and new indexes supply the next connection. Check recoverable
  `Drive` and `Apply` faults separately through their recovery state events.
- The `linux-package` job validates a Release library and package on every pull request and main push that changes more than docs or the scanner pin ([Linux package](linux-package.md)).
- `scripts/package-smoke` loads a saved dump from a locally packed NuGet package on Linux and Windows. The Windows smoke runs both ordinary build and RID-less publish outputs, checks the loaded DLL path, and rejects flat copies ([package smokes](linux-package.md)).
- When the job builds, its artifact `linux-native-<sha>` contains the library and acceptance output for attended inspection ([Linux package](linux-package.md)).
- The glibc floor policy states the actual imported symbol versions printed by the acceptance check ([Linux package](linux-package.md)).

## Release measurement evidence

Attach the coverage artifact (managed TRX plus commit.txt) from the `windows-coverage`
and `linux-coverage` jobs that tested `<release commit>`. Verify that each `commit.txt`
holds that exact checkout commit and that the TRX `<Counters>` element carries the
total, passed, failed, and skipped counts. The Windows files are in
`MFTLib.Tests/coverage-report/`; the Linux files are in `coverage-report/`. Retain the
coverage reports with them and identify their workflow runs in
the release checklist. Windows CI excludes admin tests; record attended admin
testing, consumer pins, and consumer results separately in the release checklist.

### Benchmark release checklist

Use the same machine and workload as the reference for meaningful performance
comparisons; the reports record the hardware and runtime. `compare` checks only
the first matching scenario's median and permits growth of at most 25 percent.
A different machine's result is not a comparable release-performance verdict.
Investigate failed comparisons rather than replacing a reference to hide a
regression. These measurements are attended release work, not a CI timing gate.

This recipe saves the two compact `--out` reports outside the checkout, combines
their report sections into one candidate, compares all four scenarios, and
copies only the accepted small report into the release convention without
capturing console logs:

```powershell
$benchmark = './Benchmark/bin/x64/Release/net10.0/Benchmark.exe'
$reportDirectory = Join-Path ([IO.Path]::GetTempPath()) ('mftlib-release-benchmark-' + [guid]::NewGuid().ToString('N'))
New-Item -ItemType Directory -Path $reportDirectory | Out-Null
$indexReport = Join-Path $reportDirectory 'index.txt'
$walkReport = Join-Path $reportDirectory 'walk.txt'
$candidateReport = Join-Path $reportDirectory 'release.txt'
& $benchmark index --synthetic 1000000 --iterations 5 --out $indexReport
if ($LASTEXITCODE -ne 0) { throw 'Index benchmark failed.' }
& $benchmark enumeration-walk --directories 32768 --files-per-directory 1 --iterations 5 --out $walkReport
if ($LASTEXITCODE -ne 0) { throw 'Enumeration benchmark failed.' }
$reportText = (Get-Content -LiteralPath $indexReport -Raw).TrimEnd() + [Environment]::NewLine + (Get-Content -LiteralPath $walkReport -Raw)
Set-Content -LiteralPath $candidateReport -Value $reportText -Encoding utf8
foreach ($scenario in @('exact-search', 'substring-search')) {
    & $benchmark compare Benchmark/baselines/2026-10-16-abaf975d.txt $candidateReport $scenario
    if ($LASTEXITCODE -ne 0) { throw "Release comparison failed: $scenario" }
}
foreach ($scenario in @('enumeration-walk-sequential', 'enumeration-walk-parallel')) {
    & $benchmark compare Benchmark/baselines/2026-10-20-36f07c79.txt $candidateReport $scenario
    if ($LASTEXITCODE -ne 0) { throw "Release comparison failed: $scenario" }
}
$measuredRevision = git rev-parse --short HEAD
if ($LASTEXITCODE -ne 0) { throw 'Cannot identify the measured revision.' }
$releaseReport = "Benchmark/baselines/$(Get-Date -Format yyyy-MM-dd)-$measuredRevision.txt"
if (Test-Path -LiteralPath $releaseReport) { throw 'Release report already exists; review it before replacing.' }
Copy-Item -LiteralPath $candidateReport -Destination $releaseReport
```

The filename identifies the measured code revision. Review and commit the report
before the clean-tree release dry run, then validate the resulting release
commit using the existing ordered release steps. On subsequent releases, select
the previous accepted report for the same scenarios and workload; the two
explicit references above bootstrap that convention. A future combined report
serves both groups. Retain temporary files only as local scratch, never as
committed logs or captures.
