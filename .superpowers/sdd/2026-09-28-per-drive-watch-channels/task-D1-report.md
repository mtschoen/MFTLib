# Task D1 report

Status: DONE

## Files changed

### AGENTS.md

- Rewrote Architecture / MFTLib / Checkpoint loss for per-drive faults, automatic recovery, retained LiveWatch reports, ScanCatchUp loss, the three-loss limit, and cache-only refusal behavior.
- Replaced Watch start readiness with the per-drive channel readiness contract.
- Rewrote Watch and catch-up lifetime for IIndexDriveWatch, Recovering, per-drive cancellation, recovery, and channel-fault behavior.
- Added Per-drive state machine, including DriveRuntime and WatchInstance ownership, identity checks, linearization points, recovery tickets, stop behavior, and callback reentrancy.
- Added concurrent OpenAsync behavior and OpenProgress ordering and cancellation rules.
- Added the FileIndex lock order.
- Rewrote VolumeBroker for BrokerProcess, the control pipe, request ids, independent drive channels, the parse-thread allocator, CatchUpLost, heartbeats, watchdog behavior, and stall limits.
- Rewrote the MFTLibTestExtensions project entry for BrokerTestHarness and the production fault surfaces.
- Updated journal-isolation callback threading from one watch pump to concurrent per-drive pump threads.

### CHANGELOG.md

- Updated the Unreleased introduction to include the per-drive watch-channel work.
- Rewrote Unreleased / Added for BrokerProcess, per-drive scan and watch types, operation reporting, parse-thread allowance, batched drive results, lost-catch-up surfaces, harness options, ranged block flushing, frame numbering, and concurrent open progress.
- Rewrote Unreleased / Changed for host and source signatures, native cancellation and thread allowance, RunBroker(string?), the per-drive FileIndex lifecycle, enums, concurrent OpenAsync, protocol routing, diagnostics, and watch readiness.
- Consolidated Unreleased / Removed around the deleted client, session, shared-watch protocol, old state, and obsolete API identifiers.
- Updated Unreleased / Fixed for drive isolation and issue 252 closure.
- Replaced stale VolumeBroker, test-harness, ABI, and test-suite descriptions in 0.3.0 with the API present at HEAD.

## Deleted identifier searches

Each name was searched separately with a fixed-string rg search over AGENTS.md and CHANGELOG.md. Every result is in an explicit CHANGELOG Removed entry. There are no hits in AGENTS.md.

| Identifier | Result count |
| --- | ---: |
| JournalBrokerClient | 1 |
| JournalBrokerScanSession | 1 |
| ScanSessionTestHarness | 1 |
| WatchStreamNotRunningException | 1 |
| DriveWatchFailure | 1 |
| ReadyOnFirstMoveWatchStream | 1 |
| WatchSession | 1 |
| _swapGate | 1 |
| _rescanGate | 1 |
| ArmEpoch | 1 |
| EndWatchAck | 1 |
| SendStartWatchAsync | 1 |
| StopLiveWatchAsync | 1 |
| QueryVolumesAsync | 1 |
| ArmScanAndCatchUpAsync | 1 |
| BrokerScanResult | 1 |
| BrokerDied | 1 |
| WriteWarning | 1 |
| _cacheOnlyUnresumableCheckpointOrdinals | 1 |
| _unreportedWatchFaults | 1 |
| ResumeDriveAfterRescanAsync | 1 |
| IndexDriveOpened.Ordinal | 1 |
| ReplaceWatchCursors | 1 |
| WatchCursors | 1 |

## Code and specification differences

No public API contradiction was found between specification section 3 and HEAD. The following implementation rulings refine or replace less-specific design text, and the documentation describes the code and rulings:

- Spec section 2.2 does not say that a processing channel with recent progress receives heartbeats. HostPipeWriter and ruling C5-Q1 do. AGENTS.md describes the current implementation: recent processing progress is heartbeated, while processing without progress beyond the limit receives Stalled and is cancelled.
- Spec section 2.2 says a blocked heartbeat write closes a channel. HostPipeWriter.Visit and ruling C5-Q2 skip a pipe whose write lock is held, and BrokerFrameReader applies the client-side 30-second no-frame limit. AGENTS.md describes the current implementation.
- The design text does not fully specify OpenProgress callback concurrency. HEAD and ruling B9 report from settling threads without the FileIndex lock, so callbacks can overlap and arrive out of SettledCount order, and a cancelled settle reports nothing. AGENTS.md and CHANGELOG.md describe the current implementation.

No other code/specification difference was found in the behavior covered by this lane.

## Verification

