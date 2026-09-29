### Task C1: Wire protocol, channel host and parse-thread allocator (Opus)

Opus because the host becomes concurrent: requests dispatch to their own tasks, channels open through an owned handshake and live and die independently, the parse-thread allocator is shared by every scan, and control-pipe close must cancel every channel and bound the wait.

Larger than the guideline for the same reason as B1: the protocol change breaks the client and everything that wraps it, and the only alternative to deleting them here is a second protocol.

**Files:**
- Rewrite: `MFTLib/Broker/Protocol/BrokerFrame.cs`, `BrokerProtocol.cs`, `BrokerProtocol.Write.cs`, `MFTLib/Broker/Host/JournalBrokerHost.Session.cs` (control loop), `MFTLib/Broker/Host/JournalBrokerHost.Scan.cs` (one drive, no spec parsing), `MFTLib/Broker/Host/JournalBrokerHost.cs` (`StreamWatchAsync` for one channel, no arm epochs), `MFTLib/Broker/Host/JournalBrokerHost.VolumeQuery.cs` (singular), `JournalBrokerHost.GrowUsnJournal.cs` (request id), `MFTLib/Broker/Launch/IElevatedEntryRunner.cs`, `DefaultElevatedEntryRunner.cs`, `ElevatedEntryPoint.cs`, `MFTLib/Broker/Sources/MftRecordBatchSource.cs`, `JournalBatchSource.cs` and `UsnJournalCatchUpSource.cs` (new shapes below), `MFTLib/Broker/Host/JournalBrokerHost.Sources.cs`
- Create: `MFTLib/Broker/Host/JournalBrokerHost.Channel.cs` (serve one drive pipe), `MFTLib/Broker/Host/ParseThreadAllocator.cs`, `MFTLib/Broker/Sources/IBrokerOperationReporter.cs`, `MFTLib/Broker/Host/BrokerChannelConnector.cs` (the public delegate), `MFTLib/Broker/BrokerDriveLetter.cs` (internal `Normalize` and `TryNormalize`, moved from `JournalBrokerClient`), `MFTLib.Tests/TestSupport/InMemoryPipePair.cs`, `MFTLib.Tests/TestSupport/HostChannelHarness.cs` (runs `ServeAsync` over in-memory control and drive pipes and speaks raw frames), `MFTLib.Tests/JournalBrokerHostChannelTests.cs`, `MFTLib.Tests/JournalBrokerHostSourcesTests.cs`
- Delete: `MFTLib/Broker/Client/JournalBrokerClient*.cs` (all 12), `LiveWatchItem.cs`, `NtfsVolumeQueryResult.cs`, `BrokerScanResult.cs`, `BrokerMftBlockProducer.cs`, `BrokerProgressAdapter.cs` (C2 restores what it needs), `MFTLib/Broker/Host/ClientDisconnectedException.cs` if the control loop no longer needs it (keep only if control-reply failure still uses it)
- Delete tests (ported by C3a, C3b, C4, C6): `BrokerProtocolTests.cs` and partials `.ArmEpochFrames`, `.DisarmDriveFrame`, `.Frames`, `.GrowUsnJournalFrames`, `.Scan` (C3b rewrites), every `JournalBrokerHostTests*.cs`, `JournalBrokerHostRealSeamsTests*.cs`, `JournalBrokerHostBlockScanTests.cs`, `GrowUsnJournalHostTests.cs`, `VolumeQueryHostTests.cs` (C3a, C3b), every `JournalBrokerClientTests*.cs`, `GrowUsnJournalClientTests.cs`, `VolumeQueryClientTests.cs`, `BrokerDeathTests.cs`, `BrokerLiveWatchErrorTests.cs`, `BrokerMftBlockProducerTests.cs`, `BrokerMftBlockProducerProtocolTests.cs`, `BrokerBlockContractTests.cs`, `BrokerArmEpochDemuxTests.cs`, `BrokerArmOrderingTests.cs`, `MftProducerEndToEndTests.cs` (C4), `TestSupport/InProcessBlockBrokerHarness.cs`, `ScriptedWatchBrokerHarness.cs`, `WatchSpecArmEpochs.cs`, `SingleReaderGuardStream.cs`, `GateFrameWriteStream.cs`, `CancellableGateFrameWriteStream.cs`, `BrokerBlockTestBase.cs` (C4 rewrites what it needs)
- Modify: `MFTLib.Tests/UsnJournalSyntheticTests.Cancellation.cs` (calls `ServeAsync(server, writer, false, token)` at `:37`; move it to `HostChannelHarness`), `MFTLib.Tests/DefaultElevatedEntryRunnerTests.cs`, `ElevatedEntryPointTests.cs` (C3b finishes them; here only what compiles), `scripts/coverage-linux.sh` (`:72` and `:82` name the renamed real-pipe runner test), `MFTLib/Mft/NtfsVolumeInformation.cs` (`:20` cref), `MFTLib/Index/JournalCheckpointLoss.cs` (`:114` doc mention)

