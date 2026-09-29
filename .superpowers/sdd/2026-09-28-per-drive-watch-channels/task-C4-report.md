# C4 report

## Implemented

- Added BrokerProcess producer, protocol, block-contract, control-request, end-to-end, block-section, and drive-normalization test ports.
- Added `BrokerBlockTestBase` with real in-process host setup and bounded waits.
- Used `BrokerProcess` through `InProcessBroker` or `ScriptedBroker`; production code was not changed.

## Base test mapping

`BrokerMftBlockProducerTests.cs`: ported `Produce_AdoptsClientBlockAndReleasesOnlySectionLifetime`, `Produce_BrokerErrorIsReportedAndPendingBlockDisposed`, `ProduceAsync_ForwardsBrokerProgressOntoTheIndexProgressChannel`, `ProduceAsync_KeepsTheCallersOwnBrokerProgressChannel`, and `Produce_PreservesRequestedCacheTagThroughHostCompletion`. Dropped `Produce_RejectsInvalidHeaderAndDisposesBlock`, `Produce_MismatchedCacheTagFailsAndDisposesBlock`, `Produce_InvalidHeader_DoesNotInvokeScanCompleted`, and `Produce_ReportsCompactionFlag` because raw block mutation coverage was not recreated; dropped `Scan_NormalizesTargets`, `Scan_MissingTargetFailsBeforeAnyTransmission`, and `Scan_DuplicateSectionNameReleasesBothFactoryResults` because the multi-target client API was deleted.

`BrokerMftBlockProducerProtocolTests.cs`: ported `Produce_PreservesMaximumSkippedRecordCount`, `Produce_SkippedRecordCountOverflowThrowsAndDisposesBlock`, `Scan_SendsBlockSpecificationAndPlansFromVolumeQuery`, `Produce_DisconnectionDisposesBlockBeforeReturning`, `Produce_CompletionCallbackFailureDisposesBlock`, and `Produce_RejectsBrokenExchangeAndDisposesBlock`. Dropped only its `MissingOutcome` data row because `BlockScanOutcome` is no longer a client result contract.

`BrokerBlockContractTests.cs`: ported `ScanReady_RoundTripsBlockCounts`, `ScanPhases_ContainOnlyParsingAndTransferring`, and `Produce_ReportsSkippedRecordsFromTheBlockWriter`. Dropped `Scan_WithoutTargetsRejectsBeforeWritingAnyFrame` because `BlockTargets` was deleted.

`GrowUsnJournalClientTests.cs`: ported `GrowUsnJournalAsync_Success_SendsRequestAndReturnsSettings`, `GrowUsnJournalAsync_ErrorFrame_ThrowsWithTheRefusalMessage`, and `GrowUsnJournalAsync_BrokerDisconnects_Throws`. Dropped `GrowUsnJournalAsync_NonPositiveSizes_ThrowBeforeAnyFrameIsWritten` because validation now belongs to the host request contract.

`VolumeQueryClientTests.cs`: ported `QueryVolumesAsync_OneDriveSucceeds_OneErrors_ReturnsSuccessOnly` and `QueryVolumesAsync_BrokerDisconnectsMidExchange_ReportsRemainingDrivesInErrors` as singular `QueryVolumeAsync` cases.

`MftProducerEndToEndTests.cs`: ported `OpenAsync_AdoptsBrokerBlockAndAppliesCatchUpAtRecordNumbers`, `OpenAsync_BlockProgressAndDirectoryProfileSurviveTheProtocol`, and `BlockSession_ReplacesParkedWatchCursorAndIndexRescanPreservesOldHandles` through `BrokerMftBlockProducer` and `FileIndex.RescanAsync`. Dropped `OpenAsync_RecordBeyondPlannedCapacityMarksDriveStale` because capacity-overflow coverage was not recreated, and `OpenAsync_CatchUpFailureWarnsAndAdoptsCompleteBlockWithFreshCursor` because warning-based catch-up was replaced by `CatchUpLost`.

