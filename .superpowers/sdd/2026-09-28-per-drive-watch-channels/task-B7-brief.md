### Task B7: Batched entry points

**Files:** Create `MFTLib/Index/DriveOperationResult.cs`, `MFTLib/Index/FileIndex.Batched.cs`, `MFTLib.Tests/Index/FileIndexBatchedOperationTests.cs`; modify `FileIndex.Rescan.cs` (the single-drive rescan throws `InvalidOperationException` carrying `DriveStatus.MftProducerFailureMessage` when the producer returns no block, and throws the `JournalCatchUpLostException` after a lost-catch-up stop (B5)).

```csharp
public enum DriveOperationOutcome { Succeeded, Failed, NotApplicable }
public sealed record DriveOperationResult(char DriveLetter, DriveOperationOutcome Outcome, Exception? Failure);
public Task<IReadOnlyList<DriveOperationResult>> StartWatchingAsync(IReadOnlyList<char> driveLetters, CancellationToken cancellationToken);
public Task<IReadOnlyList<DriveOperationResult>> StartWatchingAsync(CancellationToken cancellationToken);
public Task<IReadOnlyList<DriveOperationResult>> StopWatchingAsync(IReadOnlyList<char> driveLetters, CancellationToken cancellationToken);
public Task<IReadOnlyList<DriveOperationResult>> StopWatchingAsync(CancellationToken cancellationToken);
public Task<IReadOnlyList<DriveOperationResult>> RescanAsync(IReadOnlyList<char> driveLetters, CancellationToken cancellationToken);
public Task<IReadOnlyList<DriveOperationResult>> RescanAsync(CancellationToken cancellationToken);
public Task<IReadOnlyList<DriveOperationResult>> WaitForCatchUpAsync(IReadOnlyList<char> driveLetters, CancellationToken cancellationToken);
public Task<IReadOnlyList<DriveOperationResult>> WaitForCatchUpAsync(CancellationToken cancellationToken);
```

Contract: spec section 3, "Batched-call contract". No entry point passes a thread count: the broker's allocator divides the processors among whatever scans are running (S3), so a batched rescan of four drives and four single-drive rescans behave alike. `NotApplicable`: start on a drive with no MFT-backed block; stop and catch-up on a drive that is not watching. Stop's `Failed` carries the fault that had ended the drive's watch. The batched catch-up wait's completion source is created with `RunContinuationsAsynchronously`, and its cancellation comes from a token registration, not `Task.WaitAsync` (spec 2.6.8); B7 adds `BatchedWait_SettledByPumpFault_ContinuationNotInline` (B1's pump-fault row with a batched wait naming X pending, using B1's hook).

Also: audit every existing test that expects a failed rescan to complete normally, since the single-drive rescan now throws. Candidates found by Grep for `RescanAsync` at the base commit: `Index/FileIndexBlockReleaseTests.cs`, `FileIndexBlockSourceTests.cs`, `FileIndexCacheTagTests.cs`, `FileIndexCheckpointLossLifetimeTests.cs`, `FileIndexDisposalRaceTests.cs`, `FileIndexDriveStatusTests.cs`, `FileIndexLifetimeTests.cs`, `FileIndexOpenProgressTests.cs`, `FileIndexOwnerLockTests.cs`, `FileIndexProducerSelectionTests.cs`, `FileIndexQueryTests.cs`, `FileIndexRescanCleanupTests.cs`, `FileIndexResilienceTests.cs`, `.DeleteLogging.cs`, `SnapshotSwapTests.cs`, `CacheDirectoryDeletionLiveOwnerTests.cs`. Each case that relied on a silent failure asserts the `InvalidOperationException` and its message.

- [ ] **Failing tests:** `BatchedStart_PartialFailure_ReportsPerDrive_DoesNotThrow` (spec 9: source throws for `U`, an enumeration-backed `V`; `T Succeeded`, `U Failed` with that exception, `V NotApplicable`; `T` watches); `BatchedStart_CancelledWhileGated_ThrowsOnlyAfterEverySettles_NoHandlePublished` (spec 9); `Batched_DuplicateLetter_ThrowsArgumentBeforeStarting`; `Batched_UnknownLetter_Throws`; `Batched_Null_Throws`; `Batched_Disposed_ThrowsObjectDisposed`; `NoListForm_CoversDrivesInOptionsOrder`; `BatchedStop_ReturnsFaultAsFailed`; `BatchedWait_ReturnsPerDrive`; `BatchedRescan_OneDriveStopsAfterThreeLostCatchUps_ReportsFailedWithMessage_OthersSucceed` (L1: `T`'s producer loses its catch-up three times and `U`'s holds; `T` is `Failed` with a `JournalCatchUpLostException` (`RecoveryStopped`, the journal size in its message), `U` is `Succeeded`; fake producers setting `CatchUpLoss`, no clock); `SingleRescan_CatchUpStop_ThrowsJournalCatchUpLostException` (L1); `SingleRescan_ProducerReturnsNoBlock_ThrowsWithFailureMessage`. See them fail; implement; verify with `NamespaceBoundaryTests`, whole suite, `aislop scan .`; commit "Batched FileIndex operations fan out per drive and return one result each".

**Gate:** green. **Depends on:** B6, B9, C6. **Parallel with:** C8.

