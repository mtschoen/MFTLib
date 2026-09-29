### Finding Verdicts

1. ADDRESSED. `CHANGELOG.md:82` now says start completes after X's channel is connected, `StartWatch` is written, and the handle is published with its pump queued. That is the implemented boundary: `BrokerProcess.OpenWatchChannelAsync` supplies `StartWatch` as the first request (`MFTLib/Broker/Client/BrokerProcess.Watch.cs:11-18`), `OpenChannelAsync` awaits connection and the first write before returning (`MFTLib/Broker/Client/BrokerProcess.Channels.cs:22-41`), and `TryPublishHandle` marks the instance running and queues `PumpAsync` (`MFTLib/Index/FileIndex.WatchDrive.cs:350-365`). The first read remains later in the queued pump (`MFTLib/Index/FileIndex.WatchPump.cs:19-24`), which the CHANGELOG no longer overstates.

2. ADDRESSED. `CHANGELOG.md:92` states block format version 3 and a 112-byte declared header. HEAD defines those exact values in `MFTLib/Index/BlockLayout.cs:14-19`, and the listed offsets 88, 96, 104, and 108 match `MFTLib/Index/BlockHeader.cs:43-46`.

3. ADDRESSED. The current Unreleased contract at `CHANGELOG.md:73` gives the complete `StreamRecords` and `ReadRecordBatches` signatures. They match `MFTLib/Mft/MftVolume.cs:73-74` and `MFTLib/Mft/MftVolume.cs:120-121`, including progress, parse-thread allowance, and cancellation with no defaults. The older two-parameter signature at `CHANGELOG.md:161` is under the released `0.3.0` heading at `CHANGELOG.md:141` and correctly remains untouched history.

4. ADDRESSED. `CHANGELOG.md:80` now states the current wire contract directly: the drive channel supplies drive scope, and operation frames contain neither a drive nor an arm generation. The drive-pipe scope is also described by HEAD at `MFTLib/Broker/Protocol/BrokerFrame.cs:6-11`. The changed entry contains no `no longer` history narration.

Other Unreleased contract sampling from the implementer's re-verification list (14 samples, all matching HEAD):

- `JournalBrokerHost.ServeAsync(Stream, BrokerChannelConnector, IBlockSectionWriter?, CancellationToken)` matches `MFTLib/Broker/Host/JournalBrokerHost.Session.cs:28-29`; the constructor includes `processorCount` and `TimeProvider` at `MFTLib/Broker/Host/JournalBrokerHost.cs:46-54`.
- `MftRecordBatchSource` carries drive, `ParseThreadAllowance`, operation reporter, progress, and cancellation at `MFTLib/Broker/Sources/MftRecordBatchSource.cs:9-11`.
- `JournalBatchSource` carries drive, cursor, operation reporter, and cancellation at `MFTLib/Broker/Sources/JournalBatchSource.cs:9-13`.
- `UsnJournalCatchUpSource` carries drive, cursor, and `maximumBufferReads` at `MFTLib/Broker/Sources/UsnJournalCatchUpSource.cs:10-13`.
- `BlockWriteReporting` contains progress and operation reporting at `MFTLib/Broker/SharedMemory/IBlockSectionWriter.cs:17`; `IBlockSectionWriter.Write` accepts it at `MFTLib/Broker/SharedMemory/IBlockSectionWriter.cs:23-29`.
- The native caller-owned `MftParseControl` contains cancellation and parse-thread allowance fields at `MFTLibNative/mft_api.h:69-76`, and `ParseMFTRecordsWithProgress` accepts it at `MFTLibNative/mft/mft.parse.cpp:288-290`.
- `IElevatedEntryRunner.RunBroker(string?)` matches `MFTLib/Broker/Launch/IElevatedEntryRunner.cs:12-16`.
- `BrokerMftBlockProducer` uses `Func<CancellationToken, Task<BrokerProcess>>`, an optional `Action<BrokerDriveScanResult>`, and constructs the per-drive watch source at `MFTLib/Broker/Client/BrokerMftBlockProducer.cs:11-13` and `MFTLib/Broker/Client/BrokerMftBlockProducer.cs:26-39`.
- `BlockFile.Flush(Action<long>?)` and the 64 MiB range match `MFTLib/Index/BlockFile.Flush.cs:31-44` and `MFTLib/Index/BlockFile.cs:68-69`; `BlockWriter.Complete(DateTime, Action<long>?)` matches `MFTLib/Index/BlockWriter.cs:180-185`.
- `WatchFaultKind` is ordered `Subscriber`, `Drive`, `Apply`, `CatchUpLost`, `Channel`, `Recovery` at `MFTLib/Index/WatchFault.cs:4-50`; `WatchCatchUpState` is ordered `NotStarted`, `CatchingUp`, `CaughtUp`, `Recovering`, `Faulted` at `MFTLib/Index/WatchCatchUpState.cs:8-38`.
- `BrokerFrameKind` is densely numbered from 1 through 17, with `CatchUpLost = 13`, at `MFTLib/Broker/Protocol/BrokerFrame.cs:13-31`.
- `ScanReady` carries three `long` values at `MFTLib/Broker/Protocol/BrokerFrame.cs:218-224`; `BlockScanOutcome` has the stated five-field shape at `MFTLib/Broker/Client/BlockScanOutcome.cs:5-6`.
- Native and managed ABI versions are both 2, and the managed compact-entry stride is 50, at `MFTLibNative/mft_api.h:12` and `MFTLib/Internal/MFTLibNative.cs:12-13`.
- The lost-catch-up limit is 3 at `MFTLib/Index/FileIndex.CatchUp.cs:5-9`, the row-scan cancellation interval is 4096 at `MFTLib/Index/RowScanner.cs:14-17`, and the block flush range is 64 MiB at `MFTLib/Index/BlockFile.cs:68-69`.

### New Breakage in the Fix Diff

- Critical: None.
- Important: None.
- Minor: None.
- RED evidence: the fix diff changes only `CHANGELOG.md` and adds or changes no tests. W40-R1 therefore has no new test from this round to audit, and no test lacks required RED evidence.

### Out-of-Scope Observations

- None.

### Verdict

All findings addressed, no new Critical/Important breakage
