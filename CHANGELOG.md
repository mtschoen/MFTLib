# Changelog

## Unreleased

Everything since 0.2.0, the latest published release: the packed index and its MFT
producer ([MFTLib#131](https://gitea.fleet.sticktoitive.net/schoen/MFTLib/issues/131))
and the live watch bridge and consumer-gap closures tracked as
[MFTLib#132](https://gitea.fleet.sticktoitive.net/schoen/MFTLib/issues/132), including the
per-drive watch channels tracked by [MFTLib#265](https://gitea.fleet.sticktoitive.net/schoen/MFTLib/issues/265).

### Added

- `BrokerProcess` owns one elevated broker process, its control pipe, concurrent request-id-routed control operations, and independent per-operation drive channels. `LaunchAsync`, `QueryVolumeAsync`, `GrowUsnJournalAsync`, and `ScanDriveAsync` expose launch, volume sizing, journal growth, and one-drive scans; `HasEnded` and `Ended` report control-pipe loss, and `DisposeAsync` ends the process without surfacing a host fault.
- `BrokerDriveScanResult` reports one drive's armed cursor, optional advanced cursor, catch-up entries, optional proven `CatchUpLoss`, and completed `BlockScanOutcome`; `BrokerChannelLostException` identifies the affected drive when a channel or process is lost.
- `BrokerChannelConnector` and `BrokerBlockSectionFactory` are the public host-channel and client-section seams used by `JournalBrokerHost.ServeAsync` and the in-process broker harness.
- `IBrokerOperationReporter` lets host sources publish `WaitingOnVolume` and named `Processing` progress to the watchdog. `ParseThreadAllowance` supplies a mutable per-scan parse-thread share that native parsing reads at each chunk and before path resolution.
- `IIndexDriveWatch` represents one drive's watch. `IIndexWatchSource.StartAsync` returns that handle once the watch is ready; `JournalBatch` and `DriveCaughtUp` carry no drive field because the handle supplies the drive scope.
- `DriveWatchFaultException` reports a drive-owned watch failure. `JournalCatchUpLostException` reports a journal-proven scan catch-up loss, its consecutive count, whether automatic recovery stopped, and the associated `JournalCheckpointLoss`.
- `DriveOperationOutcome` and `DriveOperationResult` support concurrent list and all-drive overloads of `StartWatchingAsync`, `StopWatchingAsync`, `RescanAsync`, and `WaitForCatchUpAsync`; every call returns one ordered result per requested drive after all per-drive work settles.
- `MftBlockProduceResult.CatchUpLoss` and `BrokerDriveScanResult.CatchUpLoss` carry a complete but unresumable scan block when the host proves that its armed cursor fell out of the journal.
- `DriveStatus.ConsecutiveLostCatchUps` and `FileIndex.LostCatchUpRecoveryLimit` expose the per-drive recovery count and its limit of three. A scan whose catch-up holds resets the count; at the limit the last block remains queryable but its watch is refused until a consumer rescan.
- `JournalCheckpointLossDetection.ScanCatchUp`, `WatchFaultKind.CatchUpLost`, `WatchFaultKind.Recovery`, and `WatchCatchUpState.Recovering` identify scan-time journal loss and automatic per-drive recovery.
- `BrokerTestHarness` runs a `JournalBrokerHost` in process over in-memory control and drive pipes and returns the production `BrokerProcess`. `BrokerTestHarnessOptions` supplies the client `TimeProvider`, per-pipe connection failures, and held host writes.
- `BlockFile.Flush(Action<long>?)` flushes a block in 64 MiB ranges and reports cumulative bytes. `BlockWriter.Complete(DateTime, Action<long>?)` completes and flushes through the same progress callback.
- `BrokerFrameKind.CatchUpLost` carries the journal proof for a scan whose catch-up cannot resume; broker frame kinds are densely numbered from `OpenChannel = 1` through `CaughtUp = 17`.
- `FileChange.Timestamp` carries the UTC timestamp of the USN journal record that produced the change, captured at mutation time: a modification reports its own record's time even when a later record in the same batch (including a coalesced close record) restamps the row, and a delete's timestamp survives the tombstone, so a consumer no longer reads the post-batch row or falls back to its own arrival-time clock ([file-wizard#430](https://gitea.fleet.sticktoitive.net/schoen/file-wizard/issues/430))
- `DriveStatus.BlockSource` and the `BlockSource` enum (`None`, `WarmStartedFromCache`, `ProducedByScan`) report where a drive's current block came from, so a consumer's rebuild loop can skip the drives an `OpenAsync` just scanned instead of scanning every cold drive twice. A successful `RescanAsync` leaves the drive reading `ProducedByScan` ([MFTLib#146](https://gitea.fleet.sticktoitive.net/schoen/MFTLib/issues/146))
- `FileIndexOptions.OpenProgress` reports one `IndexDriveOpened` per drive that settles: drive letter, 1-based `SettledCount`, configured drive count, and the settled `BlockSource` and `DriveState`. Drives settle concurrently, callbacks may overlap and arrive out of count order, and a cancelled settle reports nothing. Null reports and allocates nothing; `RescanAsync` stays silent ([file-wizard#382](https://gitea.fleet.sticktoitive.net/schoen/file-wizard/issues/382))
- `FileIndex.QueryUsnJournalSettings` and the `UsnJournalSettings` record report a watched volume's USN journal sizing (`MaximumSize`, `AllocationDelta`) without elevation, via `FSCTL_QUERY_USN_JOURNAL` against a backup-semantics handle on the volume root ([MFTLib#141](https://gitea.fleet.sticktoitive.net/schoen/MFTLib/issues/141))
- `DriveStatus.CheckpointLoss` and the `JournalCheckpointLoss` record report, without elevation, that a drive was cold-scanned because its cached block's USN journal checkpoint was absent from the journal: the checkpoint USN, the journal's `FirstUsn` and `NextUsn` when the loss was detected, its `AllocationDelta` and `MaximumSize`, how far behind the checkpoint was in bytes, and the size a journal would need to be at least to have kept it. That size is the checkpoint-to-tip span rounded up to `AllocationDelta`, plus one more allocation delta because NTFS's documented `CREATE_USN_JOURNAL_DATA` and `USN_JOURNAL_DATA` trimming behavior can leave the journal below its target maximum; it is exact integer arithmetic, not a live measurement ([MFTLib#217](https://gitea.fleet.sticktoitive.net/schoen/MFTLib/issues/217))
- `JournalCheckpointLossCause` separates the two causes MFTLib can tell apart: `CheckpointTrimmed`, where `SizeThatWouldHaveRetained` says the size a journal would need to be at least to have kept the checkpoint, and `JournalRecreated`, where the journal carries a different id so no size would have helped and none is suggested. Opening a drive reads the live journal before adopting a cached block, so an unresumable checkpoint becomes one cold scan with a reason attached instead of a warm start whose watch dies on its first read ([MFTLib#217](https://gitea.fleet.sticktoitive.net/schoen/MFTLib/issues/217))
- `DriveStatus.CheckpointLoss` also reports a journal position lost mid-session, so a live watch the journal outran carries the same fields, causes and arithmetic as the warm-start path instead of only a `WatchFailureMessage`. When a drive's watch faults, MFTLib asks the live journal about the position that watch had reached, which is the drive's block cursor and so the same number a warm start asks about; the classification is the journal's answer rather than a reading of the exception. The drive keeps its block and its rows with nothing after the lost position applied, the report is recorded before the `WatchFaulted` event is raised, and a successful `RescanAsync` clears it. No broker frame or native ABI change: the query is unelevated and runs on the client side ([MFTLib#224](https://gitea.fleet.sticktoitive.net/schoen/MFTLib/issues/224))
- `JournalCheckpointLoss.DetectedDuring` and the `JournalCheckpointLossDetection` enum (`DriveOpening`, `LiveWatch`, `ScanCatchUp`) identify the check that found a loss. Reports remain until a newer report replaces them or a consumer rescan clears them; unrelated faults do not rewrite or clear them. `DetectedDuring` is a required init member ([MFTLib#224](https://gitea.fleet.sticktoitive.net/schoen/MFTLib/issues/224))
- `BrokerProcess.GrowUsnJournalAsync` resizes a volume's USN journal through the elevated broker; the host accepts only growth and replies with the settings read back from the volume ([MFTLib#141](https://gitea.fleet.sticktoitive.net/schoen/MFTLib/issues/141))
- `MftVolume.QueryUsnJournalSettings` and `MftVolume.GrowUsnJournal` expose the same query and grow-only resize to already-elevated callers ([MFTLib#141](https://gitea.fleet.sticktoitive.net/schoen/MFTLib/issues/141))
- `FileIndexOptions.InitialOpenCacheOnly` adopts a cached block whose journal checkpoint cannot be resumed as a `Ready` snapshot with its report attached. `FileIndex.StartWatchingAsync` refuses that drive until a successful per-drive `RescanAsync` publishes a resumable block; a failed rescan preserves the refusal and the old block ([file-wizard#481](https://gitea.fleet.sticktoitive.net/schoen/file-wizard/issues/481))

- `CacheDirectory.EnumerateCached` and the `CachedBlockFile` record list the drives a cache directory holds (drive letter, volume serial, path, size, last-write time) without opening or validating any block, so a consumer never reimplements the inverse of `CacheDirectory.BlockFileName`. A missing directory returns empty and an unrecognised file name is skipped rather than reported ([MFTLib#144](https://gitea.fleet.sticktoitive.net/schoen/MFTLib/issues/144))
- `FileEntry.IsDisposed` reports that the handle's snapshot has been released, distinct from `IsValid` (the default struct value) and `IsDeleted` (a tombstoned row); every read on a disposed handle throws `ObjectDisposedException` ([MFTLib#145](https://gitea.fleet.sticktoitive.net/schoen/MFTLib/issues/145))
- `MftRecord.SequenceNumber` and `UsnJournalEntry.SequenceNumber` expose the NTFS record's sequence number; combined with the record number as `(sequenceNumber << 48) | recordNumber`, it forms the file reference `FileEntry.Open` uses to detect an MFT record NTFS has reused for a different file
- `RowColumns.SequenceNumber` and `BlockFile.SequenceNumbers` carry the sequence number into the packed block format, in a sequence region between the row region and the name pool (`BlockHeader.SequenceRegionOffset`)
- `BlockHeader.LiveRowCount` and `DriveStatus.LiveRowCount` report rows in use and not tombstoned, distinct from `RowCount`'s highest-used-slot-plus-one
- `MftRecord.Size`, `SizeKnown`, and `ModifiedUtc` expose unnamed data-stream size and modification time from the native parser
- `BlockHeader.RootRow` stores the volume root at offset 20: row 5 for MFT blocks and row 0 for enumeration blocks
- `RowFlags.SizeUnknown` marks a zero size whose data attribute lives in an extension record the parser does not follow
- `FileIndexOptions.ProducerPolicy` and `MftProducer`, with `ProducerPolicy`, select MFT or enumeration for initial scans and rescans
- `MftBlockProducer`, `MftBlockProduceRequest`, and `MftBlockProduceResult` let `FileIndex` adopt a completed block and its armed journal cursor
- `BrokerMftBlockProducer` supplies the producer delegate, validates completed blocks, and transfers block ownership to the index while the caller retains client ownership
- `NamedBlockSection`, `IBlockSectionWriter`, `RealBlockSectionWriter`, and `MftBlockRowWriter` write packed rows directly into client-created file-backed sections and complete the header last
- `MftBlockCapacity` plans row and name capacities from `NtfsVolumeInformation`; `BlockScanTarget` supplies one drive's destination to `BrokerProcess.ScanDriveAsync`
- `DriveStatus.ProducerKind`, `MftProducerFailureMessage`, and `DiscardedBlock` report the selected producer, MFT failure, and rejected cache validation result; a drive whose MFT producer fails is reported `DriveState.Failed` and never falls back to a directory walk
- `BlockWriteProgress` reports parsing and transfer progress for block scans
- `IndexScanPhase` (`Enumerating`, `ParsingMft`, `Transferring`) and `IndexScanProgress` report scan progress uniformly across both producers; `BrokerProgressAdapter` forwards `BrokerScanProgress` from the broker-backed producer onto `FileIndexOptions.Progress`
- `IIndexWatchSource`, `IIndexDriveWatch`, `WatchStreamItem`, and `IndexWatchTarget` define the per-drive live-watch seam; `BrokerMftBlockProducer.CreateWatchSource()` supplies a `BrokerIndexWatchSource`
- `FileIndex.StartWatchingAsync` and `StopWatchingAsync` start and stop independent MFT-backed drive watches; `FileIndex.Changed` raises one event per applied change and `FileIndex.WatchFaulted` reports every fault with a drive letter and `WatchFaultKind`
- `DriveStatus.WatchFailureMessage` reports a drive's live-watch failure without invalidating its block; it clears when the drive is re-armed or rescanned
- `FileIndex.RescanAsync` retires, rebuilds, and conditionally restarts the named drive's watch while every other drive remains independent
- `FileChange.PreviousPath` carries the full previous path of a rename
- `JournalMutator` hydrates a row a `DirectoryIndex` scan filtered out on its first journal touch, reporting it as `FileChangeKind.Created` with a null `PreviousPath`
- `FileEntry.Open` opens an MFT-producer entry by NTFS file id (`WindowsFileById`, `OpenFileById`) against a handle on the target volume, which needs no elevation
- `IndexedDrive.FromWindowsVolume` builds a drive from a Windows drive letter, reading its real volume serial so a re-lettered or replaced volume never matches the wrong cached block
- A stale persisted journal cursor is reported as that drive's `Error`, ending only its stream
- Optional trailing `CancellationToken` parameters on every entry point that scans rows: `FileIndex.Find`, `FindByName`, `Search`, `Largest`, `DuplicateNames` and `Root`, and `FileEntry.Children`. The token is read before the first row and then at least every 4096 rows, so a whole-drive scan can be abandoned promptly; cancellation surfaces as `OperationCanceledException`
- `DriveStatus.FailureKind` and the `DriveFailureKind` enum (`None`, `CacheDeclined`, `ProducerFailed`) say why a `DriveState.Failed` drive has no block: a cache-only open that found no usable cache reports `CacheDeclined`, an MFT producer failure reports `ProducerFailed`, and every other state reads `None`. `FileIndex.RescanAsync` now scans and adopts a blockless failed drive, clearing the kind to `None` on success and re-reporting the drive as `ProducerFailed` when that scan fails; an offline drive still refuses a rescan ([file-wizard#423](https://gitea.fleet.sticktoitive.net/schoen/file-wizard/issues/423))
- `DriveFailureKind.InUse` reports a cache-only open that found the drive's cache block owned by another live `FileIndex`: the second opener never validates, renames, or deletes a block whose per-block owner lock it cannot take, so one index can no longer delete a cache out from under another (the file-wizard#424 incident). A non-cache-only open in the same situation scans into a private delete-on-close block and leaves the canonical cache untouched; once the owner is gone, the next open or rescan takes the slot and warm-starts or replaces the cache as before
- `FileIndexOptions.Diagnostics` receives one line for every block-file delete the index performs (a rejected cache validation, a stale ".retired-*" sweep, a superseded block's final release, a cancelled scan's partial replacement, or a rejected producer block), with the deleted path and the reason; `BlockFileCreateOptions.Diagnostics` forwards the same hook through block creation. Null by default, which logs nothing. A `DriveState.Failed` drive now also keeps its `DiscardedBlock` reason instead of dropping it

### Changed

- `JournalBrokerHost` serves a control pipe plus independently connected drive pipes through `ServeAsync(Stream, BrokerChannelConnector, IBlockSectionWriter?, CancellationToken)`. Its constructor accepts the process-wide parse budget through `processorCount` and all host timing through `TimeProvider`.
- `MftRecordBatchSource` receives the drive, its live `ParseThreadAllowance`, an `IBrokerOperationReporter`, progress, and cancellation. `JournalBatchSource` receives the reporter and cancellation. `UsnJournalCatchUpSource` performs one bounded read through its `maximumBufferReads` parameter.
- `BlockWriteReporting` pairs block-write progress with an optional operation reporter, and `IBlockSectionWriter.Write` uses it so block ranges report watchdog progress while flushing.
- `MftVolume.StreamRecords(string? filter, MatchFlags matchFlags, IProgress<MftScanProgress>? progress, ParseThreadAllowance? parseThreads, CancellationToken cancellationToken)` and `MftVolume.ReadRecordBatches(bool resolvePaths, int batchSize, IProgress<MftScanProgress>? progress, ParseThreadAllowance? parseThreads, CancellationToken cancellationToken)` expose parse-thread allowance, progress, and cancellation. Cancellation reaches the native parser between chunks, between 4096-record worker slices, and between path-resolution slices; the native ABI is version 2 and carries cancellation state plus the current thread allowance.
- The native `ParseMFTRecordsWithProgress` export accepts a caller-owned `MftParseControl` containing `cancelRequested` and `parseThreadAllowance`; a cancelled parse joins its workers and reports cancellation before managed code throws `OperationCanceledException`.
- `IElevatedEntryRunner.RunBroker(string?)` accepts only the control pipe name. `--once` is not part of broker launch or dispatch.
- `BrokerMftBlockProducer` connects through `Func<CancellationToken, Task<BrokerProcess>>`; its validated-result callback receives `BrokerDriveScanResult`, and `CreateWatchSource` supplies the per-drive `BrokerIndexWatchSource`.
- `FileIndex` watch lifecycle is per drive. Single-drive start, stop, rescan, and catch-up waits affect only the named drive; their list and all-drive overloads run drives concurrently. `Drive` and `Apply` faults publish `Recovering` and trigger one automatic rescan, `Channel` faults do not recover, and a failed recovery raises `WatchFaultKind.Recovery` and leaves the drive faulted for a consumer start or rescan.
- `WatchFault.DriveLetter` is a non-nullable `char`; `WatchFaultKind` is ordered as `Subscriber`, `Drive`, `Apply`, `CatchUpLost`, `Channel`, `Recovery`. `WatchCatchUpState.Recovering` sits between `CaughtUp` and `Faulted`.
- `FileIndex.OpenAsync` settles configured drives concurrently. `IndexDriveOpened.SettledCount` is the 1-based settle order; progress callbacks run on each settling thread, may overlap, and may arrive out of count order. A cancelled drive settle reports nothing, so a failed or cancelled open can report only a subset.
- `BrokerProtocol` and `BrokerFrame` use request ids on control frames and drive-scoped payloads on operation channels. The drive channel supplies drive scope; its `ArmAndScan`, `StartWatch`, `JournalBatch`, `CaughtUp`, and `Error` frames contain neither a drive nor an arm generation. Scan catch-up loss is terminal and explicit.
- `BrokerDiagnostics.Log` takes a channel tag and queues records to one bounded background writer, so diagnostics I/O never blocks a broker operation.
- `FileIndex.StartWatchingAsync(X)` completes after X's channel is connected, `StartWatch` is written, and the ready handle is published with its pump queued. Readiness is per drive and separate from `WaitForCatchUpAsync` ([MFTLib#247](https://gitea.fleet.sticktoitive.net/schoen/MFTLib/issues/247))
- A cache-mode `FileIndex` now holds a sibling `<block>.lock` file (opened with `FileShare.None`) for as long as it owns a canonical cache block. A second `FileIndex` over the same cache directory - in any process - that cannot take that lock reports the drive `Failed` with `DriveFailureKind.InUse` on a cache-only open instead of warm-starting or deleting the block. Consumers that deliberately shared one cache block between two live indexes must open the second index with `NoCache` or expect the in-use outcome
- **Recompile required.** The optional `CancellationToken` parameters listed above are source compatible but not binary compatible: a consumer bound to the 0.3.0 assemblies calls method signatures that no longer exist and fails at runtime with `MissingMethodException` until it is rebuilt against this version. This matters for a consumer that vendors prebuilt MFTLib binaries rather than building from source
- `FileChange` gains a required positional `Timestamp` parameter between `Path` and `PreviousPath`. Consumers that only receive `FileChange` instances (the `Changed` event, `ApplyJournalEntries`) need no source change; any code that constructs a `FileChange` must pass the originating record's timestamp and, like the rest of this release, recompile against this version
- Scanning on one thread while another disposes the index is safe. Each of the seven entry points above holds the snapshot it reads for its whole duration, and `FileIndex.DisposeAsync` cancels the queries in flight and waits for every one of those readers to leave, on the current snapshot and on the retired ones, before it unmaps anything. A query racing disposal ends with `OperationCanceledException`, or `ObjectDisposedException` if it had not started, rather than reading a released mapping. `FileEntry.Children` carries no reference to its index, so disposal waits that listing out rather than cancelling it. Every other `FileEntry` member reads a single row and keeps its per-access `IsDisposed` check, which is unchanged

- `FileIndex.Find` accepts a native filesystem path and resolves it against the longest matching indexed root directory instead of requiring a `X:\` drive-letter prefix, so it is the exact inverse of `FileEntry.Path` on every platform, including a Linux file name containing a backslash. Child names match by the block's own rule: an MFT block folds case, an enumeration block over a case-sensitive root matches ordinally ([MFTLib#143](https://gitea.fleet.sticktoitive.net/schoen/MFTLib/issues/143))
- `FileEntry.Path` renders the drive block's real root directory joined with the name chain using the host separator, so it is openable and can be looked up again on any platform; the drive key is never rendered into a path. `FileChange.Path` and `PreviousPath` come from the same builder and change with it. A Windows MFT block is byte-identical because its root directory is `X:\` ([MFTLib#143](https://gitea.fleet.sticktoitive.net/schoen/MFTLib/issues/143))
- `FileIndex.DisposeAsync` releases every snapshot it holds, current and retired, unconditionally, including retired snapshots whose handles are already unreachable. It returns only once those blocks are actually closed: a release a snapshot finalizer has already begun is waited out rather than taken for a finished one. During an index's lifetime, a retired snapshot still releases through its finalizer once handles minted before the rescan are unreachable. A `FileEntry` held across disposal reports `IsDisposed` and throws `ObjectDisposedException` on every read ([MFTLib#145](https://gitea.fleet.sticktoitive.net/schoen/MFTLib/issues/145))
- The native interface version constant (`MFT_NATIVE_ABI_VERSION`) is 2; compact entries remain 50 bytes, and `MftParseResult` includes cancellation state for the parse control block. Managed and native binaries must match
- Block format version is 3: the declared header is 112 bytes (`LiveRowCount` at offset 88, `SequenceRegionOffset` at offset 96, `CacheTagFourCc` at offset 104, and `CacheTagVersion` at offset 108), and a sequence-number region sits between the row region and the name pool. A version mismatch means discard the block and rescan
- `FileIndexOptions.NoCache` blocks are created with `FileOptions.DeleteOnClose`, so the operating system removes them when the last handle closes, including on a killed process, instead of relying on a managed delete during dispose plus a stale-block sweep on the next open
- `ScanReady` replaces record count and byte length with `RowCount`, `NamePoolUsedBytes`, and `SkippedRecordCount` (three int64 fields)
- `BlockScanOutcome` carries `SectionName`, `Block`, `RowCount`, `NamePoolUsedBytes`, and `SkippedRecordCount`; the caller owns the block
- `BrokerMftBlockProducer`'s `scanCompleted` callback receives a validated `BrokerDriveScanResult` and does not run for a failed scan or rejected block

### Removed

- Removed `JournalBrokerClient`, `JournalBrokerScanSession`, `JournalBrokerSessionState`, `BrokerScanResult`, and `NtfsVolumeQueryResult`; `BrokerProcess` and `BrokerDriveScanResult` are the process and one-drive scan surfaces.
- Removed `ScanSessionTestHarness`; `BrokerTestHarness` exercises the production client and host over in-memory per-operation pipes.
- Removed `WatchStreamNotRunningException`, `DriveWatchFailure`, and `ReadyOnFirstMoveWatchStream`; a ready `IIndexDriveWatch` is returned by `StartAsync`, and its read throws a drive or channel failure.
- Removed the shared-watch protocol and client members `SendStartWatchAsync`, `StopLiveWatchAsync`, `QueryVolumesAsync`, `ArmScanAndCatchUpAsync`, `BrokerDied`, `ReplaceWatchCursors`, and `WatchCursors`; control requests and drive operations are methods on `BrokerProcess`.
- Removed the merged-stream `IIndexWatchSource` start, arm, and disarm members plus `CreateBatchSource`; `StartAsync` and `IIndexDriveWatch` define the per-drive seam.
- Removed `BrokerScanOptions.BlockTargets`; each `BrokerProcess.ScanDriveAsync` call receives one `BlockScanTarget`.
- Removed `BrokerFrame.ArmEpoch`, `EndWatchAck`, and `BrokerProtocol.WriteWarning`; a drive channel carries one operation and `CatchUpLost` is the terminal scan-loss frame.
- Removed the internal `WatchSession`, `_rescanGate`, `_swapGate`, `_cacheOnlyUnresumableCheckpointOrdinals`, `_unreportedWatchFaults`, and `ResumeDriveAfterRescanAsync`; per-drive runtimes, lifecycle gates, write gates, and recovery tickets hold that state.
- Removed `IndexDriveOpened.Ordinal`; `SettledCount` reports concurrent settle order.
- Removed the internal `LookupEngine.TryParseDriveLetter`; a path no longer carries a drive key
- Removed `IMmfWriter`, `IStreamingMmfWriter`, `IMmfReader`, and `IStreamingMmfReader`
- Removed `RealMmfWriter`, `RealMmfReader`, and `MmfWriteResult`
- Removed `BrokerScanOutputFormat`, `ScanPayload`, `ScanRecord`, and `ScanRecordBatchConsumer`; cold scans return packed blocks only
- Removed `DriveScanSource`, `StreamingDriveScanSource`, and `ProgressStreamingDriveScanSource`; `JournalBrokerHost` takes `MftRecordBatchSource` as its scan source
- Removed `BrokerScanPhase.ResolvingPaths`; the broker parses without path resolution
- Removed `BrokerScanOptions.ConsumeRecords`, `OutputFormat`, `MmfCapacityBytes`, and `MmfCapacityPlanner`
- Removed `JournalBrokerHost.ArmAndScan` and `ArmAndScanBatches`; `ServeAsync` takes `IBlockSectionWriter?` for block output
- Removed the sixth (output-format) field from each `ArmAndScan` spec token
- Removed `DriveBlock`'s `deleteFileOnRelease` constructor parameter; deletion of a no-cache block is the operating system's job
- Removed `FileIndex`'s `CleanupStaleNoCacheBlocks` sweep, which compensated for the managed delete this removes
- Removed `FileEntry.Open`'s `NotSupportedException` for a non-enumeration producer; it opens by file id instead
- Removed `ProducerPolicy.Auto`, `MftOnly`, and `EnumerationOnly`; `ProducerPolicy.Mft` and `ProducerPolicy.Enumeration` are the two values
- Removed `FileChange`'s three-argument constructor and its `PreviousName` parameter; `FileChange` now requires `Path` and carries `PreviousPath`
- Removed `IndexScanProgress`'s two-argument constructor (`RowsWritten`, `CurrentDirectory`); it now requires `DriveLetter`, `Phase`, and `RowsWritten`, with `TotalRows` and `CurrentDirectory` optional

### Fixed

- `FileIndex.StartWatchingAsync` honors cancellation while `BrokerIndexWatchSource` opens the drive channel and writes `StartWatch`; cancellation closes that operation's pipe before the start throws ([MFTLib#250](https://gitea.fleet.sticktoitive.net/schoen/MFTLib/issues/250))
- Broker watch startup errors add cached-cursor rescan guidance only when a live journal query confirms a changed journal id or a checkpoint older than `FirstUsn`. Unrelated messages containing words such as "cursor" or "deleted" no longer trigger that guidance, and lost-cursor guidance no longer depends on native error wording. If the journal cannot answer, the original error is preserved. Broker frames and the native ABI are unchanged.
- Broker diagnostics no longer feed the watch they observe: while diagnostics are enabled, the broker drops journal entries for both `broker-diagnostics.log` files (its own and the client's, forwarded across the runas boundary as `--diag-log`) before building JournalBatch frames, and skips batches the filter empties, so a live watch with diagnostics on no longer generates self-sustaining journal traffic; `MFTLIB_BROKER_DIAG_INCLUDE_SELF=1` opts back in ([file-wizard#299](https://gitea.fleet.sticktoitive.net/schoen/file-wizard/issues/299), [git-wizard#143](https://gitea.fleet.sticktoitive.net/schoen/git-wizard/issues/143))

- A `BlockWriter` write racing `BlockFile.Dispose` on another thread (a broker test fixture tearing down while its serving task is mid-row-write) could dereference an unmapped view and kill the process with an `AccessViolationException`; disposal now refuses new writer operations when it begins and waits for in-flight ones before unmapping, so a late writer fails with a catchable `ObjectDisposedException` (refiled from [git-wizard#181](https://gitea.fleet.sticktoitive.net/schoen/git-wizard/issues/181))
- A fault on one drive is scoped to that drive's watch instance and cannot end, delay, or mutate a sibling drive's watch.
- A journal-invalidation `Error` frame ends only its drive channel with `DriveWatchFaultException`; sibling channels keep streaming.
- `IndexNavigation.BuildPath`, `IsUnder`, and subtree-restricted queries explicitly throw `InvalidDataException` when a valid parent chain exceeds `BlockLayout.MaximumPathDepth` hops instead of silently returning truncated paths or false negatives, while preserving cycle detection
- Under capacity exhaustion (such as a delete following an unrecorded create), `JournalMutator` suppresses the `FileChangeKind.Deleted` change event when row hydration fails rather than emitting an invalid event for an unrecorded record
- `Microsoft.SourceLink.GitHub` bumped from 8.0.0 to 10.0.401, moving the transitively pulled `Microsoft.Build.Tasks.Git` past the version affected by CVE-2026-62900 ([GHSA-23fw-v26w-5fgq](https://github.com/advisories/GHSA-23fw-v26w-5fgq))
- An aggregate `FileIndex.WaitForCatchUpAsync(CancellationToken)` that ended by cancellation or fault stayed reachable from every drive still catching up, through a slot-completion continuation that could never be detached, so repeated bounded waits against a drive that never caught up accumulated one wait coordinator each; the continuations are now cancelled, which removes them from the pending drive, as soon as the aggregate wait completes, cancels, or faults ([MFTLib#182](https://gitea.fleet.sticktoitive.net/schoen/MFTLib/issues/182))
- Closing a drive pipe cancels only that drive operation; closing the control pipe ends the host session and every channel without an unhandled broken-pipe exception ([MFTLib#196](https://gitea.fleet.sticktoitive.net/schoen/MFTLib/issues/196), [MFTLib#199](https://gitea.fleet.sticktoitive.net/schoen/MFTLib/issues/199))
- Per-drive watch channels close the cross-drive head-of-line blocking and fault-coupling tracked by [MFTLib#252](https://gitea.fleet.sticktoitive.net/schoen/MFTLib/issues/252).

## 0.3.0

### Breaking Changes

- Targets net10.0; net8.0 consumers must upgrade.
- Replaced the pre-release seven-argument `UsnJournalEntry.Create(...)` signature with `Create(UsnJournalEntryOptions)`; callers building against 0.3.0 previews must migrate to an object initializer

### Features

- **USN journal support** on `MftVolume`:
  - `QueryUsnJournal()` - get the current journal cursor (`UsnJournalCursor`) to baseline incremental updates after a full scan
  - `ReadUsnJournal(cursor)` - batch catch-up read; returns `(UsnJournalEntry[] Entries, UsnJournalCursor UpdatedCursor)`. Throws `InvalidOperationException` if the journal was recreated or entries were overwritten (caller should fall back to a full rescan)
  - `WatchUsnJournal(cursor, cancellationToken)` - live `IAsyncEnumerable<UsnJournalEntry[]>` event stream; blocks on the kernel (zero CPU) until changes arrive, unblocks via `CancelIoEx` on cancellation
  - `WatchUsnJournalWithCursor(cursor, cancellationToken)` - same as above but yields `(UsnJournalEntry[] Entries, UsnJournalCursor Cursor)` so callers can persist progress without a separate `QueryUsnJournal` IOCTL
- `UsnJournalEntry` exposes `RecordNumber` / `ParentRecordNumber` (48-bit Master File Table (MFT) segment indices matching `MftRecord`), `Usn`, `Timestamp`, `Reason`, `FileAttributes`, `FileName`, plus `IsCreate` / `IsDelete` / `IsRename` / `IsClose` reason helpers
- `UsnJournalEntry.Create(UsnJournalEntryOptions)` - public factory with a property-based value carrier for reconstructing an entry from already-decoded values (e.g. journal data serialized to disk and rebuilt in another process)
- `MftRecord.FileAttributes` now sourced from `$STANDARD_INFORMATION` (preferred) with `$FILE_NAME` fallback
- Added public `IElevationProvider` interface (with `ElevationUtilities.DefaultProvider`) so consumers can substitute elevation behavior in their own tests
- **Bounded materialization API**:
  - `MftResult.MaterializeBatches(int batchSize = 4096)` - yields materialized batches of records with bounded memory footprint over the existing result lifetime
  - `MftVolume.ReadRecordBatches(bool resolvePaths = false, int batchSize = 4096)` - owning iterator that streams and materializes record batches, releasing the native result upon completion or early disposal
  - Unified `ToArray()` row decoding over `MaterializeBatches()` while maintaining full source compatibility
- **VolumeBroker subsystem** - `BrokerProcess` owns one elevated process and control pipe; `JournalBrokerHost` handles concurrent control requests and one scan or watch on each drive pipe. `BrokerProtocol`, `BrokerFrameKind`, and `BrokerFrame` carry request-id-routed control messages plus drive-scoped operation frames. `BrokerScanProgress` and `BrokerScanOptions` report one scan's progress, `NtfsVolumeInformation` supplies its capacity inputs, and `BrokerDiagnostics` traces tagged channels through a bounded background writer. `ElevatedEntryPoint.TryHandle` dispatches `--broker --pipe NAME`, and `BrokerLauncher.Launch` starts the child through `runas`.
- **`MFTLibTestExtensions` test-harness assembly** - public `BrokerTestHarness` and `BrokerTestHarnessOptions` connect the production `BrokerProcess` and `JournalBrokerHost` over in-memory control and drive pipes. Consumer tests exercise the production lifecycle and fault surfaces without elevation or friend assembly access. The assembly ships as the separate `MFTLib.TestExtensions` NuGet package.

### Improvements

- MFT file record geometry is now detected at runtime instead of assumed to be 1024 bytes: native volume parsing queries the volume's actual record size (`FSCTL_GET_NTFS_VOLUME_DATA`) at parse time, and parsing an exported MFT file reads the record size out of record 0's header. Both 1024-byte and 4096-byte record sizes are supported
- Versioned Compact Native ABI (version 2): compact entries carry size, modification time, and sequence number, while the parse result reports cancellation and native progress reports `MftScanPhase`
- Native path resolution reports live `MftScanPhase.ResolvingPaths` progress across worker threads
- Native path resolution now supports variable-length paths up to 32767 UTF-16 units without truncation at 1024-character boundaries
- Graceful allocation-failure fallback in path resolution: out-of-memory during path resolution preserves raw parsed file entries and filenames without raising errors
- Added native and managed ABI compatibility checks via `GetMftNativeAbiVersion()` and `EnsureCompatibleNativeAbi()`
- Native path resolution now parallelizes across worker threads (same fan-out as fixup+parse) when `numThreads > 1`, with a serial fallback
- Path name-pool exhaustion is now surfaced via the native `errorMessage` ("Path name pool exhausted; N names dropped, some paths truncated") instead of silently truncating
- Self-elevation now returns `false` without attempting UAC when no interactive desktop is available (for example, CI or a Session 0 service)
- Reorganized managed sources by MFT, journal, broker, elevation, interop, and internal responsibilities; split scan, journal, broker connection, transport, and session behavior into focused partials without changing the public API
- Reworked the README and added a broker integration guide covering installation, API selection, memory lifetime, race-free scan/catch-up, live watch, rescans, recovery, and deployment
- Fixed native `bool` marshaling for synthetic generation so conversion failures reliably propagate to managed callers
- Fixed synthetic generator teardown after an asynchronous write failure so the completed writer is joined exactly once
- `BrokerProcess.LaunchAsync` has a bounded, configurable connection timeout (`DefaultConnectTimeout`, default 30 seconds) that throws a descriptive `TimeoutException` if the elevated child does not connect to the control pipe
- The root directory record (MFT record 5) survives a path-resolved scan: `MftRecord.FullPath` returns the drive root (`C:\`, or `\` without a drive letter) and `FileName` returns `.`, so `ReadAllRecords`, `ReadRecordBatches`, and broker-written blocks keep the record and journal entries created directly under the root resolve their parent. A scan without `MatchFlags.ResolvePaths` yields a null `FullPath` for record 5

### Tests

- 100% native coverage achieved without admin via synthetic seams
- Added USN journal test suites (synthetic, live, and admin-elevated)
- Added VolumeBroker test suites (protocol/payload round-trips, host and client pipe-loop behavior, elevated-entry dispatch, broker-death detection), keeping the repo at 100% managed line/branch/method coverage
- Added deterministic per-drive broker and index tests covering process ownership, channel isolation, watch/rescan/recovery state, cancellation, and in-process end-to-end paths.
- Added `BrokerTestHarness` tests exercising control requests, scans, watches, liveness, connection failures, and held writes through the public `MFTLibTestExtensions` surface.
- Added volume-query tests for `QueryVolume`/`VolumeInfo` wire frames, host handling, `BrokerProcess.QueryVolumeAsync`, `NtfsVolumeInformation.MftRecordCount`, and an admin-elevated live query.

## 0.2.0

### Breaking Changes

- Removed `MFTParse` class (use `MftVolume.ReadAllRecords()` or `MftVolume.FindByName()` instead)
- Removed `MftFileEntry` interop struct (internal; replaced by `MftRecord`)
- Replaced `uint matchFlags` with `MatchFlags` enum (`ExactMatch`, `Contains`, `ResolvePaths`) across all public APIs
- `FileUtilities`, `MFTUtilities`, and `Kernel32` are now internal
- `FindByName` now takes `MatchFlags` instead of `bool exactMatch` / `bool resolvePaths` (e.g. `FindByName(".git", MatchFlags.ExactMatch | MatchFlags.ResolvePaths)`)
- `MftVolume.BufferSizeRecords` settable property replaced with `bufferSizeRecords` parameter on `MftVolume.Open()`
- Removed `EnsureElevated()` (use `CanSelfElevate()` + `TryRunElevated()` instead)
- Removed `MftVolume.ResolvePath(ulong)` (use `ReadAllRecords(resolvePaths: true)` or `MftPathUtilities.ResolvePath()` with a lookup)
- Removed `MFTUtilities.GetFileNameForDriveLetter()` (use `MFTUtilities.GetVolumePath()`)
- Removed unused `Kernel32` P/Invoke methods (`CloseHandle`, `ReadFile`, `SetFilePointerEx`, `DeviceIoControl`)

### Improvements

- Added `MatchFlags` flags enum for type-safe filter options
- Added `CanSelfElevate()` and `TryRunElevated(string, int)` to `ElevationUtilities`
- Swappable native function indirection (`MFTLibNative`) for testability
- Swappable dependencies in `ElevationUtilities` for testability
- Extracted `MftVolume.ExtractDriveLetter()` helper
- Replaced magic numbers in `MftResult` with named constants (`NativeEntrySize`, `NativePathEntrySize`)
- Native error messages (e.g. "Volume is not NTFS", allocation failures) are now surfaced as `InvalidOperationException` instead of being silently ignored
- Fixed path resolution when filter is null
- Enabled `ContinuousIntegrationBuild` for deterministic NuGet DLLs

### Tests

- Expanded test suite from 16 to 80+ tests
- Achieved 100% line, branch, method, and full-method coverage
- Added admin-elevated test suite (`MftVolumeAdminTests`)
- Added coverage for `ElevationUtilities`, `MftResult`, `MftRecord`, path resolution, native mock indirection
- Added combined coverage script for admin + non-admin tests
- Tagged interactive UAC tests with `TestCategory("Interactive")`

## 0.1.2

- Initial public release
- MFT parsing with native C++ core
- Path resolution via `MftPathUtilities`
- `MftVolume.ReadAllRecords()` and `FindByName()` APIs
- Filename filtering (exact match or contains, case-insensitive)
- Configurable buffer sizes
- Self-elevation via `ElevationUtilities`
