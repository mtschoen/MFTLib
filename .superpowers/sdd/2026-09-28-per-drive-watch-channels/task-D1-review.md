### Plan Compliance
- Issues found: `CHANGELOG.md` overstates watch-start readiness (`CHANGELOG.md:82`), gives the wrong current block format (`CHANGELOG.md:92`), retains a non-compiling `ReadRecordBatches` signature (`CHANGELOG.md:161`), and uses prohibited history narration outside a deletion entry (`CHANGELOG.md:80`; `.superpowers/sdd/2026-09-28-per-drive-watch-channels/task-D1-brief.md:15`).
- Cannot verify from diff: the fresh aislop result. The implementer reports 99/100 with only the four baseline warnings and the ruled constructor warning (`.superpowers/sdd/2026-09-28-per-drive-watch-channels/task-D1-report.md:76`), but no scan artifact is present and this read-only review cannot run a scan that writes repository state.

### Strengths

- The AGENTS rewrite captures the specified per-drive model without leaving session, arm/disarm, merged-stream, or shared-pipe descriptions outside the allowed CHANGELOG removal entries. The readiness sentence matches the broker implementation: `AGENTS.md:287-288` says the call returns after the channel connects and `StartWatch` is written, while `BrokerIndexWatchSource.StartAsync` connects and awaits `OpenWatchChannelAsync` (`MFTLib/Broker/Client/BrokerIndexWatchSource.cs:23-32`) and the channel path writes the first request before returning (`MFTLib/Broker/Client/BrokerProcess.Channels.cs:25-41`).
- Recovery, lost catch-up, concurrent open, reentrancy, lock order, liveness, and harness fault propagation are represented with useful operational detail (`AGENTS.md:262-340`, `AGENTS.md:385-417`). Representative source checks agree: lost catch-ups increment and reset per drive (`MFTLib/Index/FileIndex.Publication.cs:77-85`), drive/apply faults queue recovery while a second pre-catch-up fault becomes `Recovery` (`MFTLib/Index/FileIndex.WatchPump.cs:216-222`), open callbacks report outside the state lock (`MFTLib/Index/FileIndex.Scanning.cs:46-71`), heartbeat decisions implement the ruled idle/queued/processing behavior (`MFTLib/Broker/Host/HostPipeWriter.cs:130-200`), and harness host faults close production pipe surfaces (`MFTLibTestExtensions/BrokerTestHarness.cs:52-75`).
- Every deleted identifier required by the brief appears only in explicit CHANGELOG removal entries (`CHANGELOG.md:100-108`), and neither edited document contains an en-dash or em-dash.

### Issues
#### Critical (Must Fix)

- None.

#### Important (Should Fix)

- `CHANGELOG.md:82` promises that `StartWatchingAsync(X)` returns when its pump "is reading" the handle. The implementation returns as soon as `TryPublishHandle` queues `PumpAsync` with `Task.Run` (`MFTLib/Index/FileIndex.WatchDrive.cs:244-246`, `MFTLib/Index/FileIndex.WatchDrive.cs:350-365`); the first read occurs later in the queued pump (`MFTLib/Index/FileIndex.WatchPump.cs:19-24`). This is stronger than both the code and the required contract. State only that the ready handle is published after X's channel is connected and `StartWatch` is written.
- `CHANGELOG.md:92` says the current block format is version 2 with a 104-byte declared header. HEAD defines format version 3 and 112 header-field bytes (`MFTLib/Index/BlockLayout.cs:13-19`), with cache-tag fields extending the header at offsets 104 and 108 (`MFTLib/Index/BlockHeader.cs:45-47`). Consumers relying on the changelog would expect the wrong cache format.
- `CHANGELOG.md:161` still presents `MftVolume.ReadRecordBatches(bool resolvePaths = false, int batchSize = 4096)`. HEAD has no such overload or defaults: the public method requires `bool resolvePaths, int batchSize, IProgress<MftScanProgress>? progress, ParseThreadAllowance? parseThreads, CancellationToken cancellationToken` (`MFTLib/Mft/MftVolume.cs:68-74`). The inline sample does not compile against HEAD and directly misses the task's required `ReadRecordBatches` shape.
- `CHANGELOG.md:80` says operation frames "no longer carry" drive or arm-generation fields. The brief requires target-state prose with no history notes outside explicit deletion entries (`.superpowers/sdd/2026-09-28-per-drive-watch-channels/task-D1-brief.md:15`; `.superpowers/sdd/2026-09-28-per-drive-watch-channels/docs-common.md:11-13`). Rewrite this Changed entry as the current wire contract, for example that the drive channel supplies drive scope and its operation frames contain no arm generation.

#### Minor (Nice to Have)

- None.

### Assessment
Task quality: Needs fixes
Reasoning: The AGENTS rewrite is strong and the core per-drive behavior is well covered, but the changelog contains three material current-state inaccuracies plus one explicit writing-rule violation. The documentation cannot be trusted as the HEAD contract until those entries are corrected.
