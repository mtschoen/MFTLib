# Task C1 report: wire protocol, channel host and parse-thread allocator

Worktree `C:\Users\mtsch\MFTLib-worktrees\265-C1`, branch `task/265-C1`, base `305fab2e` (verified).
Commit: `50bda63` "Broker host serves a control pipe and one pipe per drive operation".

## What was implemented

- **Wire** (`MFTLib/Broker/Protocol/`): `BrokerFrameKind` renumbered 1..17 exactly as the brief's table.
  `BrokerFrame` lost `ArmEpoch`, `NoArmEpoch`, `DrivesSpec`, `MmfName` and every drive field on
  drive-pipe kinds; gained `RequestId`, `PipeName`, `SectionName`, `Profile`, and an internal
  `CatchUpLoss` (`BrokerCatchUpLoss`, the proven loss fields) read back through
  `internal JournalCheckpointLoss RequireCatchUpLoss(char driveLetter)` (stamps the channel's drive
  and `ScanCatchUp`). Payload primitives live in a new `BrokerProtocol.Payload.cs`
  (`PayloadWriter` / `PayloadReader`), so every write/read is a short chain. Nullable longs on
  `CatchUpLost` are a presence byte plus int64. `ScanProgress` carries no drive: a read frame's
  `Progress.DriveLetter` is `string.Empty` (the channel owns the drive; C2 fills it in).
  Control replies (`VolumeInfo`, `UsnJournalSettings`, `Error`, `ChannelOpened`) carry the request
  id and no drive.
- **Interfaces produced** as the brief lists: `BrokerChannelConnector`, `MftRecordBatchSource`
  (allowance, reporter, progress, token), `JournalBatchSource` (reporter), `IBrokerOperationReporter`,
  `JournalBrokerHost` 8-parameter constructor, `ServeAsync(control, connectChannel, writer, token)`,
  the three internal timeouts, `ParseThreadAllocator` (`RunningScanCount`, `AdmitAsync`),
  `ParseThreadRegistration` (`Allowance`, `Dispose`), `IElevatedEntryRunner.RunBroker(string?)`.
  `UsnJournalCatchUpSource` keeps two arguments (ruling R-W2-1).
  `BrokerDriveLetter.Normalize` / `TryNormalize` moved from the client.
- **Host** (`MFTLib/Broker/Host/`):
  - `JournalBrokerHost.Session.cs`: control read loop; each request is its own task (forced
    yield off the read loop, tracked); control writes under a control-only `SemaphoreSlim`; a
    control reply that finds the pipe gone (`ClientDisconnectedException`) ends the session; on EOF,
    caller cancellation or that failure the session token is cancelled and `DrainAsync` waits for
    all tracked request and channel tasks (re-snapshotting for tasks tracked while waiting) until
    `ControlClosedGracePeriod` on the injected clock, then returns. A faulted tracked task rethrows.
  - `JournalBrokerHost.Channel.cs`: `OpenChannel` connects through the connector (a sync throw is
    turned into a faulted connection via `Task.Run`), bounded by `ChannelConnectTimeout`; failure or
    timeout replies `Error` with the request id and disposes a late connection; success replies
    `ChannelOpened` and tracks `ServeChannelAsync`. The channel reads one request bounded by
    `FirstRequestTimeout` (timeout: `Error`, close; malformed: `Error`, close; unknown kind:
    `Error`, close), then an EOF reader task cancels the channel token while the operation runs;
    the operation's completion stops and joins the reader. Any cancellation or a broken drive-pipe
    write ends only that channel, quietly.
  - `JournalBrokerHost.Scan.cs`: `Queued` published, `AdmitAsync(channelToken)`, then the retained
    pipeline for one drive with `parseThreads = registration.Allowance`. Cursor is written before
    the source is invoked (so a source that reports progress eagerly cannot precede it). The
    registration is disposed only after the pipeline (source enumeration inside the section
    writer, then the section) has returned; completion frames are written after. Catch-up failure
    (not `OperationCanceledException`): `JournalCheckpointCheck.Check(..., ScanCatchUp)` on the
    armed cursor; loss -> `CatchUpLost(loss, message)`, null -> `Error(message)`; no re-query, no
    empty batch.
  - `JournalBrokerHost.cs`: constructor, timeouts, `StreamWatchAsync` per channel without epochs;
    `DescribeWatchFailure` keeps its wording.
  - `JournalBrokerHost.Frames.cs`: frame IO with per-channel diagnostics tags
    (`ControlChannel`, `DriveChannel(drive, sequence)`; sequence counted per drive per session).
  - `JournalBrokerHost.VolumeQuery.cs`, `.GrowUsnJournal.cs`: one reply per request id.
  - `JournalBrokerHost.Sources.cs`: `CreateDefault()` (plus internal `CreateDefault(TimeProvider?)`
    used by the synthetic cancellation test), `HostScanChunkBytes`, `HostScanChunkRecords`,
    `ScanDriveRecordBatches` (reports `WaitingOnVolume` around volume query/open, `Processing` per
    native progress callback, sizes the volume buffer from `BytesPerFileRecordSegment`, passes
    allowance and token to `ReadRecordBatches`), `WatchAndDisposeAsync` (reports `WaitingOnVolume`
    before each `MoveNextAsync`, `Processing("journal batch")` after each batch).
  - `ParseThreadAllocator.cs`: rule S3 under one lock; queued waiters leave the queue on
    cancellation; admitted waiters are completed outside the lock. `QueuedScanCount` (internal) and
    `JournalBrokerHost.ParseThreads` (internal) exist for the queue tests.
  - `BrokerOperationState.cs`: the C1 reporter; records phase, step and timestamp only (C5 wires it).
  - `ClientDisconnectedException` kept: control-reply failure still uses it (now also drive-pipe
    write failure, caught per channel), and it carries the `IOException`.
