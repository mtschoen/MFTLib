# Linux W4 verification report

Status: GREEN. Commit 9725d0b8a246cd56b9a4cefa3e17fd1e5c024d65 (branch impl/265-per-drive-channels), host llamabox, clone ~/scratch/mftlib-265-w3.

## Commands run
1. ssh llamabox 'cd ~/scratch/mftlib-265-w3 && git fetch origin impl/265-per-drive-channels && git checkout --detach 9725d0b... && git clean -ffxd && git status --short; git rev-parse HEAD'
   - HEAD is now at 9725d0b Host-side scan steps helper is ScriptedScanSteps; rev-parse matched the target.
2. nohup ./init.sh --build > ~/scratch/mftlib-265-w3-build.log (exit file: 0)
   - "Build succeeded. 1 Warning(s) 0 Error(s)" (sourcelink warning only); prerequisites missing: aislop, gcovr (not needed).
3. nohup scripts/coverage-linux.sh > ~/scratch/mftlib-265-w3-coverage.log (exit file: 0)
4. Follow-up targeted run for per-class results:
   dotnet test MFTLib.Tests/MFTLib.Tests.csproj -c Debug -p:Platform=x64 --filter "<the six class names>" --logger trx (results ~/scratch/w4.trx)

## Results
- Build exit code: 0. Coverage exit code: 0.
- Native smoke tests: "=== 20 passed, 0 failed ===".
- Managed: Total 1488, Passed 1405, Failed 0, Skipped 83 (Windows-only tests etc.).
- Line coverage: managed 93.45% (7800/8346), branch 91.14%; native 74.5% (1032/1385).
- Failed tests: none.

## New test classes of this plan (none are excluded by the coverage-linux.sh filter)
Targeted run: Passed 64, Failed 0, Skipped 1, Total 65.
- BlockFileRangedFlushTests (MFTLib.Tests.Index): 12 results, all passed. It RAN on Linux (the msync path in BlockFile.Flush executed) and passed.
- JournalBrokerHostLivenessTests: 14 results, 13 passed, 1 skipped:
  BlockFlush_EachRange_RestartsScanPipeProgressClock -> Assert.Inconclusive "Named block sections require Windows." (JournalBrokerHostLivenessTests.Progress.cs line 119). Expected platform skip, not a failure.
- BrokerProcessLivenessTests: 12 results, all passed.
- FileIndexCatchUpLossTests: 16 results, all passed.
- FileIndexConcurrentRescanTests: 8 results, all passed.
- FileIndexDisposalOrderTests: 3 results, all passed.
