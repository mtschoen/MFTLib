# Context common to the broker tasks after C1 (C2, C3a, C3b, and later C4 to C8)

Task C1 replaced the single-pipe broker host with one control pipe plus one pipe per drive
operation, a process-wide parse-thread allocator, and the new wire table. It deleted the old
client (`JournalBrokerClient*`) and every test file that no longer compiled. The old files are
read with `git show c1d43784:<path>`; the new contract is the code in your worktree. Line numbers
in briefs were read at `c1d43784`; locate by symbol.

## What exists on your base (read the code; these are pointers, not a substitute)

- Host: `MFTLib/Broker/Host/JournalBrokerHost*.cs` (`.Session.cs` control session and grace
  drain, `.Channel.cs` owned channel open with bounded connect and first-request waits, `.Scan.cs`
  scan frame order and the lost catch-up decision, `.Sources.cs`), `ParseThreadAllocator.cs`.
- Wire: `MFTLib/Broker/Protocol/BrokerFrame.cs` (frame kinds), `BrokerProtocol*.cs`
  (`ReadFrame`, writers), `MFTLib/Broker/Host/JournalBrokerHost.Frames.cs`.
- Host test harness: `HostChannelHarness` under `MFTLib.Tests/TestSupport`, over in-memory pipes
  with an injected `TimeProvider` and processor count. Model new host tests on the existing
  C1 tests that use it.
- Deviations from the briefs that C1 made and the review accepted. Where a brief disagrees, these
  win:
  - `IBrokerOperationReporter.Processing(string stepName)`: the parameter is `stepName`, not
    `step` (analyzer CA1716).
  - `ScanProgress` frames carry no drive letter. A frame read off a drive pipe has
    `DriveLetter == string.Empty`; the client fills it in from the channel it read the frame on.
  - The `CatchUpLost` loss is read through the internal `BrokerFrame.RequireCatchUpLoss(char)`,
    because the wire does not carry the drive letter `JournalCheckpointLoss` requires.
    `WriteCatchUpLost` and `BrokerFrame.CatchUpLost` are internal.
  - `UsnJournalCatchUpSource` takes `(string driveLetter, UsnJournalCursor since)`. The
    `int maximumBufferReads` parameter is added by task C5, not before.
  - `JournalCheckpointLossDetection.ScanCatchUp` already exists (added by C1).
  - A malformed drive-pipe request or a cancelled control request is answered with an `Error`
    frame on that pipe; other channels keep running. An `Error` write to an already-closed drive
    pipe ends only that channel, quietly.
  - `DefaultElevatedEntryRunner` has an internal constructor taking a `TimeProvider`; its exit
    path flushes the diagnostics writer with a bounded wait on that clock.
- Deleted by C1 because nothing read them, to be restored by the task that reads them (C2):
  `MFTLib/Broker/Client/BlockScanOutcome.cs`, `MFTLib/Broker/Client/BlockScanTarget.cs`,
  `BrokerScanOptions.BlockTargets` (the spec deletes this one for good), and the public property
  `BlockFile.DeleteOnClose` (`BlockFileCreateOptions.DeleteOnClose` remains). Restore only what
  your task's code actually reads; an unread member fails the aislop gate.
- Diagnostics: `BrokerDiagnostics.Log(string channel, string message)` and
  `LogFrame(string channel, string direction, byte kind, int length)`, with
  `BrokerDiagnostics.ControlChannel` and `BrokerDiagnostics.DriveChannel(char, int)`. The writer
  is process-global and asynchronous: a test that reads the log calls `FlushForTestAsync` first,
  and its class carries `[DoNotParallelize]`.
- Native parse: `MftVolume.StreamRecords` and `ReadRecordBatches` take a `ParseThreadAllowance?`
  and a `CancellationToken`. One allowance attaches to one running parse at a time. The native
  chunk-thread-count recorder (`GetChunkThreadCounts`, `GetResolveThreadCount`) is
  process-global: a test in which two parses run at once must not assert on it.
- Known open defect, recorded for a later task, do not fix unless your dispatch assigns it: a
  huge frame-length prefix (`Frames.cs`, the length read) throws `OverflowException` or
  `OutOfMemoryException` instead of `InvalidDataException` and ends the session.

## Rules for every broker task

- Nothing you run or write may need elevation or start a real elevated process. The only two
  `runas` sites are `BrokerLauncher` and `ElevationUtilities.TryRunElevated`; tests reach them
  only through their fake start-process seams. `DefaultElevatedEntryRunner._exitProcess` is
  replaced in every test that calls `RunBroker`, and the class is `[DoNotParallelize]`.
- Time: every limit (heartbeat, stall, processing, reply timeout, connect timeout, grace period)
  reads the injected `TimeProvider`; tests use `FakeTimeProvider`. No `Task.Delay` on real time
  in a test, no elapsed-time comparison, ordering proven by `TestGate` signals, every await in a
  test bounded by a timeout so a failure fails instead of hanging.
- Port rules (C3a, C3b, C4, C6): port every case that still describes the new contract; keep the
  method name when the meaning is unchanged; the commit message lists every dropped method, one
  per line, with the reason, built mechanically by comparing the `[TestMethod]` names in each
  base file with those in each new file. A ported test that fails against the code is
  information: do not weaken the assertion and do not change production code; report it as a
  suspected defect with the test name, the assertion, the observed value and the spec line.
  Ports are not written test-first; a NEW test your brief names is shown failing by a scratch
  mutation that is not committed.