**Wire (spec 2.2).** Framing unchanged: 4-byte little-endian length, kind byte, payload. `BrokerFrameKind` renumbered densely:

| Value | Kind | Pipe | Payload |
|---|---|---|---|
| 1 | `OpenChannel` | control, to host | RequestId, Drive, PipeName |
| 2 | `ChannelOpened` | control, to client | RequestId |
| 3 | `QueryVolume` | control, to host | RequestId, Drive |
| 4 | `VolumeInfo` | control, to client | RequestId, volume fields as today |
| 5 | `GrowUsnJournal` | control, to host | RequestId, Drive, MaximumSize, AllocationDelta |
| 6 | `UsnJournalSettings` | control, to client | RequestId, settings as today |
| 7 | `Error` | any, to client | RequestId (0 on a drive pipe), Message |
| 8 | `Heartbeat` | any, to client | none |
| 9 | `Stalled` | any, to client | Message |
| 10 | `ArmAndScan` | drive, to host | SectionName, Profile, KeepFileNames |
| 11 | `Cursor` | drive, to client | JournalId, NextUsn |
| 12 | `ScanProgress` | drive, to client | as today without drive |
| 13 | `CatchUpLost` | drive, to client | Loss (the proven `JournalCheckpointLoss` fields: Cause, CheckpointUsn, FirstUsn, NextUsn, AllocationDelta, MaximumSize, BytesBehind, SizeThatWouldHaveRetained), Message |
| 14 | `ScanReady` | drive, to client | RowCount, NamePoolUsedBytes, SkippedRecordCount |
| 15 | `JournalBatch` | drive, to client | JournalId, NextUsn, entries |
| 16 | `StartWatch` | drive, to host | JournalId, NextUsn |
| 17 | `CaughtUp` | drive, to client | none |

`BrokerFrame` loses `ArmEpoch`, `NoArmEpoch`, `DrivesSpec` and every drive field on drive-pipe kinds; gains `RequestId`, `PipeName`, `SectionName`. `StartWatch` drive lists, `DisarmDrive`, `EndWatch`, `EndWatchAck`, `Shutdown` and plural `QueryVolumes` are gone.

**Scan frame order (amendments R13 and L1):** `Cursor`, `ScanProgress`\*, `ScanReady`, then exactly one terminal frame: `JournalBatch` when the catch-up held, or `CatchUpLost` when it failed and the live journal proves the armed cursor lost; or `Error` at any point, including a catch-up failure the journal does not prove. `ScanReady` is written before catch-up runs (`JournalBrokerHost.Scan.cs:204-251` at the base commit, the catch-up at `:213-238`); C2's collector accepts exactly this.

**Interfaces produced:**

