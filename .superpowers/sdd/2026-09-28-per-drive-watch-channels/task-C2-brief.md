### Task C2: BrokerProcess client, scan channels, BrokerTestHarness

**Files:**
- Create: `MFTLib/Broker/Client/BrokerProcess.cs` (fields, events, dispose), `BrokerProcess.Launch.cs`, `BrokerProcess.Control.cs` (control reader, request ids), `BrokerProcess.Channels.cs` (open a drive pipe), `BrokerProcess.Scan.cs`, `MFTLib/Broker/Client/BrokerFrameReader.cs` (reads frames off one pipe; C7 adds the stall limit here), `BrokerChannelLostException.cs`, `BrokerDriveScanResult.cs`, `BrokerBlockSectionFactory.cs`, `BrokerPipes.cs` (internal `IBrokerPipeFactory`, `BrokerPipeListener`, `NamedPipeBrokerPipeFactory`), `MFTLib/Broker/Client/BrokerMftBlockProducer.cs` (scan half), `BrokerProgressAdapter.cs` (restored for one drive), `MFTLibTestExtensions/BrokerTestHarness.cs`, `MFTLibTestExtensions/InMemoryBrokerPipes.cs`, `MFTLib.Tests/BrokerProcessTests.cs`, `MFTLib.Tests/BrokerProcessLaunchTests.cs`
- Modify: `MFTLib/Broker/Client/BrokerScanOptions.cs` (drop `BlockTargets`), `MftBlockCapacity.cs` (if its signature named the client), `MFTLib/Index/MftBlockProducer.cs` (`MftBlockProduceResult.CatchUpLoss`, an init property: set only with a loss the journal proved); create `MFTLibTestExtensions/BrokerTestHarnessOptions.cs`

**Interfaces produced (spec section 3, plus internals):**

