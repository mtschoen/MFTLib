# Linux verification v1 - commit 38fbca32307ec11f0792c2665583542be04da1bd

Status: GREEN

Host llamabox, scratch clone ~/scratch/mftlib-265-w3 (reused, git clean applied there only).

## Commands (all via ssh llamabox)
1. mkdir -p ~/scratch; clone if missing (clone existed); cd ~/scratch/mftlib-265-w3 && git fetch origin impl/265-per-drive-channels && git checkout --detach 38fbca32... && git clean -ffxd && git status --short
   - HEAD is now at 38fbca3 "FileIndex enumerates the caller's drive list once, pinned by a test"
   - git rev-parse HEAD = 38fbca32307ec11f0792c2665583542be04da1bd (match)
   - status after clean: empty
2. nohup ./init.sh --build > ~/scratch/mftlib-265-w3-build.log  -> EXIT=0
   - Only notes: missing optional prerequisites aislop and gcovr (gcovr reported as native coverage reports only, but coverage script produced native report anyway)
3. nohup scripts/coverage-linux.sh > ~/scratch/mftlib-265-w3-coverage.log -> EXIT=0
4. Extra read-only check (no rebuild): dotnet test MFTLib.Tests/MFTLib.Tests.csproj --no-build --filter "<12 plan classes>" --logger trx;LogFileName=~/scratch/mftlib-265-w3-newclasses.trx
   - "Passed! - Failed: 0, Passed: 162, Skipped: 1, Total: 163"

## Results
- Exit codes: init.sh --build = 0; coverage-linux.sh = 0.
- Native tests: "=== 20 passed, 0 failed ===".
- Managed (full coverage run): "Passed! - Failed: 0, Passed: 1585, Skipped: 84, Total: 1669, Duration: 35 s".
- Managed line coverage: 94.71% (8393/8861); branches 92.86% (2252/2425). MFTLib 95.11% line, MFTLibTestExtensions 79.82% line.
- Native coverage (gcc): lines 74.7% (1034/1385), branches 49.2%.
- Failed tests: none.
- Skipped 84: Windows-only / real-volume tests (SupportedOSPlatform windows, or real NTFS volume required).

## New plan test classes (none excluded by the coverage-linux.sh filter; the full log prints only skipped tests, so per-class counts come from the TRX of the extra filtered run)
| Class | Tests | Result |
| --- | --- | --- |
| BlockFileRangedFlushTests | 12 | all passed |
| JournalBrokerHostLivenessTests | 16 | 15 passed, 1 skipped (BlockFlush_EachRange_RestartsScanPipeProgressClock, [SupportedOSPlatform("windows")], JournalBrokerHostLivenessTests.Progress.cs line 114) |
| BrokerProcessLivenessTests | 12 | all passed |
| FileIndexCatchUpLossTests | 18 | all passed |
| FileIndexConcurrentRescanTests | 8 | all passed |
| FileIndexDisposalOrderTests | 3 | all passed |
| FileIndexCallbackReentrancyTests | 21 | all passed |
| FileIndexWatchRecoveryTests | 26 | all passed |
| FileIndexConcurrentOpenTests | 14 | all passed |
| FileIndexBatchedOperationTests | 15 | all passed |
| BrokerCrossDriveLivenessTests | 8 | all passed |
| BrokerIndexWatchSourceTests | 10 | all passed |
Total 163 = 162 passed + 1 skipped.

## msync path
BlockFileRangedFlushTests ran on Linux: 12 of 12 tests passed (not excluded, none skipped). This is the first Linux execution of BlockFile.Flush ranged path.

Logs on llamabox: ~/scratch/mftlib-265-w3-build.log, ~/scratch/mftlib-265-w3-coverage.log, ~/scratch/mftlib-265-w3-newclasses.trx.