```csharp
public delegate Task<Stream> BrokerChannelConnector(string pipeName, CancellationToken cancellationToken);
public delegate IEnumerable<IReadOnlyList<MftRecord>> MftRecordBatchSource(string driveLetter, ParseThreadAllowance parseThreads,
    IBrokerOperationReporter operation, IProgress<BlockWriteProgress>? progress, CancellationToken cancellationToken);
public delegate (UsnJournalEntry[] Entries, UsnJournalCursor Updated) UsnJournalCatchUpSource(
    string driveLetter, UsnJournalCursor since, int maximumBufferReads);
public delegate IAsyncEnumerable<(UsnJournalEntry[] Entries, UsnJournalCursor Cursor)> JournalBatchSource(
    string driveLetter, UsnJournalCursor since, IBrokerOperationReporter operation, CancellationToken cancellationToken);

// Amendment R11: how a source tells the host's watchdog what it is doing.
public interface IBrokerOperationReporter
{
    void WaitingOnVolume();            // blocked in a volume read; heartbeats, never stalls
    void Processing(string step);      // working; restarts the processing clock
}

public sealed partial class JournalBrokerHost
{
    public JournalBrokerHost(UsnJournalCursorQuery queryCursor, MftRecordBatchSource scanDrive,
        UsnJournalCatchUpSource readJournal, JournalBatchSource? watchDrive = null,
        NtfsVolumeInformationQuery? queryVolumeInfo = null, GrowUsnJournalQuery? growUsnJournal = null,
        int? processorCount = null, TimeProvider? timeProvider = null); // null: Environment.ProcessorCount, TimeProvider.System
    public Task ServeAsync(Stream control, BrokerChannelConnector connectChannel,
        IBlockSectionWriter? blockSectionWriter, CancellationToken cancellationToken);
    internal static readonly TimeSpan ControlClosedGracePeriod = TimeSpan.FromSeconds(5);
    internal static readonly TimeSpan ChannelConnectTimeout = TimeSpan.FromSeconds(30);   // R5
    internal static readonly TimeSpan FirstRequestTimeout = TimeSpan.FromSeconds(30);     // R5
}
internal sealed class ParseThreadAllocator // amendment S3
{
    public ParseThreadAllocator(int processorCount);
    public int RunningScanCount { get; }
    // Admits the scan while fewer than processorCount scans run, otherwise queues it in arrival order; a cancelled wait leaves the queue.
    public ValueTask<ParseThreadRegistration> AdmitAsync(CancellationToken cancellationToken);
}
internal sealed class ParseThreadRegistration : IDisposable
{
    public ParseThreadAllowance Allowance { get; } // written by the allocator for as long as the scan runs
    public void Dispose();                         // ends the registration once, rebalances the remaining scans, admits queued ones
}
public interface IElevatedEntryRunner { void RunBroker(string? controlPipeName); }
```

The allocator's rule (S3): with `r` running scans and `P` processors (`r <= P` by admission), the scan at admission position `k` (from 0) has an allowance of `P / r`, plus 1 when `k < P % r`; every allowance is at least 1 and the allowances sum to `P`. Every admission and every `Dispose` recomputes the split under one lock and writes each running scan's `ParseThreadAllowance.Count`; the native parser reads it at its next chunk (A1). Nothing computes a share anywhere else.

The production sources report through `IBrokerOperationReporter`: `WatchAndDisposeAsync` reports `WaitingOnVolume` before each `MoveNextAsync` of the native watch and `Processing("journal batch")` after each batch; `ScanDriveRecordBatches` reports `WaitingOnVolume` around the volume open and `Processing` per native progress callback. It passes the scan's `ParseThreadAllowance` and the channel token to `MftVolume.ReadRecordBatches` (A1), which is what lets the rebalance reach the native parser. In C1 the reporter only records the state; C5 wires it to the watchdog.

Host behavior (spec 2.1, 2.3, with amendments S3, R5, R7, R11, L1):