- AGENTS.md: 479 CRLF line endings, 0 bare LF line endings.
- CHANGELOG.md: 237 CRLF line endings, 0 bare LF line endings.
- Forbidden Unicode dash scan: no U+2013 or U+2014 in either edited documentation file.
- git diff --check: clean.
- Public API review: checked git diff 3597586 HEAD -- MFTLib MFTLibTestExtensions and the public API in specification section 3 against the Unreleased entries.
- aislop 0.16.0: 99/100, 0 errors, 5 warnings, 0 fixable findings.
- Baseline warnings: two AsyncFixer01 findings in MFTLib.Tests/NativeSeamIsolationFixtures.cs and two redundant XML documentation findings in MFTLib/Index/CachedBlockDeletionOutcome.cs.
- Ruled warning: JournalBrokerHost has 8 parameters at MFTLib/Broker/Host/JournalBrokerHost.cs:46.
- Findings beyond the allowed baseline and ruled constructor warning: none.

## Not run or not verified

- Build and tests were not run, as directed for this documentation-only lane.
- Runtime Windows broker behavior was not executed; behavior was verified against HEAD source, the design specification, controller rulings, and the B5 and B6 reports.

## Fix round 1

### Review findings

- `CHANGELOG.md:82`: narrowed watch-start readiness to the implemented boundary: X's channel is connected, `StartWatch` is written, and the ready handle is published with its pump queued. It no longer claims the queued pump is already reading.
- `CHANGELOG.md:92`: corrected the current Unreleased block contract to format version 3 and a 112-byte declared header, including `CacheTagFourCc` at offset 104 and `CacheTagVersion` at offset 108. This statement is in Unreleased, so it was corrected in place rather than preserved as released history.
- `CHANGELOG.md:73`: added the complete HEAD signatures for `MftVolume.StreamRecords` and `MftVolume.ReadRecordBatches`, including progress, parse-thread allowance, and cancellation. The stale two-parameter signature at `CHANGELOG.md:161` is in the released 0.3.0 section and was left untouched.
- `CHANGELOG.md:80`: replaced the history wording with the current wire contract: the drive channel supplies drive scope, and its operation frames contain neither a drive nor an arm generation.
- `AGENTS.md`: no fix-round edit was needed; its watch-start readiness statement at lines 287-288 already matches HEAD.

### Unreleased contract re-verification

Checked each remaining explicit signature, shape, version, and numeric contract against HEAD:

- `JournalBrokerHost.ServeAsync(Stream, BrokerChannelConnector, IBlockSectionWriter?, CancellationToken)` and the constructor's `processorCount` and `TimeProvider` parameters.
- `MftRecordBatchSource`, `JournalBatchSource`, and `UsnJournalCatchUpSource`, including `ParseThreadAllowance`, `IBrokerOperationReporter`, progress, cancellation, and `maximumBufferReads`.
- `BlockWriteReporting` and `IBlockSectionWriter.Write`.
- `MftVolume.StreamRecords` and `ReadRecordBatches`, plus the caller-owned native `MftParseControl` used by `ParseMFTRecordsWithProgress`.
- `IElevatedEntryRunner.RunBroker(string?)`.
- `BrokerMftBlockProducer`'s `Func<CancellationToken, Task<BrokerProcess>>` connection, `Action<BrokerDriveScanResult>?` callback, and `CreateWatchSource()` return surface.
- `BlockFile.Flush(Action<long>?)` and `BlockWriter.Complete(DateTime, Action<long>?)`.
- `WatchFaultKind` order (`Subscriber`, `Drive`, `Apply`, `CatchUpLost`, `Channel`, `Recovery`) and `WatchCatchUpState` order (`NotStarted`, `CatchingUp`, `CaughtUp`, `Recovering`, `Faulted`).
- `BrokerFrameKind`'s dense numbering from `OpenChannel = 1` through `CaughtUp = 17`, including `CatchUpLost = 13`; `ScanReady`'s three `long` fields; and the `BlockScanOutcome` shape.
- Native ABI version 2, managed expected ABI version 2, and the 50-byte compact-entry stride.
- Block format version 3, 112 header-field bytes, and header offsets 88, 96, 104, and 108.
- Lost-catch-up recovery limit 3, row-scan cancellation interval 4096, and block flush range 64 MiB.

The deleted-identifier searches were also rerun individually over `AGENTS.md` and `CHANGELOG.md`. Each name in the existing table still has exactly one result, and every result remains in an explicit CHANGELOG Removed entry.

### Verification

- `git diff --check`: clean before the final scan.
- Line endings: `AGENTS.md` has 479 CRLF and 0 bare LF; `CHANGELOG.md` has 237 CRLF and 0 bare LF.
- Forbidden Unicode dash scan: no U+2013 or U+2014 in either documentation file.
- Smoke test: not applicable because this fix is prose only.
- aislop 0.16.0: 99/100, 0 errors, 5 warnings, 0 fixable findings.
- Baseline warnings: two AsyncFixer01 findings in `MFTLib.Tests/NativeSeamIsolationFixtures.cs` and two redundant XML documentation findings in `MFTLib/Index/CachedBlockDeletionOutcome.cs`.
- Ruled warning: `JournalBrokerHost` has 8 parameters at `MFTLib/Broker/Host/JournalBrokerHost.cs:46`.
- Findings beyond the allowed baseline and ruled constructor warning: none.