- **Launch**: `DefaultElevatedEntryRunner.RunBroker(controlPipeName)` connects the control pipe,
  serves with `ConnectDrivePipeAsync` (internal static, `NamedPipeClientStream` + `ConnectAsync`),
  then flushes diagnostics with a bounded 2 s wait (`BrokerDiagnostics.FlushAsync(...).Wait(timeout)`;
  `FlushForTestAsync` renamed `FlushAsync` since production now uses it) and exits 0.
  `ElevatedEntryPoint.TryHandle` drops `--once`.
- **Deleted**: all 12 `JournalBrokerClient*.cs`, `LiveWatchItem.cs`, `NtfsVolumeQueryResult.cs`,
  `BrokerScanResult.cs`, `BrokerMftBlockProducer.cs`, `BrokerProgressAdapter.cs`,
  `JournalBrokerHost.BlockScan.cs` (folded into Scan.cs), and every test file the brief lists
  (46 files, named one per line in the commit message). `.editorconfig` section for the deleted
  `JournalBrokerClient.cs` removed.
- **Modified per brief**: `UsnJournalSyntheticTests.Cancellation.cs` (`EndWatch_...` became
  `CloseWatchPipe_IdleNativeRead_EndsChannel`, through `HostChannelHarness` on a `FakeTimeProvider`
  host so the session can only end once the channel really stopped), `DefaultElevatedEntryRunnerTests`
  (null-pipe case kept; real-pipe case renamed `..._ServesUntilControlEof_ExitsWithCode0`; the two
  client-disconnect cases removed for C3b), `ElevatedEntryPointTests` (no `--once`),
  `scripts/coverage-linux.sh` (`:72`, `:82` renamed), `NtfsVolumeInformation.cs:20` cref,
  `JournalCheckpointLoss.cs:114` doc mention.
- **Test support**: `InMemoryPipePair`, `HostChannelHarness` (in-memory control and drive pipes,
  raw frames, optional `BrokenPipeStream` control). `MFTLib.Tests.csproj` gains
  `Microsoft.Extensions.TimeProvider.Testing` 10.10.0.

## Additions to the brief's file list (orchestrator ruling)

- `MFTLib/Index/JournalCheckpointLoss.cs`: `JournalCheckpointLossDetection.ScanCatchUp` added after
  `LiveWatch` with a doc comment (proven by the broker host against the live journal when a scan's
  catch-up failed), plus the `:114` doc fix. Only file under `MFTLib/Index` touched.
  `NamespaceBoundaryTests` green (in the targeted run below and the whole suite).
- `MFTLib/Broker/Protocol/BrokerProtocol.Payload.cs`, `MFTLib/Broker/Host/JournalBrokerHost.Frames.cs`,
  `MFTLib/Broker/Host/BrokerOperationState.cs`: new partials/types to keep files small.