- `ServeAsync` reads control frames; each request runs on its own task; control writes take a control-only `SemaphoreSlim`. A control reply write that finds the pipe gone ends the session; a drive-pipe write that finds its pipe gone ends only that channel quietly (today's `TryWriteFrameAsync`, `JournalBrokerHost.cs:216-229`).
- `OpenChannel` (R5, host half): `connectChannel(pipeName, token)` bounded by `ChannelConnectTimeout` on the injected clock; on success reply `ChannelOpened` and serve the channel on its own tracked task; on failure or timeout reply `Error` with the request id and dispose whatever was connected. The served channel waits for its first request bounded by `FirstRequestTimeout`, then disposes and ends if none arrives.
- A channel reads exactly one request frame (`ArmAndScan` or `StartWatch`; anything else is `Error` and close). A reader task then reads until EOF and cancels the channel token; the operation's completion cancels and joins that reader task.
- Scan (S3): `AdmitAsync(channelToken)` on the host's `ParseThreadAllocator` (a closed pipe leaves the queue), then today's pipeline for one drive with `parseThreads = registration.Allowance`. The registration, the volume and the section writer are held until the native parse has returned (R7: A1's cancellation makes that prompt), then released in that order; teardown never returns the scan's share to the others while native work runs. Frame order per R13.
- Scan chunk: `ScanDriveRecordBatches` queries `NtfsVolumeInformation.Query(drive).BytesPerFileRecordSegment` (the host scan is Windows-only) and opens the volume with the existing `MftVolume.Open(string, uint bufferSizeRecords)` overload, passing `HostScanChunkRecords(...)`, so a progress callback lands at least every 64 MB read. `internal const long HostScanChunkBytes = 64L * 1024 * 1024;` and `internal static uint HostScanChunkRecords(long bytesPerFileRecordSegment)` (`max(1, HostScanChunkBytes / size)`) live in `JournalBrokerHost.Sources.cs`.
- Lost catch-up, proven by the journal (L1, spec 2.3): a catch-up failure other than `OperationCanceledException` (the `catch ... when` at `JournalBrokerHost.Scan.cs:228`) is not classified from the exception, because `MftVolume.ReadUsnJournal` throws one `InvalidOperationException` for a null native result and for any native error text (`MFTLib/Journal/MftVolume.Journal.cs:76-88`). The host runs `JournalCheckpointCheck.Check(drive, armed.JournalId, armed.NextUsn, JournalCheckpointLossDetection.ScanCatchUp)` against the live journal, from the armed cursor of the `Cursor` frame. A returned loss: the host writes `CatchUpLost` carrying that loss and the failure message, and closes. A null answer (the cursor is still retained, or the journal cannot answer): the host writes `Error` with the failure message and closes. In neither case does it re-query the cursor (`:230`), substitute an empty batch (`:236-237`) or write a `JournalBatch`. The block that `ScanReady` announced is complete; the client delivers it after `CatchUpLost` and disposes it after `Error` (C2). The index trusts the frame and does not query the journal again.
- Watch: `StreamWatchAsync` with no epoch; `DescribeWatchFailure` keeps its wording. The host closes its end after a terminal frame.
- Control EOF: cancel every channel, wait for their tasks up to `ControlClosedGracePeriod` on the injected `TimeProvider`, return.
- Diagnostics: control frames log under `BrokerDiagnostics.ControlChannel`, drive frames under `BrokerDiagnostics.DriveChannel(drive, sequence)`; the log filter stays per drive (`BrokerDiagnostics.CreateLogFilter`).
- `DefaultElevatedEntryRunner.RunBroker(controlPipeName)`: connect the control `NamedPipeClientStream`; the connector opens a `NamedPipeClientStream(".", pipeName, InOut, Asynchronous)` and `ConnectAsync(token)`; `ServeAsync(control, connector, new RealBlockSectionWriter(), CancellationToken.None)`; exit 0. `ElevatedEntryPoint.TryHandle` drops `--once`.

- [ ] **Step 1: Failing tests** in `JournalBrokerHostChannelTests` (through `HostChannelHarness`, fake sources, `FakeTimeProvider`):
  - `QueryVolume_RepliesWithRequestId`; `GrowUsnJournal_RepliesWithRequestId`; `QueryVolume_SourceThrows_RepliesErrorWithRequestId`.
  - `ControlRequests_RunConcurrently`: a `QueryVolume` whose source waits on a gate does not delay a second `QueryVolume` reply.
  - `OpenChannel_ConnectsNamedPipeAndReplies`.
  - `ScanChannel_EmitsCursorProgressReadyAndCatchUpInOrderThenCloses`.
  - `ScanChannel_CatchUpFailsAndJournalProvesLoss_EmitsScanReadyThenCatchUpLostAndCloses` (R13, L1): the catch-up source throws `IOException`; the synthetic journal window (journal id 7 armed at next USN 1000; window journal id 7, first USN 5000, next USN 9000, allocation delta 4096, maximum size 32768, supplied through the internal journal override) proves the loss; the frames are `Cursor`, `ScanProgress`\*, `ScanReady`, `CatchUpLost` whose loss has `Cause` `CheckpointTrimmed`, `BytesBehind` 4000 and `SizeThatWouldHaveRetained` 12288, then EOF; no `JournalBatch`; the cursor query was called once.
  - `ScanChannel_CatchUpFailsAndCursorStillRetained_EmitsErrorAfterScanReady` (first USN 500), `ScanChannel_CatchUpFailsAndJournalCannotAnswer_EmitsErrorAfterScanReady` (the override answers null) and `ScanChannel_CatchUpFailsAndJournalRecreated_CatchUpLostHasNoSize` (window journal id 8: `Cause` `JournalRecreated`, `SizeThatWouldHaveRetained` null) (L1).
  - `ScanChannel_CatchUpCancelled_WritesNoCatchUpLost` (L1): an `OperationCanceledException` from the catch-up source ends the channel without a `CatchUpLost` frame.
  - `ScanChannel_AllowanceReachesSource` (S3): host `processorCount: 8`, one scan; the fake source receives a `ParseThreadAllowance` whose `Count` is 8.
  - `ParseThreadAllocator_TwoScans_DivideProcessors` (S3): `processorCount: 8` gives 4 and 4; three scans on 8 give 3, 3 and 2 in admission order (remainder to the earliest); the allowances always sum to 8.
  - `ParseThreadAllocator_ScanStarts_RunningScanIsReducedWithoutItsCooperation` (S3): one scan on `processorCount: 4` holds 4; a second is admitted and the first's `Allowance.Count` reads 2 with the first scan's source doing nothing.
  - `ParseThreadAllocator_ScanEndsCancelsOrFails_RemainingScansAreRaised` (S3): two scans at 2 and 2; the first ends by completing, then (separately) by cancellation, then by its source throwing; each time the other reads 4.
  - `ParseThreadAllocator_NeverOversubscribedAtRest` (S3): for every `processorCount` from 1 to 8 and every scan count from 1 to 12, a fixed sequence of admissions and disposals; after each step the running count is at most `processorCount`, every allowance is at least 1 and the allowances sum to `processorCount` (no random input).
  - `ParseThreadAllocator_MoreScansThanProcessors_ExtrasQueueInArrivalOrder` (S3): `processorCount: 2`, three scans; the third's source is not entered and each running scan holds 1; when the first ends the third is admitted with 1 and the queue order is preserved for a fourth.
  - `QueuedScan_PipeClosed_LeavesQueueAndSourceNeverRuns` (S3): the queued channel's source is never invoked; a later scan is admitted.
  - `ConcurrentScans_TwoChannels_BothInsideSourceAtOnce`: `processorCount: 4`; both entry signals arrive before either gate opens and the two allowances read 2 and 2.
  - `ScanChannel_PipeClosed_ShareReturnsOnlyAfterSourceReturns` (R7): the fake source ignores cancellation until a gate opens; closing the pipe does not raise the other scan's allowance, and a queued scan stays queued, until the gate opens.
  - `ScanChannel_PipeClosed_CancelsOnlyThatScan`: the other channel's scan completes.
  - `OpenChannel_ConnectorNeverConnects_RepliesErrorAfterTimeout` (R5), `OpenChannel_NoFirstRequest_ChannelEndsAfterTimeout` (R5), `OpenChannel_ConnectorThrows_RepliesErrorWithRequestId` (R5).
  - `WatchChannel_StreamsBatchesAndCaughtUp_NoDriveFields`.
  - `WatchChannel_SourceThrows_WritesErrorAndCloses`.
  - `WatchChannel_ClientClosesPipe_EndsQuietly_OtherChannelUnaffected`.
  - `ControlEof_CancelsEveryChannelAndReturnsWithinGracePeriod`: two watch channels blocked in their sources; close control; `ServeAsync` returns after the fake clock advances past the grace period even if a source ignores cancellation.
  - `ControlReplyWrite_BrokenPipe_EndsSessionAndCancelsEveryChannelWithinBound`: a `QueryVolume` reply write throws `IOException`; two watch channels blocked in sources are cancelled; `ServeAsync` returns once the fake clock passes `ControlClosedGracePeriod` even if a source ignores cancellation.
  - `HostScanChunk_OneKilobyteRecords_Is65536` and `HostScanChunk_FourKilobyteRecords_Is16384` (in `JournalBrokerHostSourcesTests`).
  - `DriveChannel_UnknownFirstFrame_WritesErrorAndCloses`.
- [ ] **Step 2: See them fail;** implement; delete and edit the listed files; the build names every remaining caller of a deleted member; rewrite or delete it per the lists above.
- [ ] **Step 3: Verify.** Targeted `JournalBrokerHostChannelTests`; `bash scripts/coverage-linux.sh` locally is not required, but check the filter lines in `scripts/coverage-linux.sh` name only test methods that exist (Grep); whole suite; `aislop scan .`.
- [ ] **Step 4: Commit:** "Broker host serves a control pipe and one pipe per drive operation". List deleted test files and their porting tasks.

**Gate:** green (the client, producer and broker watch source are absent until C2 and C6). **Depends on:** A1, A2, A3, A5. **Parallel with:** A4, B1.

---

