# Task C1 review (base 305fab2, head 50bda63)

### Plan Compliance
- Issues found: the task's aislop gate (no finding beyond the four baseline warnings) is not met; three extra warnings beyond the ruled 8-parameter constructor (Important 1). Otherwise built as the brief and rulings require: wire table (`BrokerFrame.cs` enum 1-17, `BrokerProtocol.cs` `ReadFrame`), interfaces (two-argument `UsnJournalCatchUpSource` per ruling), S3 allocator rule (`ParseThreadAllocator.cs` `RebalanceLocked`), R5 bounded connect and first request (`JournalBrokerHost.Channel.cs:45-78`, `:147-177`), R13/L1 frame order and journal-proven loss (`JournalBrokerHost.Scan.cs:26-45`, `:198-235`), control EOF grace drain (`JournalBrokerHost.Session.cs:33-50`, `:111-138`), `--once` dropped, `ScanCatchUp` added, `coverage-linux.sh` renames, and every brief-named test present with its exact name.
- Cannot verify from diff:
  - The commit message's per-file list of deleted tests and porting tasks (the diff header shows only the subject). It should also name the two `DefaultElevatedEntryRunnerTests` client-disconnect cases removed inside a modified file, for C3b.
  - That `MftBlockRowWriter.WriteBatches` disposes the batch enumerator (releasing the volume) before `RealBlockSectionWriter.Write` returns; `RealBlockSectionWriter.cs:14-19` holds the section correctly (R7 order: volume, section, registration).
  - That A1's `ReadRecordBatches` returns promptly on cancellation (R7).

### Elevation
No test in the diff can reach a real `Verb = "runas"` launch; `Environment.Exit` is reachable only after a timed-out runner test.
- The diff references no `BrokerLauncher`, `ElevationUtilities` or `TryRunElevated` in code (the only grep hit is a comment in `ElevatedEntryPoint.cs`, diff:4565).
- `DefaultElevatedEntryRunner.RunBroker` (`DefaultElevatedEntryRunner.cs:24-52`) does only `NamedPipeClientStream.Connect`, `CreateDefault().ServeAsync`, `FlushDiagnostics()` and `_exitProcess`.
- Both runner tests replace `_exitProcess` with a fake before calling `RunBroker` (`DefaultElevatedEntryRunnerTests.cs`, diff:352 and :370); the class is `[DoNotParallelize]` and resets in `[TestCleanup]`.
- `ElevatedEntryPointTests` uses only `RecordingRunner` (diff:651-666).
- `HostChannelHarness` uses in-memory pipes or `ConnectDrivePipeAsync` (plain named-pipe client); volume access goes through seams, journal checks through the internal override.
- Residual: if `RunBroker_ValidPipeName_..._ServesUntilControlEof_ExitsWithCode0` hits its 10-second timeout, `[TestCleanup]` restores `_exitProcess = Environment.Exit` while the orphaned `RunBroker` thread is still in `ServeAsync`; when it returns, `DefaultElevatedEntryRunner.cs:52` calls the real `Environment.Exit` and kills the test host. The same hazard existed before C1; it is not a UAC path.
- Outside the diff, the launcher tests inject `_startProcess` (`ElevationUtilitiesTests.cs:111`, `BrokerLauncherTests.cs:33`). The only real elevation in test tooling is `scripts/run-coverage.ps1:154` (`Start-Process powershell -Verb RunAs`, run whenever `-NonInteractive` is omitted) and `native-coverage-elevated.ps1`. Whichever lane ran one of those without the flag caused the prompt; this lane reports using `-NonInteractive`.

### Strengths
- Channel open is owned end to end: a connector that throws synchronously becomes a faulted task (`Channel.cs:50`); a timeout cancels the connector token and disposes a late connection (`Channel.cs:56-64`, `:80-88`, tested); a failed `ChannelOpened` write disposes the stream (`Channel.cs:28-37`); the first-request wait is bounded on the injected clock (`Channel.cs:156`).
- Teardown order: the registration is released only after `ProduceBlockAsync` returns, and that awaits the inner `Task.Run` (whose token only gates the start) and the progress pump (`Scan.cs:26-37`, `:76`). `ScanChannel_PipeClosed_ShareReturnsOnlyAfterSourceReturns` proves it with a source that ignores cancellation.
- Allocator: admits immediately only when the queue is empty (arrival order kept); a cancelled waiter removes itself only if still queued, so an admission in the same moment keeps its registration and `SetResult` never races `TrySetCanceled`; waiters complete outside the lock; the registration disposes once; `RunScanAsync` has no await between admission and its `using`, so no path leaks an allowance.
- The lost catch-up decision follows L1 exactly: the live journal is the only classifier, with no cursor re-query and no empty batch (`Scan.cs:198-235`). Four tests cover proved, retained, unanswerable and recreated, plus cancellation.
- Control concurrency: each request forces a yield off the read loop (`Session.cs:57`); writes are serialized per pipe (control lock, `DriveChannel.WriteLock`); a test proves a gated query does not delay a second one.
- The grace drain is proven on `FakeTimeProvider`, including a source that ignores cancellation, and re-snapshots tasks tracked during the drain.
- `ClientDisconnectedException` now carries the inner `IOException`; drive-pipe and control-pipe handling are split correctly.
- The diagnostics flush question is answered: a bounded 2-second flush on orderly exit.