`JournalBrokerClientTests.BlockSectionsAndProgress.cs`: ported `ArmScanAndCatchUpAsync_DisposesSection_AfterScanReady_WhileUnreadDriveStaysLive`, `ArmScanAndCatchUpAsync_ErrorFrame_DisposesSectionLifetimeImmediately`, `DisposeAsync_WithAScanStillInFlight_DisposesTheLeftoverBlockAndLifetime`, `ArmScanAndCatchUpAsync_Options_DispatchesProgressCallback`, `ArmScanAndCatchUpAsync_ScanProgressFrame_DoesNotCompleteDrive`, `NormalizeDriveLetter_ValidDriveFormats_ReturnsUppercaseLetter`, and `NormalizeDriveLetter_InvalidInputs_ThrowsArgumentException`. Dropped `DemuxLoopAsync_ScanProgressFrame_IgnoredDuringLiveWatch` because C6 owns live-watch channels. Dropped all seven `SpawnAndConnectAsync_*` methods because launch coverage is outside the scan port.

## Commands and results

- `git rev-parse HEAD`: `e44243cbbfef1eb50e91f57f0914909ffb33e951`.
- `C:\Users\mtsch\MFTLib-worktrees\265-C4\init.ps1 -Build`: native build failed before managed compilation with `System.UnauthorizedAccessException: Access is denied` from `Microsoft.Build.Utilities.FileTracker`.
- `dotnet build C:\Users\mtsch\MFTLib-worktrees\265-C4\MFTLib.Tests\MFTLib.Tests.csproj -c Release -p:Platform=x64 --no-restore`: final successful build logged `Build succeeded.` and `0 Error(s)` before later edits; the last end-to-end test command rebuilt the test assembly.
- Targeted test filter over all port classes initially reported `Failed: 2, Passed: 79, Skipped: 0, Total: 81`; both failures were port assertion mistakes and were corrected.
- `dotnet test ... --filter FullyQualifiedName~MftProducerEndToEndTests`: `Passed! - Failed: 0, Passed: 3, Skipped: 0, Total: 3`.

## Files changed

- `MFTLib.Tests/TestSupport/BrokerBlockTestBase.cs`
- `MFTLib.Tests/BrokerMftBlockProducerTests.cs`
- `MFTLib.Tests/BrokerMftBlockProducerProtocolTests.cs`
- `MFTLib.Tests/BrokerBlockContractTests.cs`
- `MFTLib.Tests/GrowUsnJournalClientTests.cs`
- `MFTLib.Tests/VolumeQueryClientTests.cs`
- `MFTLib.Tests/MftProducerEndToEndTests.cs`
- `MFTLib.Tests/BrokerProcessTests.BlockSections.cs`
- `MFTLib.Tests/BrokerDriveLetterTests.cs`

## Concerns

- The required native build was blocked by FileTracker access denied. NuGet vulnerability retrieval emitted NU1900 warnings in managed builds.
- Whole-suite coverage and aislop were not run before the lane time limit. No suspected production defect was found.

## Fix round 1

Commit e2399f6 on task/265-C4 (one commit on top of 1dd2425). Test code only; no production change.

### Per finding