```csharp
public sealed class BrokerProcess : IAsyncDisposable
{
    public static readonly TimeSpan DefaultConnectTimeout = TimeSpan.FromSeconds(30);
    internal static readonly TimeSpan ControlReplyTimeout = TimeSpan.FromSeconds(30); // enforced by C7
    [SupportedOSPlatform("windows")] public static Task<BrokerProcess> LaunchAsync(Func<string, bool> launchBroker, CancellationToken cancellationToken);
    [SupportedOSPlatform("windows")] public static Task<BrokerProcess> LaunchAsync(Func<string, bool> launchBroker, TimeSpan connectTimeout, CancellationToken cancellationToken);
    public bool HasEnded { get; }
    public event Action<string>? Ended;          // fires once, from the control reader
    public Task<NtfsVolumeInformation> QueryVolumeAsync(char driveLetter, CancellationToken cancellationToken);
    public Task<UsnJournalSettings> GrowUsnJournalAsync(char driveLetter, long maximumSize, long allocationDelta, CancellationToken cancellationToken);
    public Task<BrokerDriveScanResult> ScanDriveAsync(char driveLetter, BlockScanTarget target, BrokerScanOptions options, CancellationToken cancellationToken);
    public ValueTask DisposeAsync();

    internal BrokerProcess(Stream control, IBrokerPipeFactory pipes, BrokerBlockSectionFactory createBlockSection, TimeProvider timeProvider);
    internal Task<BrokerDriveChannel> OpenChannelAsync(char driveLetter, Action<ArrayBufferWriter<byte>> writeFirstRequest,
        CancellationToken cancellationToken);
}
internal interface IBrokerPipeFactory { BrokerPipeListener Listen(string pipeName); }
internal sealed class BrokerPipeListener : IAsyncDisposable
{
    public string PipeName { get; }
    public Task<Stream> WaitForConnectionAsync(CancellationToken cancellationToken);
}
internal sealed class BrokerDriveChannel : IAsyncDisposable // owns the pipe, the reader, the diagnostics tag
{
    public char DriveLetter { get; }
    public Task WriteAsync(Action<ArrayBufferWriter<byte>> write, CancellationToken cancellationToken);
    public ValueTask<BrokerFrame?> ReadAsync(CancellationToken cancellationToken); // null at EOF
}
// AdvancedCursor is null and CatchUpEntries empty when CatchUpLoss is set. CatchUpLoss is the loss the host proved against the live journal (C1).
public sealed record BrokerDriveScanResult(char DriveLetter, UsnJournalCursor ArmedCursor,
    UsnJournalCursor? AdvancedCursor, IReadOnlyList<UsnJournalEntry> CatchUpEntries,
    JournalCheckpointLoss? CatchUpLoss, BlockScanOutcome Block);
public sealed record BrokerScanOptions
{
    public BrokerScanProfile Profile { get; init; } = BrokerScanProfile.Full;
    public IReadOnlyCollection<string>? KeepFileNames { get; init; }
    public IProgress<BrokerScanProgress>? Progress { get; init; }
}
public sealed class BrokerChannelLostException : IOException
{
    public BrokerChannelLostException(char? driveLetter, string message, Exception? innerException = null);
    public char? DriveLetter { get; }
}
public delegate (string SectionName, BlockFile Block, IDisposable Lifetime) BrokerBlockSectionFactory(
    char driveLetter, BlockFileCreateOptions options);
public sealed class BrokerMftBlockProducer
{
    public BrokerMftBlockProducer(Func<CancellationToken, Task<BrokerProcess>> connectAsync,
        BrokerScanOptions? scanOptions = null, Action<BrokerDriveScanResult>? scanCompleted = null);
    public MftBlockProducer CreateProducer();
    // CreateWatchSource returns in C6.
}
namespace MFTLib.Index;
// MftBlockProduceResult gains one init property (spec 2.6.6): a producer sets it only with a loss the journal proved.
//     public JournalCheckpointLoss? CatchUpLoss { get; init; }   // a proven loss only
namespace MFTLibTestExtensions;
public static class BrokerTestHarness
{
    public static BrokerProcess StartInProcess(JournalBrokerHost host, IBlockSectionWriter blockSectionWriter,
        BrokerBlockSectionFactory createBlockSection);
    public static BrokerProcess StartInProcess(JournalBrokerHost host, IBlockSectionWriter blockSectionWriter,
        BrokerBlockSectionFactory createBlockSection, BrokerTestHarnessOptions options);
}
// Amendment R11. The host's own clock and processor count go through the JournalBrokerHost constructor.
public sealed record BrokerTestHarnessOptions
{
    public TimeProvider TimeProvider { get; init; } = TimeProvider.System;        // client side: stall limit, reply timeouts
    public Func<string, Exception?>? FailConnection { get; init; }               // by pipe name: the host's connect throws this
    public Func<string, Task>? HoldWrites { get; init; }                         // by pipe name ("control" or a drive pipe): host writes wait on the returned task
}
```

`BrokerMftBlockProducer`'s `_connectAsync` field changes type here, from `Func<CancellationToken, Task<JournalBrokerClient>>` (`BrokerMftBlockProducer.cs:11` at the base commit) to `Func<CancellationToken, Task<BrokerProcess>>`; C6 restores `CreateWatchSource` over the same field.

Behavior (spec 2.1, 2.4, with amendments R5, R6, R12, R13, L1):