- Test partials `JournalBrokerHostChannelTests.Watch.cs`, `.Scan.cs`, `.Allocation.cs`.

## TDD evidence

Honest account: the named tests were written against the new API, which had to exist to compile,
so the implementation and tests landed in the same pass. RED was then demonstrated by mutating the
implementation behind each behavior and running the class; every mutation was reverted afterwards.

RED A (inline sequential control dispatch; catch-up loss ignored; no first-request bound; drain
without a deadline), `dotnet test ... --filter "FullyQualifiedName~JournalBrokerHostChannelTests"`:
```
Failed ControlRequests_RunConcurrently [10 s]            (second reply blocked behind the gated first)
Failed OpenChannel_NoFirstRequest_ChannelEndsAfterTimeout [10 s]
Failed ScanChannel_CatchUpFailsAndJournalProvesLoss_EmitsScanReadyThenCatchUpLostAndCloses
Failed ScanChannel_CatchUpFailsAndJournalRecreated_CatchUpLostHasNoSize
Failed ControlEof_CancelsEveryChannelAndReturnsWithinGracePeriod [10 s]
Failed ControlReplyWrite_BrokenPipe_EndsSessionAndCancelsEveryChannelWithinBound [10 s]
Failed!  - Failed: 6, Passed: 26
```
RED B (rebalance gives every scan 1 thread; a closed pipe returns the share immediately via a
token registration):
```
Failed ParseThreadAllocator_TwoScans_DivideProcessors
Failed ParseThreadAllocator_ScanStarts_RunningScanIsReducedWithoutItsCooperation
Failed ParseThreadAllocator_ScanEndsCancelsOrFails_RemainingScansAreRaised (Completes|Cancelled|SourceThrows)
Failed ParseThreadAllocator_NeverOversubscribedAtRest
Failed ConcurrentScans_TwoChannels_BothInsideSourceAtOnce
Failed ScanChannel_PipeClosed_ShareReturnsOnlyAfterSourceReturns [5 s]
Failed ScanChannel_AllowanceReachesSource
Failed!  - Failed: 9, Passed: 23
```
One real defect found by the first green run: `ScanChannel_CatchUpCancelled_WritesNoCatchUpLost`
faulted the session because the channel only swallowed cancellation of its own token; the channel
now ends quietly on any `OperationCanceledException`.

GREEN (final code), run twice:
```
dotnet test MFTLib.Tests\MFTLib.Tests.csproj -c Release -p:Platform=x64 --no-build --filter
  "FullyQualifiedName~JournalBrokerHostChannelTests|FullyQualifiedName~JournalBrokerHostSourcesTests|
   FullyQualifiedName~ElevatedEntry|FullyQualifiedName~UsnJournalSyntheticTests|
   FullyQualifiedName~NamespaceBoundary|FullyQualifiedName~NativeSeamIsolation"
Passed!  - Failed: 0, Passed: 111, Skipped: 0, Total: 111
```
Every brief-named test exists with its exact name, plus `ScanChannel_ForwardsProfileAndKeepNamesToSectionWriter`,
`HostScanChunk_RecordLargerThanChunk_IsOneRecord`, `HostScanChunk_NoRecordSize_Throws`,
`OperationState_RecordsEachPublicationAndWhenItHappened`. `MoreScansThanProcessors` and
`NeverOversubscribedAtRest` run on the allocator directly; the host-level "source not entered while
queued" half is asserted in `ScanChannel_PipeClosed_ShareReturnsOnlyAfterSourceReturns` and
`QueuedScan_PipeClosed_LeavesQueueAndSourceNeverRuns`.

## Whole suite