### Issues
#### Critical (Must Fix)
None.

#### Important (Should Fix)
1. **The aislop gate is not met: three new warnings beyond the ruled 8-parameter constructor.**
   - `MFTLib/Broker/Client/BlockScanOutcome.cs:6` (`SectionName`) and `BlockScanTarget.cs:6` (`Path`) are never read, and `BlockScanOutcome` is never constructed at all (grep finds only its declaration).
   - The `MFTLib/Index/BlockFile.cs:76` `DeleteOnClose` getter has no readers.
   - The spec keeps both records (spec:748-756; C2 reads them), and `BlockFile.cs` belongs to B1, a parallel lane, so editing it here risks a conflict. No behavior impact, but the gate is binary.
   - Resolution: either the orchestrator rules them transient until C2 and B1, or C1 deletes the two unread record members and C2 re-adds them, per the plan's "delete the dependents, a later task rebuilds" rule.
2. **A malformed drive-pipe request becomes a session fault instead of an `Error` frame.**
   - `ReadFirstRequestAsync` catches only `InvalidDataException` and `EndOfStreamException` (`Channel.cs:169`).
   - A short or garbled payload throws `ArgumentOutOfRangeException` from `PayloadReader` (out-of-range span slicing in `BrokerProtocol.Payload.cs` `String()` and `Int32()`), or from `new List<string>(nameCount)` with a negative count (`BrokerProtocol.cs` `ReadArmAndScanFrame`). A large positive `nameCount` risks `OutOfMemoryException`.
   - The exception escapes `ServeChannelAsync`, faults the tracked task, and `DrainAsync` rethrows it at session end (`Session.cs:126`). `ServeAsync` then throws, and `RunBroker` never reaches `FlushDiagnostics()` or `_exitProcess(0)` (`DefaultElevatedEntryRunner.cs:51-52`).
   - The same applies to a canceled request task: `SessionTasks.Track` keeps canceled tasks (`Session.cs:200`), so a connector that throws its own `OperationCanceledException` while the session is alive fails the drain with `TaskCanceledException`, and that request never gets a reply.
   - Fix: map payload decode failures to `InvalidDataException` inside `ReadFrame`, or catch them in `ReadFirstRequestAsync`. Add a regression test (a truncated `ArmAndScan` payload yields `Error` then EOF), seen failing first.

#### Minor (Nice to Have)
1. `DrainAsync` hides a fault when another task outlives the grace period: if a faulted task coexists with one still running at the deadline, the method returns quietly and the fault is never observed (`Session.cs:120-124`). Either rethrow faults from the completed subset or document the behavior.
2. Concern 4 is sound in practice but has a check-then-act window: `WriteControlFrameAsync` checks the token, then takes a lock that `ControlSession.Dispose` may have disposed (`Session.cs:97-99`), and an abandoned writer holding the lock calls `Release()` on a disposed semaphore (`Frames.cs:58-61`). Both surface only as unobserved exceptions after the session has returned, and the process exits right after.
3. Deviation `Processing(string stepName)`: the CA1716 rename is justified and positional callers are unaffected; C5's brief should use the new name.
4. `ScanProgress` without a drive is brief-mandated ("as today without drive"), not a deviation. `DriveLetter = string.Empty` on read (`BrokerProtocol.cs` `ReadScanProgress`) must be filled by C2's collector; name this in C2's brief.
5. `WriteCatchUpLost` and `BrokerFrame.CatchUpLost` are internal while every other writer and factory is public (`BrokerProtocol.Write.cs:109`). The missing-drive reason covers `RequireCatchUpLoss` but not the writer, which takes a public type. Inconsistent but harmless.
6. `ParseThreadAllocator_MoreScansThanProcessors_ExtrasQueueInArrivalOrder` tests the allocator directly; the host-level "the third's source is not entered" is covered only indirectly, by `ShareReturnsOnlyAfterSourceReturns` asserting that `E` was not entered.
7. The progress throttle and `ScanProgressState.Elapsed` still read `Stopwatch`, not the injected `TimeProvider` (`Scan.cs` `RunProgressPumpAsync`). This predates C1, and the global list does not name the throttle.
8. No protocol round-trip tests remain after the deletions; the `CatchUpLost` nullable fields are exercised only through host tests. Acceptable given C3b's porting; confirm C3b covers every kind 1 to 17.

### Assessment
Task quality: Needs fixes
Reasoning: The concurrency core (owned channel open, allocator admission and release, R7 teardown order, grace drain, journal-proven `CatchUpLost`) is correct and well tested. Approval is blocked by the aislop gate miss (a fix or an orchestrator ruling) and by a malformed drive-pipe request escalating to a session-ending exception instead of an `Error` frame.