- Drive pipe names: `mftlib-broker-{guid}-{drive}-{sequence}`, the guid shared with the control pipe; the sequence is per process, starting at 1.
- Request ids (R12): allocated under the pending-request lock from a `uint` counter that skips 0 and every id still in the pending table (outstanding or abandoned); a full table (every nonzero id retained) throws `InvalidOperationException("No broker request id is free")`. An entry is removed when its reply arrives or when the process ends, never earlier. The allocator has two internal seams: `internal Func<uint>? StartingRequestIdForTest` so tests start near `uint.MaxValue`, and `internal uint? MaximumRequestIdForTest` so a test can shrink the id space to three ids instead of creating billions of live requests.
- Control writes (R6): a request's cancellation token is observed only before its frame starts writing. Once writing starts, the frame is finished under a process-owned token bounded by `ControlReplyTimeout`; if the write fails or times out, the control connection is ended loudly (`Ended` fires, every pending request fails with `BrokerChannelLostException(null, ...)`). Only a fully written request uses the late-reply discard rule when its caller stops waiting.
- `OpenChannelAsync` (R5, client half): one owned operation: create the listener, register the request id, write `OpenChannel`, then await the host connection and `ChannelOpened` in either order, then write the first request frame (the caller's `ArmAndScan` or `StartWatch`, passed in). Every failed branch (cancellation, `Error` reply, connection without acknowledgement, acknowledgement without connection within `ControlReplyTimeout`, first-write failure) disposes the listener and any connected stream, so the host observes EOF on whatever it holds; an `Error` reply throws `InvalidOperationException` with the host message; control loss throws `BrokerChannelLostException(drive, ...)`.
- Control reader: one task; replies complete the pending request whose id matches; a reply with no pending entry is dropped; `Heartbeat` is ignored here (C7 counts it for the stall limit); EOF or I/O error fails every pending request with `BrokerChannelLostException(null, ...)`, releases every retained entry, sets `HasEnded`, raises `Ended` once.
- `ScanDriveAsync`: `QueryVolumeAsync`, then today's `PrepareDriveBlock` (`JournalBrokerClient.BlockScan.cs:31-72` at the base commit), open a channel whose first frame is `ArmAndScan`, then read in the R13 order: `Cursor`, `ScanProgress`\* (to `options.Progress`), `ScanReady`, then one terminal frame; any other order is a protocol error (`BrokerChannelLostException`). A terminal `JournalBatch` closes the channel and returns the result with the block transferred to the caller. A terminal `CatchUpLost` (L1) closes the channel and returns the same result with the block transferred to the caller, `AdvancedCursor` null, `CatchUpEntries` empty and `CatchUpLoss` set to the proven loss; the block is complete, so the caller decides what to do with it. `BrokerMftBlockProducer.CreateProducer` copies the loss to `MftBlockProduceResult.CatchUpLoss`. An `Error` frame after `ScanReady` fails the scan like any other `Error`: the section is disposed and no block is returned. `Error` frame: `InvalidOperationException(message)`. EOF or I/O before the terminal frame: `BrokerChannelLostException(drive, ...)`. Cancellation: dispose the channel and the section, throw `OperationCanceledException`.
- `DisposeAsync`: close control (the host ends every channel), dispose open channels, wait for the control reader.
- `BrokerTestHarness.StartInProcess`: in-memory control pair plus an `InMemoryBrokerPipes` registry that implements both `IBrokerPipeFactory` (client side) and a `BrokerChannelConnector` (host side) by pipe name; runs `host.ServeAsync` on a background task; the returned process's disposal ends the host and awaits it.

- [ ] **Step 1: Failing tests** in `BrokerProcessTests` (in-process through the harness; fake host sources; `RecordingBlockSectionWriter` from `TestSupport`):
  - `QueryVolume_ReturnsVolumeInformation`; `GrowUsnJournal_ReturnsSettings`; `QueryVolume_HostError_ThrowsInvalidOperationWithMessage`.
  - `ControlRequestIds_CancelledWaitDropsLateReply_NextRequestSucceeds` (spec 9, "Control request ids": the first query's source waits on a gate; cancel the caller; release; the next query returns its own answer).
  - `RequestIds_WrapAround_SkipZeroAndOutstanding` (R12): allocator started at `uint.MaxValue - 1` with id 1 still pending; the next ids are `uint.MaxValue`, then 2.
  - `RequestIds_SpaceExhausted_ThrowsInvalidOperationAndOtherRequestsUnaffected` (R12): `MaximumRequestIdForTest` 3 and three requests pending; the fourth throws `InvalidOperationException("No broker request id is free")`; releasing one reply lets a fifth succeed.
  - `RequestIds_ReleasedOnProcessEnd` (R12).
  - `ControlWrite_CancelledMidFrame_FinishesFrame_NextRequestSucceeds` (R6): `HoldWrites` holds the client's control write stream after the length prefix (through an in-memory stream wrapper); the caller cancels; the frame still completes when released; the next request is answered.
  - `ControlWrite_FailsMidFrame_EndsProcessLoudly` (R6): the wrapper throws after the length prefix; `Ended` fires; pending requests fail with `BrokerChannelLostException`.
  - `OpenChannel_CancelledBeforeHostConnects_HostSeesNoChannel` (R5), `OpenChannel_ConnectedButErrorReply_ClientDisposesStream_HostChannelEnds` (R5), `OpenChannel_FailConnection_ThrowsWithHostMessage` (R5), `OpenChannel_FirstRequestWriteFails_DisposesBothSides` (R5).
  - `ScanDrive_CatchUpLost_ReturnsBlockWithLossAndArmedCursor` (L1): the host's catch-up source throws and the synthetic window proves the loss; the result has the block, `ArmedCursor` equal to the `Cursor` frame, `AdvancedCursor` null, empty `CatchUpEntries` and a `CatchUpLoss` equal to the host's; the host channel ends and the section stays alive for the caller.
  - `ScanDrive_ErrorAfterScanReady_DisposesSectionAndReturnsNoBlock` (L1): the catch-up failure is not proven (cursor retained); the scan throws `InvalidOperationException` with the host message and the section is disposed.
  - `ScanDrive_CatchUpLostBeforeScanReady_IsProtocolError` and `ScanDrive_JournalBatchAfterCatchUpLost_IsProtocolError` (R13, L1).
  - `Producer_CatchUpLost_CopiesLossToProduceResult` (L1): through `BrokerMftBlockProducer.CreateProducer`; `MftBlockProduceResult.CatchUpLoss` equals the host's loss and the block is present.
  - `ScanDrive_ReturnsArmedAndAdvancedCursorsAndBlock`.
  - `ScanDrive_ReportsProgress`.
  - `ScanDrive_HostError_ThrowsInvalidOperation`.
  - `ScanDrive_Cancelled_ClosesChannelAndDisposesSection_SourceObservesCancellation`; `ScanDrive_CancelOne_OtherDriveCompletes` (spec 9, "Scan cancellation").
  - `ScanDrive_TwoDrivesConcurrently_BothComplete`.
  - `HostEnds_EveryPendingRequestFailsWithChannelLost_EndedFiresOnce`.
  - `Dispose_EndsHost`.
  - `LaunchAsync_LaunchDeclined_Throws`, `LaunchAsync_NeverConnects_TimesOut` (ported from `JournalBrokerClientTests.ConnectionAndWatchFailures.cs:12` and `:58` at the base commit; these open a real named pipe and are Windows-only).
  - In `BrokerProcessLaunchTests`, ported from `JournalBrokerClientTests.BlockSectionsAndProgress.cs:13-183` and `ConnectionAndWatchFailures.cs:30-139`: `LaunchAsync_DiagEnvVarSet_AppendsDiagFlag`, `LaunchAsync_BrokerDiagnosticsEnabledProgrammatically_AppendsDiagFlag`, `LaunchAsync_RelativeLogDirectory_ForwardsResolvedFullPath`, `LaunchAsync_IncludeSelfEnvVarSet_AppendsIncludeSelfFlag`, `LaunchAsync_IncludeSelfSetProgrammatically_AppendsIncludeSelfFlag`, `LaunchAsync_DiagEnvVarUnset_OmitsAllDiagnosticsFlags`, `LaunchAsync_EndToEnd_UsesRealPipeAndRealBlockSeams` (`:185`), `LaunchAsync_NullLaunchBroker_ThrowsArgumentNull`, `LaunchAsync_NegativeTimeout_ThrowsArgumentOutOfRange`, `LaunchAsync_DefaultTimeoutOverridden_TimesOut`, `LaunchAsync_CallerCancellationRequested_ThrowsOperationCanceled`, `DisposeAsync_ControlPipeAlreadyClosed_DoesNotThrow` and `DisposeAsync_CalledTwice_DoesNotThrow`.
- [ ] **Step 2: See them fail;** implement.
- [ ] **Step 3: Verify** targeted; whole suite; `aislop scan .`.
- [ ] **Step 4: Commit:** "BrokerProcess owns the control pipe and scans each drive on its own channel".

**Gate:** green. **Depends on:** C1. **Parallel with:** B2, B3, B4, C3a, C3b.

