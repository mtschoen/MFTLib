# MFTLib - Test Report

2026-09-30

| Field | Value |
| --- | --- |
| Status | Issue 293 review fixes meet the requested verification bar |
| Mode | Maintain coverage of changed code, with the specified Windows and existing lint baseline |
| Git | fix/293-rescan-retires-watch-at-commit, bc411f88f28e3bc24033e49837af7a6feb93144c plus the review fixes in this commit |
| Tests | 2012 total: 2006 passed, 0 failed, 6 platform skips; RequiresAdmin tests excluded by NonInteractive |
| Targeted tests | 117 passed before the final follow-up; all 9 cases in the new regression fixture passed after it, and all passed in the final full run |
| Coverage | 6935/6983 lines (99.31%), 48 uncovered across all assemblies |
| MFTLib namespace | 2780/2780 lines (100.000000%) |
| MFTLib.Index namespace | 3415/3424 lines (99.737150%); 9 uncovered Unix-only lines in unchanged files |
| Changed production files | 100% line coverage; no added or changed executable line versus gitea/main is uncovered |
| Lint | aislop 0.16.0: score 99/100, 0 errors, 5 warnings, 0 fixable; exit 1; no new suppressions |
| Native | Release x64 build passed using the coverage script's amd64 MSBuild and PlatformToolset=v143 |
| Platform | Windows; Linux, macOS, native instrumented coverage, and elevated live-volume tests not run |

## Lint

| Engine | Findings | Locations |
| --- | --- | --- |
| format | 0 | None |
| lint | 2 | MFTLib.Tests/NativeSeamIsolationFixtures.cs:73,79 (AsyncFixer01) |
| code-quality | 1 | MFTLib/Broker/Host/JournalBrokerHost.cs:46 (too-many-params) |
| ai-slop | 2 | MFTLib/Index/CachedBlockDeletionOutcome.cs:8,10 (redundant-doc-comment) |
| security | 0 | None |

All five warnings are in files unchanged by this branch, verified with
`git diff gitea/main -- <file>`. There are no findings in files this branch touches.
The configured absolute score threshold is 100, so aislop exits 1 for the existing
five warnings; these are the accepted baseline for this lane.

## Uncovered namespace lines

| File | Lines |
| --- | --- |
| MFTLib/Index/BlockFile.Flush.cs | 82,83,104,105 |
| MFTLib/Index/CacheDirectory.cs | 335,337,340,342,345 |

These nine unchanged lines implement Unix paths that cannot execute on Windows
and are exercised by the Linux CI job. No new exclusions were introduced.
Namespace totals use the grouping in `scripts/coverage-status.ps1`.

## Final full run

One final `run-coverage.ps1 -NonInteractive` run completed with exit 0:

```text
Test Run Successful.
Total tests: 2012
     Passed: 2006
    Skipped: 6
 Total time: 38.6044 Seconds
```

The MFTLib assembly summary, distinct from the namespace totals above:

```text
| MFTLib               | 99.85% | 97.82% | 99.92% |
```

The runtime smoke exercised the gated stop/commit/drain/restart interleavings,
recovery ticket publication, lost catch-up, and real in-process broker scan/watch
overlap. Both defects reproduced before their production fixes. The final
read-only review found no remaining findings. XML remarks and AGENTS.md describe
fault retention and commit-time recovery cleanup; README and the other lifecycle
documents still match. No separate live-volume smoke or CI run was performed.

## Commands

```powershell
dotnet test MFTLib.Tests\MFTLib.Tests.csproj -c Release -p:Platform=x64 --filter "FullyQualifiedName~FileIndexWatchRescan|FullyQualifiedName~FileIndexWatchRecovery|FullyQualifiedName~FileIndexCatchUpLossTests|FullyQualifiedName~FileIndexWatchPumpTests|FullyQualifiedName~BrokerFileIndexRescanTests" --logger "console;verbosity=normal"
dotnet test MFTLib.Tests\MFTLib.Tests.csproj -c Release -p:Platform=x64 --filter FullyQualifiedName~FileIndexWatchRescanFaultHandoffTests --logger "console;verbosity=normal"
pwsh -NoProfile -File scripts\run-coverage.ps1 -NonInteractive
aislop ci .
git diff --check
```
