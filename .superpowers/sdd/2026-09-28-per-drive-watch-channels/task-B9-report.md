# Task B9 report: concurrent open

Status: DONE_WITH_CONCERNS (RED evidence is by scratch mutation for every new test, per W40-R1; two of the
ten new tests pass against the B5 base by design, see below).

Worktree `C:\Users\mtsch\MFTLib-worktrees\265-B9`, branch `task/265-B9`, base
`9725d0b8a246cd56b9a4cefa3e17fd1e5c024d65` (verified). Commit `d20d214` "FileIndex opens its drives
concurrently and reports each as it settles".

## What was implemented

- `IndexDriveOpened.Ordinal` replaced by `SettledCount` (1-based, "this drive was the n-th to settle").
- `FileIndex.OpenAsync` -> `SettleDrivesAsync` (FileIndex.Scanning.cs): records `_driveConfigurations`
  up front, starts one `Task.Run` settle task per drive, returns `Task.WhenAll`. `WhenAll` waits for every
  task, so a failure or cancellation is thrown only after all drives finished; `OpenAsync`'s existing catch
  then runs `ReleaseUnpublishedBlocks` over every adopted block (no task can still be producing one).
  The first failure in options order is thrown; cancellation surfaces as `OperationCanceledException`.
- `SettleDriveAsync` reports `OpenProgress` synchronously on its own thread with
  `Interlocked.Increment(ref _settledDriveCount)`. `AddDriveWithProgressAsync`, `ReportSettledDrive*` deleted.
- `DescribeSettledDrive` now finds the drive by letter (the old "last block / last blockless status"
  shortcut assumed sequential settles).
- `EnumerationWalkLimit` (new, internal): process-wide `SemaphoreSlim(Environment.ProcessorCount)`;
  `ProduceDriveBlockAsync` takes a lease before the enumeration walk (covers open and rescan). Test seams:
  `WalkQueuedForTest` (fires when a walk must wait) and `OverrideSizeForTestAsync(int)` (holds back slots).
- Each MFT scan at open goes through B5's `ScanOpenedDriveAsync` loop (B5-Q1), no thread count passed.
- Docs: `FileIndexOptions.OpenProgress` remarks, `IndexDriveOpened` remarks, README paragraph,
  `docs/broker-integration.md` sentence (they named "configured order" and "ordinal").

Deviation from the brief: `AddDriveAsync` still returns `Task` and adopts under `_stateLock` itself (B5
already publishes the `PendingDriveResult` there and assigns the ordinal at adoption), rather than
returning a `PendingDriveResult` for `OpenAsync` to publish. Behavior matches the brief (ordinal assigned at
settle under `_stateLock`); no lock-order change: only `_stateLock`, never held across an await, no gate.

## TDD / RED evidence (W40-R1: scratch mutations, all reverted; tests were written before the mutations were run)

Common build: `dotnet build MFTLib.Tests\MFTLib.Tests.csproj -c Release -p:Platform=x64` -> 0 Error(s).

Mutation A (uncommitted): `SettleDrivesAsync` settles sequentially
(`await SettleDriveAsync(...)` instead of `settling.Add(Task.Run(...))`):
```
dotnet test MFTLib.Tests\MFTLib.Tests.csproj -c Release -p:Platform=x64 --no-build --filter "FullyQualifiedName~FileIndexConcurrentOpenTests"
  Failed Open_TwoColdDrives_BothProducersInsideAtOnce [10 s]            System.TimeoutException: The operation has timed out.
  Failed Open_ProgressReportsInSettleOrder_WithSettledCount [10 s]      System.TimeoutException: No further OpenProgress report arrived.
  Failed Open_ProgressReportedFromSettlingThread [10 s]                 System.TimeoutException: No further OpenProgress report arrived.
  Failed Open_EnumerationWalks_NeverExceedTheWalkLimit [10 s]           System.TimeoutException: The operation has timed out.
  Failed Open_OneDriveProducerFails_OtherSucceeds_EachStatusOwn [10 s]  System.TimeoutException: The operation has timed out.
  Failed Open_CancelledWhileGated_SettlesAllThenReleasesAndThrows [10 s] System.TimeoutException: The operation has timed out.
  Failed Open_DrivesStatusOrderFollowsOptions [10 s]                    System.TimeoutException: The operation has timed out.
  Failed Open_WarmAndColdMix_WarmDoesNotWaitForCold [10 s]              System.TimeoutException: No further OpenProgress report arrived.
Failed!  - Failed:     8, Passed:     2, Skipped:     0, Total:    10
```
(The 2 that pass are the two `Open_CatchUpLost*` cases; see mutation C.)

