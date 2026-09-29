### Task B9: Concurrent open

Owner ruling 13. `FileIndex.OpenAsync` settles every configured drive concurrently; the broker's allocator bounds the threads across those scans and every other scan, and `FileIndex` passes no thread count.

**Files:** Modify `MFTLib/Index/FileIndex.cs` (`OpenAsync`), `MFTLib/Index/FileIndex.Scanning.cs` (`AddDriveAsync` returns B5's `PendingDriveResult` instead of adding to `_driveBlocks`; `AddDriveWithProgressAsync` goes), `MFTLib/Index/IndexDriveOpened.cs`, `MFTLib/Index/FileIndexOptions.cs` (`OpenProgress` doc); rewrite `MFTLib.Tests/Index/FileIndexOpenProgressTests.cs`; create `MFTLib/Index/EnumerationWalkLimit.cs`, `MFTLib.Tests/Index/FileIndexConcurrentOpenTests.cs`.

```csharp
public sealed record IndexDriveOpened
{
    public required char DriveLetter { get; init; }
    public required int SettledCount { get; init; }   // this drive was the SettledCount-th to settle, 1-based; replaces Ordinal
    public required int Total { get; init; }
    public required BlockSource BlockSource { get; init; }
    public required DriveState State { get; init; }
}
```

Behavior: `OpenAsync` starts one settle task per configured drive, each with the cancellation token; each MFT scan goes through B5's scan-operation loop and passes no thread count (S3), so a cold drive that loses its catch-up rescans itself up to three times in a row; after three it settles `Ready` (its last block, unresumable, count 3, the `ScanCatchUp` report attached) and `OpenAsync` does not throw (L1); it reports `OpenProgress` once, when it settles. Enumeration walks (`FileIndex.Scanning.cs:157-163`) are admitted through a process-wide limit of one walk per processor (`MFTLib/Index/EnumerationWalkLimit.cs`, with an internal size seam), so the limit on scans holds wherever drives are scanned (spec 2.6.7). Each task, when it settles, publishes its `PendingDriveResult` under `_stateLock` (ordinal assigned then, R8) and reports `OpenProgress` synchronously on its own thread with the next `SettledCount` (an `Interlocked.Increment`). `OpenAsync` awaits every task before it publishes the snapshot or throws: if any task threw (cancellation included), it waits for the rest to settle, releases every block already adopted (`ReleaseUnpublishedBlocks`) and every block a still-settling task produced, then throws the first failure (`OperationCanceledException` when cancelled). Block ordinals follow settle order; `Drives` and `DriveStatus` keep `FileIndexOptions.Drives` order because they are computed from the options (`FileIndex.cs:104-121`).

- [ ] **Failing tests:** `Open_TwoColdDrives_BothProducersInsideAtOnce`; `Open_ProgressReportsInSettleOrder_WithSettledCount` (`U` settles before `T` by gates; reports are `U` 1 of 2 then `T` 2 of 2); `Open_ProgressReportedFromSettlingThread` (the report arrives while the opening caller is still awaiting); `Open_CatchUpLostTwiceThenHolds_DriveReadyAndReportedOnce` (L1: script lost, lost, block; three producer calls; one `OpenProgress` for the drive; no block file left over); `Open_CatchUpLostThreeTimes_DriveReadyUnresumableWithReport_OpenDoesNotThrow` (L1: `Ready`, count 3, the report, the watch refused; the other drive `Ready`; synthetic journal windows, no clock); `Open_EnumerationWalks_NeverExceedTheWalkLimit` (size seam 1 with two enumeration drives: one walk at a time; `[DoNotParallelize]`, the limit is process-wide); `Open_OneDriveProducerFails_OtherSucceeds_EachStatusOwn` (R8); `Open_CancelledWhileGated_SettlesAllThenReleasesAndThrows` (no block left mapped; the cache files of warm-started drives remain); `Open_DrivesStatusOrderFollowsOptions`; `Open_WarmAndColdMix_WarmDoesNotWaitForCold`. Port `FileIndexOpenProgressTests` cases that asserted configured order onto settle order. See them fail; implement; verify with the `Index` filter, whole suite, `aislop scan .`; commit "FileIndex opens its drives concurrently and reports each as it settles".

**Gate:** green. **Depends on:** B5. **Parallel with:** B6, C6 (disjoint files: B6 does not touch `FileIndex.cs` or `FileIndex.Scanning.cs`).