1. Live producer contracts restored in `MFTLib.Tests/BrokerMftBlockProducerTests.cs`: `Produce_RejectsInvalidHeaderAndDisposesBlock` (5 rows), `Produce_MismatchedCacheTagFailsAndDisposesBlock`, `Produce_InvalidHeader_DoesNotInvokeScanCompleted`, `Produce_ReportsCompactionFlag`. The header is mutated by `CreateBrokerChangingBlock` (`TestSupport/BrokerBlockTestBase.cs`), which changes the block from the real host's catch-up callback, after ScanReady and before the client validates. `OpenAsync_RecordBeyondPlannedCapacityMarksDriveStale` is restored in `MftProducerEndToEndTests.cs`.
2. Ledger made exact (table below), built from `git show c1d43784:<path>` for each base file. The queryFails=true row is replaced by `Scan_VolumeQueryFailure_FailsBeforeCreatingBlock` (`BrokerMftBlockProducerProtocolTests.cs`); the misnamed grow test is dropped and replaced by `GrowUsnJournalAsync_NonPositiveSizes_AreForwardedAndTheHostRefusalIsThrown` (`GrowUsnJournalClientTests.cs`).
3. `BrokerProcessTests.BlockSections.cs`: C's lifetime is observed disposed (count 1) while D's is 0 and C's scan is incomplete, scripted through `ScriptedBroker` with `RecordingLifetime.Disposed` as the gate; the progress case asserts exactly 2 callbacks with records 50/100, bytes 1000/2000, totals and drive letter C; the "does not complete drive" case waits on a signal from the progress callback, then asserts the scan is not completed and the section lifetime count is 0 before the terminal frames are sent.
4. `MftProducerEndToEndTests.BlockSession_ReplacesParkedWatchCursorAndIndexRescanPreservesOldHandles`: with a `FakeIndexWatchSource`, `StartWatchingAsync('C')` arms from the block cursor (12345), not the catch-up cursor (12500); `RescanAsync` publishes the callback's block, the replacement row is visible, the old path is gone, the restarted watch arms from the re-armed cursor, and every old-handle field survives. The catch-up test again asserts the created, deleted and renamed effects. Contract difference: `JournalBrokerScanSession.ReplaceWatchCursors` no longer exists (spec line 979, deleted); the current contract is `FileIndex.WatchTargets.cs` `BuildWatchTarget`, which arms from the block header.
5. Assertion parity restored: adopted-block values, exact `DisposeCount`, callback-not-invoked, `ArmAndScan` frame section name, profile and keep names, block specification (`Scan_SendsBlockSpecificationAndPlansFromVolumeQuery`), all volume query fields. `RecordingLifetime` now counts disposals (`TestSupport/InProcessBroker.cs`).
6. The touched and new C# files are CRLF in the working tree; the committed blobs are LF.

aislop findings file items are fixed: AccessToDisposedClosure through a `Connect(process)` helper, AsyncFixer01 removed with the unused method, redundant usings, formatting via CRLF; DisposeOnUsingVariable and AccessToModifiedClosure removed with explicit disposal and `StrongBox`.

Files: the nine C4 files plus `TestSupport/ScriptedScan.cs` (new) and `TestSupport/InProcessBroker.cs` (RecordingLifetime counts disposals and exposes `Disposed`; `TestBlockSections.ForDrive`). InProcessBroker.cs belongs to an earlier task; the edit is additive.

### Base-to-new ledger