Mutation B (uncommitted, concurrency restored): delete the `using var walkLease = await
EnumerationWalkLimit.EnterAsync(...)` line in `ProduceDriveBlockAsync`:
```
dotnet test MFTLib.Tests\MFTLib.Tests.csproj -c Release -p:Platform=x64 --no-build --filter "FullyQualifiedName~Open_EnumerationWalks_NeverExceedTheWalkLimit"
  Failed Open_EnumerationWalks_NeverExceedTheWalkLimit [10 s]   System.TimeoutException: The operation has timed out.
Failed!  - Failed:     1, Passed:     0, Total:     1
```
(No walk ever queues, so the queued signal never arrives. Run against the first form of the limit class;
the final form only changes how the size override is held.)

Mutation C (uncommitted): `FileIndex.CatchUp.cs` `ScanOpenedDriveAsync`, `if (settled.CatchUpLoss is not { } catchUpLoss ||`
-> `... || adopted is not null ||` (no retry at open):
```
dotnet test ... --filter "FullyQualifiedName~Open_CatchUpLostTwiceThenHolds_DriveReadyAndReportedOnce|FullyQualifiedName~Open_CatchUpLostThreeTimes_DriveReadyUnresumableWithReport_OpenDoesNotThrow"
  Failed Open_CatchUpLostTwiceThenHolds_DriveReadyAndReportedOnce [52 ms]   Assert.AreEqual failed. Expected:<3>. Actual:<1>.
  Failed Open_CatchUpLostThreeTimes_DriveReadyUnresumableWithReport_OpenDoesNotThrow [9 ms]  Assert.AreEqual failed. Expected:<3>. Actual:<1>.
Failed!  - Failed:     2, Passed:     0, Total:     2
```
These two pass on the plain B5 base by design (B5-Q1: B5 already wired the sequential open through the loop);
they pin that the concurrent open keeps using it. `Open_ProgressReportedFromSettlingThread` first passed under
mutation A only because my `WaitForReportAsync` swallowed the `SemaphoreSlim.WaitAsync(TimeSpan)` false result;
fixed to throw, then it fails as listed.

GREEN: `--filter "FullyQualifiedName~FileIndexConcurrentOpenTests|FullyQualifiedName~FileIndexOpenProgressTests"`
-> `Passed! Failed: 0, Passed: 19`. `--filter "FullyQualifiedName~Index"` x3 -> `Passed! Failed: 0, Passed: 838, Skipped: 6` each.

## New tests (`FileIndexConcurrentOpenTests`, class-level `[DoNotParallelize]`; fixture `OpenScenario.cs`)