`.\scripts\run-coverage.ps1 -NonInteractive` (final code):
```
Passed: 1424   Skipped: 6   (0 failed)
Line coverage: 96.2%   Branch coverage: 92.6% (2059 of 2222)
exit 0
```
New-class coverage from the first full run: `ParseThreadAllocator` 96.3%, `ParseThreadRegistration`
100%, `BrokerOperationState` 100%, `BrokerFrame` 96.5%, `BrokerProtocol` 94.7%, `JournalBrokerHost`
84.1% (production sources need real volumes), `BrokerDriveLetter` 78.5% (`Normalize` failure path
and `\\.\` prefix are exercised only by client tests C2 restores), `DefaultElevatedEntryRunner` 88.4%.

## aislop

`aislop scan .` final: 98/100, 0 errors, 8 warnings:
- Baseline four: `NativeSeamIsolationFixtures.cs:73`, `:79`; `CachedBlockDeletionOutcome.cs:8`, `:10`.
- `JournalBrokerHost.cs:43` too many parameters (8): reported per ruling R-W2-2, not restructured.
- `BlockScanOutcome.SectionName`, `BlockScanTarget.Path` never accessed: their only readers were the
  deleted client; the spec keeps both types, C2 reads them again.
- `MFTLib/Index/BlockFile.cs:76` `DeleteOnClose.get` never used: its readers were deleted tests /
  client code; the file is B1's, so I did not touch it.

## Deviations and concerns

1. `IBrokerOperationReporter.Processing(string step)` is `Processing(string stepName)`: `step`
   raised CA1716 (reserved keyword `Step` in VB) as the build's only warning and an aislop finding.
   Positional callers are unaffected; say if you want the brief's exact name back.
2. `ScanProgress` frames read off the wire carry `DriveLetter = string.Empty` (the drive is not on
   the wire); C2's collector must set it from the channel.
3. `CatchUpLost` loss is exposed as an internal `BrokerCatchUpLoss` plus
   `BrokerFrame.RequireCatchUpLoss(char)`, because `JournalCheckpointLoss` requires a drive the
   wire does not carry. `WriteCatchUpLost` and `BrokerFrame.CatchUpLost` are internal for the same
   reason.
4. Session disposal with abandoned tasks: after the grace period, `ServeAsync` disposes the session
   even if a task still runs. The session's token is captured as a struct, `EndAsync` skips an
   already cancelled token, and control writes check the token before taking the lock, so an
   abandoned task cannot touch the disposed CTS or semaphore. Channels do not use the session lock.
5. `OpenChannel_ConnectsNamedPipeAndReplies` uses a real named pipe and is not excluded on Linux;
   .NET named pipes work there (Unix domain sockets), but it has not been run on Linux.
6. Diagnostics flush: added on the runner's orderly exit with a 2 s bound; lines still queued
   after that are lost with the process.

## Adjacent (not fixed)

- `scripts/coverage-linux.sh` filter names one test that does not exist:
  `MockVolumeTests.GetVolumeHandle_InvalidVolume_ThrowsIOException` (the class has
  `GetVolumeHandle_InvalidHandle_ThrowsIOException`). Pre-existing; a `!=` filter on a missing name
  excludes nothing. Every other filter line, including the renamed runner test, names an existing
  method.
- `.editorconfig` still has sections for the deleted `JournalBrokerScanSession.*.cs` (A3's deletion).
- `README.md` and `AGENTS.md` still describe `JournalBrokerClient`, `BrokerScanResult`, `--once`
  and the old frames; the docs tasks (D1 to D3) own them.

## Files changed

Production: `MFTLib/Broker/BrokerDiagnostics.cs`, `BrokerDriveLetter.cs` (new),
`Host/BrokerChannelConnector.cs` (new), `Host/BrokerOperationState.cs` (new),
`Host/ClientDisconnectedException.cs`, `Host/JournalBrokerHost.cs`, `.Channel.cs` (new),
`.Frames.cs` (new), `.GrowUsnJournal.cs`, `.Progress.cs`, `.Scan.cs`, `.Session.cs`, `.Sources.cs`,
`.VolumeQuery.cs`, `Host/ParseThreadAllocator.cs` (new), `Launch/DefaultElevatedEntryRunner.cs`,
`Launch/ElevatedEntryPoint.cs`, `Launch/IElevatedEntryRunner.cs`, `Protocol/BrokerFrame.cs`,
`Protocol/BrokerProtocol.cs`, `Protocol/BrokerProtocol.Payload.cs` (new), `Protocol/BrokerProtocol.Write.cs`,
`Sources/IBrokerOperationReporter.cs` (new), `Sources/JournalBatchSource.cs`,
`Sources/MftRecordBatchSource.cs`, `MFTLib/Index/JournalCheckpointLoss.cs`,
`MFTLib/Mft/NtfsVolumeInformation.cs`, `.editorconfig`, `scripts/coverage-linux.sh`.
Tests: `MFTLib.Tests.csproj`, `JournalBrokerHostChannelTests.cs` (+ `.Watch`, `.Scan`, `.Allocation`),
`JournalBrokerHostSourcesTests.cs`, `TestSupport/HostChannelHarness.cs`, `TestSupport/InMemoryPipePair.cs`,
`BrokerDiagnosticsTests.cs`, `DefaultElevatedEntryRunnerTests.cs`, `ElevatedEntryPointTests.cs`,
`UsnJournalSyntheticTests.Cancellation.cs`. Deletions as listed in the commit message.

## Elevation

Nothing I ran or wrote can start an elevated process.

- Commands: native MSBuild, `dotnet build`, `.\init.ps1`, targeted `dotnet test` runs, `aislop scan .`,
  and `.\scripts\run-coverage.ps1 -NonInteractive` twice (logs `.superpowers\coverage.log`, done 00:29,
  and `.superpowers\coverage2.log`, done about 00:36). With `-NonInteractive` the script runs only
  `TestCategory!=RequiresAdmin` and skips its `Start-Process -Verb RunAs` branch (script lines 92-104
  against 154). I never ran it without `-NonInteractive`, and never ran `native-coverage*.ps1`.
- The targeted filters named `JournalBrokerHostChannelTests`, `JournalBrokerHostSourcesTests`,
  `ElevatedEntry` (matches `ElevatedEntryPointTests` and `DefaultElevatedEntryRunnerTests` only),
  `UsnJournalSyntheticTests`, `NamespaceBoundary` and `NativeSeamIsolation`. None of these classes
  carries `RequiresAdmin` (`UsnJournalSyntheticTests.cs:16` mentions it only in a comment).
- The code: the only `Verb = "runas"` sites are `BrokerLauncher.cs:44` and `ElevationUtilities.cs:113`.
  C1 does not call or change either. `DefaultElevatedEntryRunner.RunBroker` is the child side of the
  broker: it connects to a named pipe and never starts a process. Its tests replace `_exitProcess`.
  `RunBroker_ValidPipeName_..._ServesUntilControlEof_ExitsWithCode0` connects an unelevated,
  in-process host to a pipe the test created. `ElevatedEntryPointTests` route every call to the fake
  `RecordingRunner`. `OpenChannel_ConnectsNamedPipeAndReplies` uses only a local named pipe.
- The rest of the suite that my coverage runs included: every test that reaches `TryRunElevated`,
  `BrokerLauncher.Launch` or `DriveScanner` self-elevation replaces the launch seam
  (`_startProcess`, `_tryRunElevated`) or returns before the launch (the `dotnet.exe` path, a null
  process path). I checked `ElevationUtilitiesTests`, `ElevationUtilitiesCoverageTests`,
  `ElevationProviderTests`, `BrokerLauncherTests` and `DriveScannerTests`. I did not change these tests.

Timing: my second non-interactive coverage run was in progress at about 00:35. I found no path in it
that can show UAC, so I cannot tie the prompt to this lane. Other lanes' runs from the same period are
worth checking.

## Fix round 1 (commit `2f67e21`, on top of `50bda63`)

### Finding 1: unread members (aislop gate)

I searched the spec for `DeleteOnClose` and found no match, so its public-surface section does not keep
`BlockFile.DeleteOnClose`. I followed each deletion's chain until aislop reported nothing new:
- `BlockFile.DeleteOnClose` property deleted, together with the private `BlockFile` constructor parameter
  that only set it, and its three call sites in `BlockFile.cs`. `BlockFileCreateOptions.DeleteOnClose`,
  which selects `FileOptions.DeleteOnClose`, stays. `MFTLib/Index/BlockFile.cs` is an addition to the
  file list under this ruling.
- `BlockScanOutcome` record deleted: nothing constructed or read it.
- `BlockScanTarget`: deleting `Path` exposed `VolumeSerial` as unread next, and nothing constructs or
  reads the record at all. The whole record is deleted, along with `BrokerScanOptions.BlockTargets`, its
  only use. The spec deletes that property too (spec table row "BrokerScanOptions.BlockTargets:
  Deleted").
The commit message lists all of these under "C2 restores what it reads:".

### Finding 2: malformed drive request and cancelled control request

- `BrokerProtocol.Payload.cs`: `PayloadReader` checks every read against the bytes left in the frame.
  A field longer than the frame, a negative length, a count the remaining bytes cannot hold
  (`Count(minimumItemBytes)`, `EntryCount()`), and an entry whose name overruns the frame all throw
  `InvalidDataException`. A garbled count therefore never sizes an allocation.
  `ReadArmAndScanFrame` and `ReadJournalBatchFrame` use the checked counts. The channel's existing
  `InvalidDataException` catch writes `Error` ("malformed request") and closes only that pipe.
- `JournalBrokerHost.Session.cs`: request handling is split into `HandleControlRequestAsync`, which
  handles session end and a broken reply pipe, and `AnswerControlRequestAsync`. The latter answers an
  `OperationCanceledException` raised while the session is still alive with `Error` carrying the
  request id. The Error write sits inside the inner method, so a broken pipe during that write still
  reaches the outer `ClientDisconnectedException` handler.
- `JournalBrokerHost.Channel.cs`: in `ConnectChannelAsync`, a connector's own cancellation while the
  session is alive counts as a connection failure and gets the `Error` reply. Only cancellation of the
  session itself is rethrown.
- `DefaultElevatedEntryRunner.cs`: `ServeAsync` runs inside `try` / `finally { FlushDiagnostics(); }`,
  so a session that throws still gets the bounded flush before the exception leaves. The 2 s bound is
  now `internal static TimeSpan _diagnosticsFlushTimeout` so a test can lift it; `ResetToDefaults`
  restores it.

Regression tests. I ran them first and saw them fail, each for the stated reason
(`.superpowers\fix1-red.log`):
```
Failed RunBroker_SessionFails_FlushesDiagnosticsBeforeLeaving
  Assert.AreNotSame failed. The runner left while a diagnostics line was still queued.
Failed DriveChannel_MalformedRequestPayload_WritesErrorAndOtherRequestsContinue (truncated section name)
  System.ArgumentOutOfRangeException: Specified argument was out of the range of valid values.
Failed ... (negative name count)
  System.ArgumentOutOfRangeException: Non-negative number required. (Parameter 'capacity')
Failed ... (name count beyond the payload)
  System.OutOfMemoryException: Array dimensions exceeded supported range.
Failed OpenChannel_ConnectorThrowsOperationCanceled_RepliesErrorWithRequestId [10 s]
  System.Threading.Tasks.TaskCanceledException: A task was canceled.
Failed!  - Failed: 5, Passed: 0
```
GREEN (`.superpowers\fix1-green.log`): `Passed!  - Failed: 0, Passed: 5`.
The malformed-payload test also sends a `QueryVolume` afterwards and asserts it is answered. The
harness's dispose awaits `ServeAsync`, so any session fault fails the test.
The flush test parks the diagnostics sink on its first line, sends an unknown-kind control frame over
a real named pipe, and asserts `RunBroker` has not returned within 500 ms while the line is still
queued. This is a negative check only: with the fix the runner cannot return before the sink is
released. It then asserts `InvalidDataException` and that no exit code was set.

### Where the volume is released before the section (R7)

- `JournalBrokerHost.Scan.cs:98`: `blockSectionWriter.Write(...)` enumerates `batches` synchronously.
- `RealBlockSectionWriter.cs:16` calls `MftBlockRowWriter.WriteBatches`. Its
  `foreach (var batch in batches)` (`MftBlockRowWriter.cs:29`) disposes the enumerator when the loop
  completes or throws.
- Disposing that iterator runs `using var volume` at `JournalBrokerHost.Sources.cs:52`, releasing the
  volume. The native parse has already returned by then: `ReadRecordBatches` finishes `StreamRecords`
  before yielding (`MftVolume.cs:76-82`).
- `WriteBatches` returns, then `RealBlockSectionWriter.Write`'s `using var block` (`:14`) releases the
  section.
- The pipeline returns, and `using (registration)` (`JournalBrokerHost.Scan.cs:33`) returns the
  parse-thread share last.
Order: volume, section, registration.

### Verification

- Targeted: the JournalBrokerHostChannelTests, JournalBrokerHostSourcesTests, ElevatedEntry,
  UsnJournalSyntheticTests, NamespaceBoundary, NativeSeamIsolation and BlockFile filters:
  `Passed!  - Failed: 0, Passed: 144`.
- Whole suite, `.\scripts\run-coverage.ps1 -NonInteractive` (`.superpowers\coverage3.log`):
  `Passed: 1429  Skipped: 6` (0 failed), line coverage 96.2%, branch 92.6% (2071 of 2236), exit 0.
  This run preceded the final `BlockScanTarget` / `BlockTargets` deletion. That deletion removed a
  type nothing referenced; the build stayed clean with no warnings and the targeted run passed.
- `aislop scan .` final (`.superpowers\aislop5.log`): 99/100, 0 errors, 5 warnings:
  `NativeSeamIsolationFixtures.cs:73`, `:79` and `CachedBlockDeletionOutcome.cs:8`, `:10` (the
  baseline four), plus `JournalBrokerHost.cs:43` 8 parameters (ruled, R-W2-2). Nothing else.
- Elevation: nothing in this round can prompt. The coverage run used `-NonInteractive`. The new runner
  test drives the unelevated child side over a local named pipe and fakes `_exitProcess`.

## Fix round 2 (commit `f974d5e`, on top of `2f67e21`)

### Finding: the flush test used wall-clock time and a static knob

- `DefaultElevatedEntryRunner.cs`: I removed the static `_diagnosticsFlushTimeout` knob and added
  clock injection, which the runner did not have before.
  - The public parameterless constructor uses `TimeProvider.System`. A new internal constructor takes a
    `TimeProvider`, the same constructor-injection pattern as `JournalBrokerHost`.
  - `FlushDiagnostics` (now an instance method) waits for whichever finishes first: the diagnostics
    flush, or `Task.Delay(2 s, _timeProvider)`. The delay's timer is cancelled afterwards.
  - The injected clock bounds only the exit flush. The host is still built by `CreateDefault()` on the
    system clock, so the flush's delay is the only timer the runner creates on the injected clock.
- `DefaultElevatedEntryRunnerTests.RunBroker_SessionFails_FlushesDiagnosticsBeforeLeaving` rewritten:
  1. The diagnostics sink parks on a `TestGate`.
  2. The runner gets a `FlushBoundSignalingClock`, a `FakeTimeProvider` subclass that is never
     advanced. Its `CreateTimer` completes a `FlushBoundStarted` signal.
  3. The test sends an unknown-kind frame and awaits `sink.Entered`, then `clock.FlushBoundStarted`,
     both bounded by the hang-guard token.
  4. It asserts `runTask.IsCompleted == false`, releases the gate, and asserts the runner throws
     `InvalidDataException`, sets no exit code, and delivered the `frame read kind=200` line to the
     sink.
  The test has no `Task.Delay` and no elapsed-time comparison. Because the clock never advances, the
  bound cannot end the wait in the green state.

RED: a scratch mutation replaced the `FlushDiagnostics();` call in `RunBroker`'s `finally` with a
comment. It was not committed; I restored the file and checked that no mutation text remains.
`.superpowers\fix2-red.log`:
```
Failed RunBroker_SessionFails_FlushesDiagnosticsBeforeLeaving [10 s]
  System.Threading.Tasks.TaskCanceledException: A task was canceled.   (FlushBoundStarted never fired)
Failed!  - Failed: 1, Passed: 0
```
GREEN: `DefaultElevatedEntryRunnerTests`: `Passed!  - Failed: 0, Passed: 3`.

### Verification

- Targeted: the JournalBrokerHostChannelTests, JournalBrokerHostSourcesTests, ElevatedEntry,
  UsnJournalSyntheticTests, NamespaceBoundary, NativeSeamIsolation, BlockFile and BrokerDiagnostics
  filters: `Passed!  - Failed: 0, Passed: 173`.
- Whole suite, `.\scripts\run-coverage.ps1 -NonInteractive` (`.superpowers\coverage4.log`, after the
  round 1 deletions): `Passed: 1429  Skipped: 6` (0 failed), line coverage 96.3%, branch 92.5%
  (2070 of 2236), exit 0.
- The first aislop scan flagged a missing `<summary>` on the new internal constructor. I added it,
  which was a doc-only change, and re-ran the scan. Final `aislop scan .` (`.superpowers\aislop7.log`):
  99/100, 0 errors, 5 warnings: the baseline four (`NativeSeamIsolationFixtures.cs:73`, `:79`;
  `CachedBlockDeletionOutcome.cs:8`, `:10`) and `JournalBrokerHost.cs:43` with 8 parameters (ruled,
  R-W2-2). After the doc change I re-ran `DefaultElevatedEntryRunnerTests`: 3 passed.
- Elevation: nothing can prompt. The coverage run used `-NonInteractive`, and the runner test drives the
  unelevated child side over a local named pipe with `_exitProcess` faked.