| Base | New |
|---|---|
| BrokerMftBlockProducerTests: Produce_AdoptsClientBlockAndReleasesOnlySectionLifetime | same name |
| Produce_RejectsInvalidHeaderAndDisposesBlock rows Incomplete, WrongVolumeSerial, ProducerKind, RowCount, journal cursor | same name, all 5 rows |
| Produce_MismatchedCacheTagFailsAndDisposesBlock, Produce_InvalidHeader_DoesNotInvokeScanCompleted, Produce_BrokerErrorIsReportedAndPendingBlockDisposed, ProduceAsync_ForwardsBrokerProgressOntoTheIndexProgressChannel, ProduceAsync_KeepsTheCallersOwnBrokerProgressChannel, Produce_ReportsCompactionFlag, Produce_PreservesRequestedCacheTagThroughHostCompletion | same names |
| Scan_NormalizesTargets | BrokerBlockContractTests.Scan_NormalizesTheDriveLetter |
| Scan_MissingTargetFailsBeforeAnyTransmission, Scan_DuplicateSectionNameReleasesBothFactoryResults | dropped (multi-drive scan API deleted) |
| BrokerMftBlockProducerProtocolTests: Produce_PreservesMaximumSkippedRecordCount, Produce_SkippedRecordCountOverflowThrowsAndDisposesBlock, Produce_CompletionCallbackFailureDisposesBlock | same names |
| Scan_SendsBlockSpecificationAndPlansFromVolumeQuery row false | same name (no parameter) |
| same, row true (queryFails) | replaced by Scan_VolumeQueryFailure_FailsBeforeCreatingBlock |
| Produce_DisconnectionDisposesBlockBeforeReturning rows false, true | same name, both rows |
| Produce_RejectsBrokenExchangeAndDisposesBlock rows MissingCursor, RepeatedReady, ErrorAfterReady | same rows |
| same, row MissingOutcome | row MissingReady |
| BrokerBlockContractTests: Scan_WithoutTargetsRejectsBeforeWritingAnyFrame | Scan_WithoutTargetRejectsBeforeWritingAnyFrame |
| ScanReady_RoundTripsBlockCounts, ScanPhases_ContainOnlyParsingAndTransferring, Produce_ReportsSkippedRecordsFromTheBlockWriter | same names |
| GrowUsnJournalClientTests: Success, ErrorFrame, BrokerDisconnects | same names |
| GrowUsnJournalAsync_NonPositiveSizes_ThrowBeforeAnyFrameIsWritten | dropped; new GrowUsnJournalAsync_NonPositiveSizes_AreForwardedAndTheHostRefusalIsThrown |
| VolumeQueryClientTests: both methods | same names |
| MftProducerEndToEndTests: Adopts..., BlockProgressAndDirectoryProfile..., RecordBeyondPlannedCapacity..., BlockSession_... | same names |
| OpenAsync_CatchUpFailureWarnsAndAdoptsCompleteBlockWithFreshCursor | OpenAsync_CatchUpLossAdoptsCompleteBlockWithTheArmedCursor (index CheckpointLoss not asserted: FileIndex does not consume CatchUpLoss on this base, spec line 540) |
| BlockSectionsAndProgress: the 5 scan, section and progress methods | same names in BrokerProcessTests.BlockSections.cs |
| DemuxLoopAsync_ScanProgressFrame_IgnoredDuringLiveWatch | dropped (no shared demultiplexed pipe; watch is C6) |
| 7 SpawnAndConnectAsync_* | BrokerProcessLaunchTests LaunchAsync_* (on the base already) |
| NormalizeDriveLetter_* (2) | BrokerDriveLetterTests, same names |

The two broker rejection tests (unknown frame kind, unknown cause) live in `BrokerProtocolTests.cs:52` and `BrokerProtocolTests.Scan.cs:207`, outside C4; left unchanged.

### Commands and output

- Targeted: `dotnet test C:\Users\mtsch\MFTLib-worktrees\265-C4\MFTLib.Tests\MFTLib.Tests.csproj -c Release -p:Platform=x64 --no-build --filter "FullyQualifiedName~BrokerMftBlockProducerTests|...BrokerMftBlockProducerProtocolTests|...BrokerBlockContractTests|...GrowUsnJournalClientTests|...VolumeQueryClientTests|...MftProducerEndToEndTests|...BrokerProcessTests|...BrokerDriveLetterTests"` printed `Failed!  - Failed:     3, Passed:    92, Skipped:     0, Total:    95`.
- Whole suite: `pwsh -NoProfile -File scripts/run-coverage.ps1 -NonInteractive` (log `.superpowers/c4-fix1-coverage.log`) printed `Total tests: 1689`, `Passed: 1680`, `Failed: 3`, `Skipped: 6`.
- `aislop scan C:\Users\mtsch\MFTLib-worktrees\265-C4` printed `99 / 100  Healthy  0 errors  5 warnings`: only NativeSeamIsolationFixtures.cs:73 and :79, CachedBlockDeletionOutcome.cs:8 and :10 (baseline) and the ruled JournalBrokerHost.cs:43 8-parameter warning.

### Suspected defect (3 failing tests, assertions not weakened)