All ten brief names: `Open_TwoColdDrives_BothProducersInsideAtOnce`, `Open_ProgressReportsInSettleOrder_WithSettledCount`,
`Open_ProgressReportedFromSettlingThread`, `Open_CatchUpLostTwiceThenHolds_DriveReadyAndReportedOnce`,
`Open_CatchUpLostThreeTimes_DriveReadyUnresumableWithReport_OpenDoesNotThrow` (fake producers with `CatchUpLoss`,
no journal window, no clock: W5-2), `Open_EnumerationWalks_NeverExceedTheWalkLimit` (limit size 1; a walk parked
in the `FileIndexOptions.Progress` callback holds its slot; the second walk is proven queued through
`WalkQueuedForTest`, never by waiting), `Open_OneDriveProducerFails_OtherSucceeds_EachStatusOwn`,
`Open_CancelledWhileGated_SettlesAllThenReleasesAndThrows` (asserts zero producers active at the throw, the
warm drive's cache file remains and opens `FileShare.None`, i.e. no mapping left), `Open_DrivesStatusOrderFollowsOptions`,
`Open_WarmAndColdMix_WarmDoesNotWaitForCold` (cold drive configured first). Every await bounded by
`FakeIndexWatchSource.HangGuard`; no clock, no sleep; owned cache directories.

## Port accounting: `FileIndexOpenProgressTests` (base method -> result)

| Base method | Result |
|---|---|
| OpenAsync_CacheOnlyWarmStart_ReportsEveryDriveInConfiguredOrder | `OpenAsync_CacheOnlyWarmStart_ReportsEveryDriveOnceWithSettledCounts` (by letter, counts exactly 1..3) |
| OpenAsync_CacheOnlyDeclinedDrive_ReportsItsBlocklessState | same name, by letter |
| OpenAsync_ColdScan_ReportsAfterTheProducerCompletes | same name, `SettledCount` 1 |
| OpenAsync_OfflineDrive_ReportsOfflineWithNoBlock | same name, `SettledCount` 1 |
| OpenAsync_NullOpenProgress_OpensExactlyAsBefore | unchanged |
| RescanAsync_DoesNotReportOpenProgress | unchanged |
| OpenAsync_InterleavedDrives_ReportsSettledStateInOrder | `OpenAsync_InterleavedDrives_ReportsEachSettledStateOnce` (by letter; Drives-order asserts kept) |
| OpenAsync_AsynchronousHandledProducerFailure_ReportsFailedState | same name, `SettledCount` 1 |
| OpenAsync_ThrowingOpenProgressCallback_ReleasesUnpublishedBlocks | unchanged |

Report lists are now `ConcurrentQueue`. None dropped.

## Existing test changed beyond the port

`FileIndexWatchTests` (Initialize): the enumeration drive `E` shared `_treeRoot` with the warm MFT drive `T`, so
`Find(path)` resolved to whichever settled first (2 failures in the first Index run: `ApplyJournalEntries_DeleteMakes...`,
`ApplyJournalEntries_ConcurrentHeldHandleReader...`). `E` now has its own `_enumerationRoot`. Test-only.

## Verification

- `.\scripts\run-coverage.ps1 -NonInteractive` (final tree): Total 1765, Passed 1759, Skipped 6, Failed 0; line 97.7%,
  branch 95.3% (2291 of 2402); `MFTLib.Index.EnumerationWalkLimit` 100%, `MFTLib.Index.FileIndex` 98.6%.
- `aislop scan . -d`: `99 / 100 Healthy 0 errors 5 warnings` = the four baseline warnings
  (`NativeSeamIsolationFixtures.cs:73`, `:79`; `CachedBlockDeletionOutcome.cs:8`, `:10`) plus the ruled
  `JournalBrokerHost` 8-parameter warning. Findings I introduced along the way (AsyncFixer01/02, IDISP001,
  redundant `!`) were fixed, not suppressed.
- CRLF kept on every touched and new file; no non-ASCII added.

## Files changed

Production: `MFTLib/Index/FileIndex.cs`, `FileIndex.Scanning.cs`, `IndexDriveOpened.cs`, `FileIndexOptions.cs`,
`EnumerationWalkLimit.cs` (new). Docs: `README.md`, `docs/broker-integration.md` (both name the old ordinal contract; CHANGELOG
left to the docs task). Tests: `FileIndexConcurrentOpenTests.cs` (new), `OpenScenario.cs` (new),
`FileIndexOpenProgressTests.cs`, `FileIndexWatchTests.cs`. No B6 file touched.

## Concerns and adjacent findings

- The two `Open_CatchUpLost*` tests are not RED on the B5 base (ruling B5-Q1); RED is by mutation C.
- Ordinal-order assumptions in other tests: the whole suite is green, but a test that mixes a warm and a cold drive over
  one shared tree and resolves a path is order-dependent now (`FileIndexWatchTests` was one). Consumers' tests that
  assumed "ordinal n = nth configured drive" need the same care (file-wizard `FileIndexHostTests` per plan section G).
- `AGENTS.md` still says nothing about concurrent open or `SettledCount`; left for the docs tasks (D1-D3), as is CHANGELOG.

## Primary checkout

`git -C C:\Users\mtsch\MFTLib status --short`: (empty output)

## Fix round 1

Commit: "FileIndex open settles drives in one order and a cancelled open always reports its cancellation" (on d20d214).

1. **Settle order linearized.** `ReportSettledDrive` (FileIndex.Scanning.cs) takes `_settleReportLock` (new `Lock`, FileIndex.cs), increments the count, reads the settled status and runs the callback inside it: reports never overlap and arrive in `SettledCount` order. Docs (`FileIndexOptions.OpenProgress`, README) now say so; block ordinals follow adoption order, not the count (the count is claimed when the drive's report starts, after its final adoption). Test seam `FileIndex.SettleReportContendedForTest` (static). RED (before the fix), literal command:
   `dotnet test MFTLib.Tests\MFTLib.Tests.csproj -c Release -p:Platform=x64 --no-build --filter "FullyQualifiedName~Open_ProgressReports_NeverOverlap_AndFollowSettledCountOrder|FullyQualifiedName~Open_CancelledWhileAnotherDriveSettleFaults_ThrowsCancellation"`
   -> `Failed Open_ProgressReports_NeverOverlap_AndFollowSettledCountOrder [42 ms]` / `Assert.IsFalse failed. the second drive's report ran while the first drive's report was still in progress`; `Failed!  - Failed: 2, Passed: 0, Total: 2`.
2. **Cancellation precedence.** `SettleDrivesAsync` catches any `WhenAll` failure when the token is cancelled and throws `OperationCanceledException(token)`. Same RED run: `Failed Open_CancelledWhileAnotherDriveSettleFaults_ThrowsCancellation [15 ms]` / `System.InvalidOperationException: T's progress handler failed` (the fault masked the cancellation). Test later refactored into a `CancelOnFirstReport` helper for aislop (same behavior); green after the fix.
3. **Exact RED commands for the two lost-catch-up tests** (mutation `|| adopted is not null ||` in `FileIndex.CatchUp.cs` `ScanOpenedDriveAsync`, reverted):
   `dotnet test MFTLib.Tests\MFTLib.Tests.csproj -c Release -p:Platform=x64 --no-build --filter "FullyQualifiedName~Open_CatchUpLostTwiceThenHolds_DriveReadyAndReportedOnce"` -> `Failed ... [48 ms]` / `Assert.AreEqual failed. Expected:<3>. Actual:<1>.` / `Failed: 1`.
   `dotnet test MFTLib.Tests\MFTLib.Tests.csproj -c Release -p:Platform=x64 --no-build --filter "FullyQualifiedName~Open_CatchUpLostThreeTimes_DriveReadyUnresumableWithReport_OpenDoesNotThrow"` -> `Failed ... [45 ms]` / `Assert.AreEqual failed. Expected:<3>. Actual:<1>.` / `Failed: 1`.
4. **Bounded awaits.** Every `OpenAsync`, `StartWatchingAsync` and `OverrideSizeForTestAsync` await in the class now ends in `.WaitAsync(HangGuard)`. Minors also done: the walk test's cleanup disposes a successful open (`DisposeIfOpenedAsync`); README paragraph split.

Verification (after the last edit): Index filter `Passed! Failed: 0, Passed: 840, Skipped: 6`; `run-coverage.ps1 -NonInteractive`: Total tests 1767, Passed 1761, Skipped 6, Failed 0 (line 97.7%, branch 95.3%); `aislop scan . -d`: 99/100, 5 warnings (4 baseline + ruled JournalBrokerHost). Primary checkout `git status --short`: empty.

## Fix round 2

Commit: "An opening drive claims its settle count when it is adopted and reports without a lock" (ruling B9-Q1).

- `_settleReportLock`, the serialization and `SettleReportContendedForTest` deleted. `SettledCount` is claimed by `ClaimSettledCountLocked` under `_stateLock` in the section that records the drive's final open state: `AdoptOpenedDrive` (when the catch-up held or the limit was reached), `RecordOfflineDrive`, `RecordFailedDrive`, and `RecordRescanProducerFailure(..., endsOpenSettle: true)` for a retry that produced no block. `SettleDriveAsync` then reads the count and status under `_stateLock` and calls `OpenProgress` with no lock held. Unused `DescribeSettledDrive` deleted. Docs (`FileIndexOptions.OpenProgress`, `IndexDriveOpened`, README) say callbacks may overlap and arrive out of order and `SettledCount` gives the order.
- Tests: `Open_ProgressReports_NeverOverlap_AndFollowSettledCountOrder` replaced by `Open_SettledCountFollowsAdoption_WhenALaterDrivesCallbackRunsFirst` (T adopted first with its callback held, U's callback runs meanwhile; T=1, U=2, ordinal T<U) and `Open_CallbackBlockedUntilTheOtherDrivesCallbackRuns_DoesNotDeadlock`. RED (against the round-1 lock), literal command:
  `dotnet test MFTLib.Tests\MFTLib.Tests.csproj -c Release -p:Platform=x64 --no-build --filter "FullyQualifiedName~Open_SettledCountFollowsAdoption_WhenALaterDrivesCallbackRunsFirst|FullyQualifiedName~Open_CallbackBlockedUntilTheOtherDrivesCallbackRuns_DoesNotDeadlock"`
  -> both `Failed [10 s]` / `System.TimeoutException: The operation has timed out.` / `Failed! - Failed: 2, Passed: 0, Total: 2`. GREEN after the change: `Passed: 22` for the `FileIndexConcurrentOpenTests|FileIndexOpenProgressTests` filter.
- Finding 4: `DisposeIfOpenedAsync` now bounds `DisposeAsync().AsTask().WaitAsync(HangGuard)`. Re-sweep: remaining un-`WaitAsync` awaits are `scenario.WaitForReportAsync()` (bounded inside), a local `File.WriteAllTextAsync`, and the implicit `DisposeAsync` of `await using` after a completed open.
- Verification after the last edit: Index filter `Passed: 841, Skipped: 6, Failed: 0`; `run-coverage.ps1 -NonInteractive` Total tests 1768, Passed 1762, Skipped 6, Failed 0 (line 97.7%, branch 95.3%); `aislop scan . -d` 99/100, 5 warnings (4 baseline + ruled JournalBrokerHost). Primary checkout clean.

## Fix round 3

Commit: "A cancelled drive settles nothing and open progress docs say which drives report" (ruling B9-Q2).

1. Docs (`FileIndexOptions.OpenProgress`, `IndexDriveOpened`, README, docs/broker-integration.md): a report for each drive that settles; a cancelled drive reports nothing; a cancelled or failed open may have reported only some drives. Code unchanged (a cancelled settle claims no count and reports nothing already). `Open_CancelledWhileGated_SettlesAllThenReleasesAndThrows` now also asserts the only report is the warm drive's. New `Open_CancelledAfterALostCatchUpAdoption_ReleasesTheNonterminalBlockAndReportsNothing` (fixture `OpenScenario.LetProductionsThrough`: T's first production loses its catch-up and is adopted, the retry parks, the open is cancelled): no report, no producer left, only the restored canonical file remains and it opens `FileShare.None`. RED by scratch mutation (removed `driveBlock.Block.Dispose()` in `ReleaseUnpublishedBlocks`, reverted):
   `dotnet test MFTLib.Tests\MFTLib.Tests.csproj -c Release -p:Platform=x64 --no-build --filter "FullyQualifiedName~Open_CancelledAfterALostCatchUpAdoption_ReleasesTheNonterminalBlockAndReportsNothing"` -> `Failed ... [47 ms]` / `System.IO.IOException: The process cannot access the file '...T-00000054.mlix' because it is being used by another process.` / `Failed: 1`.
2. Every FileIndex `await using` in `FileIndexConcurrentOpenTests` (13) now goes through `BoundedIndex`, whose `DisposeAsync` is `Index.DisposeAsync().AsTask().WaitAsync(HangGuard)`; the cleanup helper was bounded in round 2. Sweep: remaining un-bounded awaits are `scenario.WaitForReportAsync()` (bounded inside) and a local `File.WriteAllTextAsync`.

Verification after the last edit: Index filter `Passed: 842, Skipped: 6, Failed: 0`; `run-coverage.ps1 -NonInteractive` Total tests 1769, Passed 1763, Skipped 6, Failed 0 (line 97.7%, branch 95.3%); aislop 99/100, 5 warnings (4 baseline + ruled JournalBrokerHost). Primary checkout clean.

## Fix round 4

Re-review r3 finding 4. Commit `faa5245` on task/265-B9 (parent 8f536bc), test-only, two lines:

- `FileIndexConcurrentOpenTests.Open_EnumerationWalks_NeverExceedTheWalkLimit`: `await File.WriteAllTextAsync(..., Token).WaitAsync(HangGuard, Token)`. (A synchronous `File.WriteAllText` was tried first; aislop flagged it as AsyncFixer "blocking call inside an async method", so the bounded async form was kept.)
- `OpenScenario.ProduceAsync` (helper, found in the inventory sweep): the held-producer wait `gate.WaitForReleaseAsync(cancellationToken)` was bounded only by the open's token and the test's release-in-finally; it is now `.WaitAsync(FakeIndexWatchSource.HangGuard, cancellationToken)`, so a gate that is never released faults that drive's producer instead of hanging the open. The token is forwarded to satisfy CA2016.

Await inventory after the change (every `await` in FileIndexConcurrentOpenTests.cs and OpenScenario.cs):

- Held-producer entry waits: `Task.WhenAll(heldT.Entered, heldU.Entered).WaitAsync(HangGuard)` (lines 39, 115, 421, 462, 521); `script.FirstDriveCallback.Entered.WaitAsync(HangGuard)` (117, 149); `script.SecondDriveReported.WaitAsync(HangGuard)` (119); `retry.Entered.WaitAsync(HangGuard)` (494).
- Open results: `await opening.WaitAsync(HangGuard)` or `FileIndex.OpenAsync(...).WaitAsync(HangGuard)` inside each `BoundedIndex` (47, 73, 99, 128, 157, 188, 216, 277, 430, 451, 532, 549, 571).
- Implicit `await using` disposals (same 13 sites): `BoundedIndex.DisposeAsync` = `Index.DisposeAsync().AsTask().WaitAsync(HangGuard)` (371).
- `ThrowsAsync` (173, 230, 466, 496): each lambda passed is `...WaitAsync(HangGuard)`; the helper's own `await action()` (FileIndexWatchRescanTests.cs:537) awaits only that bounded task.
- `index.StartWatchingAsync('T', Token).WaitAsync(HangGuard)` (203, and inside ThrowsAsync at 230).
- `scenario.WaitForReportAsync()` (62, 91, 428, 463, 522, 524, 559): bounded inside, `_reportSignal.WaitAsync(HangGuard)` with a `TimeoutException` on false (OpenScenario.cs:93).
- Enumeration-walk test: `File.WriteAllTextAsync(...).WaitAsync(HangGuard, Token)` (247, this round); `EnumerationWalkLimit.OverrideSizeForTestAsync(1).WaitAsync(HangGuard)` (262); `queued.Task.WaitAsync(HangGuard)` (268); `Task.WhenAny(...).WaitAsync(HangGuard)` (269); `gates[second].Entered.WaitAsync(HangGuard)` (274); `DisposeIfOpenedAsync(opening)` (293) whose two awaits are `opening.WaitAsync(HangGuard)` and `DisposeAsync().AsTask().WaitAsync(HangGuard)` (379).
- OpenScenario.ProduceAsync: `gate.WaitForReleaseAsync(cancellationToken).WaitAsync(HangGuard, cancellationToken)` (156, this round).
- Synchronous blocking waits (not awaits, listed for completeness): `TestGate.WaitForRelease` (`Wait(HangGuard)`, used by ReportScript and ParkedWalks) and `_secondDriveReported.Task.Wait(HangGuard)` (341), both bounded.

No unbounded await remains in either file.

Commands and outputs:

- `dotnet build MFTLib.Tests\MFTLib.Tests.csproj -c Release -p:Platform=x64` -> `0 Warning(s)`, `0 Error(s)` (dll timestamp after the source edits).
- `dotnet test MFTLib.Tests\MFTLib.Tests.csproj -c Release -p:Platform=x64 --no-build --filter "FullyQualifiedName~FileIndexConcurrentOpenTests"` -> `Passed!  - Failed:     0, Passed:    14, Skipped:     0, Total:    14, Duration: 258 ms - MFTLib.Tests.dll (net10.0)`.
- `aislop scan C:\Users\mtsch\MFTLib-worktrees\265-B9` -> `99 / 100  Healthy  0 errors · 5 warnings · 0 fixable`: exactly NativeSeamIsolationFixtures.cs:73 and :79, CachedBlockDeletionOutcome.cs:8 and :10, and JournalBrokerHost.cs:46 (8 params).
- `git -C C:\Users\mtsch\MFTLib status --short` -> (empty)