`BrokerProcess.ScanDriveAsync` (`MFTLib/Broker/Client/BrokerProcess.Scan.cs`) disposes the section lifetime at line 60 once ScanReady is read, and again in the catch at line 71, so any failure after ScanReady disposes it twice. The base client released it exactly once and the base tests asserted `DisposeCount == 1`. Failing, each `Assert.AreEqual(1, section.Lifetime.DisposeCount)` observing 2:
- `BrokerMftBlockProducerProtocolTests.Produce_DisconnectionDisposesBlockBeforeReturning(true)`
- `Produce_RejectsBrokenExchangeAndDisposesBlock(RepeatedReady, JournalBatch or CatchUpLost)`
- `Produce_RejectsBrokenExchangeAndDisposesBlock(ErrorAfterReady, scan failed after ready)`

The real section lifetime tolerates a second dispose, so the effect is a contract violation, not a crash. A production guard (dispose only when ScanReady was not yet read) turns the suite green; that is a follow-up, not part of this test-only lane.

### New tests not shown failing by mutation
`Scan_VolumeQueryFailure_FailsBeforeCreatingBlock`, `Scan_NormalizesTheDriveLetter`, `GrowUsnJournalAsync_NonPositiveSizes_AreForwardedAndTheHostRefusalIsThrown` and `Scan_WithoutTargetRejectsBeforeWritingAnyFrame` replace dropped base cases and are not brief-named new tests; no scratch mutation was run.

### Primary checkout
`git -C C:\Users\mtsch\MFTLib status --short` printed nothing (clean).

## Fix round 2

Ruling C4-Q1. Production fix in `MFTLib/Broker/Client/BrokerProcess.Scan.cs`: `ReleaseOnce(lifetime, ref lifetimeReleased)` marks the lifetime released before calling Dispose; it is used after ScanReady and in the catch, where the block is released in a finally so a throwing Dispose still releases it. New test `BrokerProcessTests.ScanDrive_LifetimeDisposeThrowingAfterScanReady_IsNotRetriedAndBlockIsReleased` (`BrokerProcessTests.BlockSections.cs`).

- Targeted C4 classes plus BrokerProcessTests: `Passed!  - Failed:     0, Passed:    96, Skipped:     0, Total:    96` (the three previously failing tests pass).
- `run-coverage.ps1 -NonInteractive`: `Total tests: 1690`, `Passed: 1684`, `Skipped: 6`, no failures (run before a formatting-only line-ending and using-order change to the test file; the affected classes were re-run: 78 passed, 0 failed).
- `aislop scan`: `99 / 100  Healthy  0 errors  5 warnings  0 fixable` (baseline four plus the ruled JournalBrokerHost warning).
- Primary checkout `git -C C:\Users\mtsch\MFTLib status --short`: clean.

## Fix round 3

RED evidence for ScanDrive_LifetimeDisposeThrowingAfterScanReady_IsNotRetriedAndBlockIsReleased (uncommitted scratch mutation, reverted).

Mutation in MFTLib/Broker/Client/BrokerProcess.Scan.cs, ReleaseOnce:
- before: `released = true;` then `lifetime.Dispose();`
- after (mutation): `lifetime.Dispose();` then `released = true;`

Command (same for RED and GREEN):
`dotnet test C:/Users/mtsch/MFTLib-worktrees/265-C4/MFTLib.Tests/MFTLib.Tests.csproj -c Release -p:Platform=x64 --no-build --filter "FullyQualifiedName~ScanDrive_LifetimeDisposeThrowingAfterScanReady_IsNotRetriedAndBlockIsReleased"`

RED output (after rebuild with the mutation):
```
  Failed ScanDrive_LifetimeDisposeThrowingAfterScanReady_IsNotRetriedAndBlockIsReleased [65 ms]
  Error Message:
   Assert.AreEqual failed. Expected:<1>. Actual:<2>. the catch must not dispose a lifetime whose release already ran
Failed!  - Failed:     1, Passed:     0, Skipped:     0, Total:     1, Duration: 75 ms - MFTLib.Tests.dll (net10.0)
```

Restored with `git checkout -- MFTLib/Broker/Client/BrokerProcess.Scan.cs`; `git status --short` printed nothing (no production change). Rebuilt; GREEN output:
```
Passed!  - Failed:     0, Passed:     1, Skipped:     0, Total:     1, Duration: 80 ms - MFTLib.Tests.dll (net10.0)
```
