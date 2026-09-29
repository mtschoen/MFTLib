### Task B1: Per-drive watch contract and FileIndex state machine (Opus)

Opus because it builds the per-drive state machine (amendment SM, with R1, R2 and R4) that replaces the session pump, stop, catch-up and the rescan's watch interplay at once, and every one of those runs concurrently with the others.

This task is larger than the 600-line guideline (about 1100 lines of new production and support code, plus deletions). It cannot be split green: the contract change breaks every watch caller at once, and the only alternative is an adapter from the merged stream to per-drive handles.

**Files:**
- Create: `MFTLib/Index/IIndexDriveWatch.cs`, `MFTLib/Index/DriveWatchFaultException.cs`, `MFTLib/Index/FileIndex.DriveRuntime.cs`, `MFTLib/Index/FileIndex.WatchDrive.cs` (start, stop), `MFTLib.Tests/Index/FileIndexPerDriveWatchTests.cs`
- Rewrite: `MFTLib/Index/IIndexWatchSource.cs`, `MFTLib/Index/WatchStreamItem.cs`, `MFTLib/Index/JournalBatch.cs`, `MFTLib/Index/WatchFault.cs`, `MFTLib/Index/FileIndex.WatchPump.cs` (per-drive pump), `MFTLib/Index/FileIndex.WatchCatchUp.cs` (per-drive slots, single-drive wait only), `MFTLib/Index/WatchCatchUpState.cs` (slot stays; `CatchUpCoordinator` goes), `MFTLib/Index/FileIndex.Rescan.cs` (watch interplay: stop X, scan, commit, start X), `MFTLib/Index/FileIndex.Watch.cs`, `MFTLib/Index/FileIndex.WatchTargets.cs`, `MFTLib/Index/FileIndexOptions.cs` (`WatchSource` doc), `MFTLib.Tests/TestSupport/FakeIndexWatchSource.cs`, `MFTLib.Tests/TestSupport/WatchHarness.cs`
- Modify: `MFTLib/Index/FileIndex.cs` (drop `_watchSession`; add `_driveRuntimes`), `MFTLib/Index/FileIndex.Disposal.cs` (stop every drive's watch first, never throw a fault), `MFTLib/Index/DriveStatus.cs`, `FileIndex.Scanning.cs`, `BlockSource.cs` and `DriveFailureKind.cs` (doc comments that describe the session watch or rescan)
- Delete: `MFTLib/Index/ReadyOnFirstMoveWatchStream.cs`, `MFTLib/Index/WatchStreamNotRunningException.cs`, `MFTLib/Index/FileIndex.WatchSession.cs`, `MFTLib/Index/FileIndex.WatchStart.cs`, `MFTLib/Index/FileIndex.WatchFaults.cs`, `MFTLib/Index/FileIndex.Rescan.Watch.cs`
- Delete tests (ported by B2, B3, B4): `MFTLib.Tests/Index/FileIndexWatchTests.cs`, `FileIndexWatchPumpTests.cs`, `FileIndexWatchFaultTests.cs`, `FileIndexWatchCatchUpTests.cs`, `FileIndexWatchCatchUpLinkedWaitTests.cs`, `FileIndexWatchCatchUpRetentionTests.cs`, `FileIndexWatchRescanTests.cs`, `FileIndexWatchFailedRescanTests.cs`, `FileIndexWatchRescanFaultDuringProductionTests.cs`, `FileIndexWatchRescanCheckpointLossTests.cs`, `FileIndexWatchRecoveryFaultTests.cs`, `FileIndexCacheOnlyUnresumableWatchTests.cs`, `FileIndexMidSessionCheckpointLossTests.cs`, `ConsumerJournalIsolationTests.cs`, `FileIndexCheckpointLossDetectionTests.cs` (only if it no longer compiles; otherwise edit its `DriveWatchFailure` uses in place), `FileIndexWatchStartReadinessTests.cs`, `FileIndexWatchRescanEndedSessionTests.cs` (the last two are not ported: their subject is gone); `MFTLib.Tests/TestSupport/ReadinessScriptedWatchSource.cs` (the broker watch-source tests were deleted by A5)
- Modify tests that only need the new shape to compile: `Index/FileIndexRescanCleanupTests.cs` (keeps its `_swapGate` reflection until B5), any other file the build names

**Interfaces produced (public, from spec section 3):**

```csharp
namespace MFTLib.Index;
public interface IIndexWatchSource
{
    Task<IIndexDriveWatch> StartAsync(IndexWatchTarget target, CancellationToken cancellationToken);
}
// FileIndex disposes each handle exactly once (the pump's finally once the handle is published, the start path before that),
// and ReadAsync ends promptly when its token is cancelled.
public interface IIndexDriveWatch : IAsyncDisposable
{
    char DriveLetter { get; }
    IAsyncEnumerable<WatchStreamItem> ReadAsync(CancellationToken cancellationToken);
}
public abstract record WatchStreamItem { private protected WatchStreamItem() { } }
public sealed record JournalBatch(IReadOnlyList<UsnJournalEntry> Entries, ulong JournalId, long NextUsn) : WatchStreamItem;
public sealed record DriveCaughtUp : WatchStreamItem;
public sealed class DriveWatchFaultException : Exception
{
    public DriveWatchFaultException(char driveLetter, string message, Exception? innerException = null);
    public char DriveLetter { get; }
}
public enum WatchFaultKind { Subscriber, Drive, Apply, Channel } // CatchUpLost and Recovery are added by B5
public sealed record WatchFault(WatchFaultKind Kind, char DriveLetter, Exception Exception);

public sealed partial class FileIndex
{
    public Task StartWatchingAsync(char driveLetter, CancellationToken cancellationToken);
    public Task StopWatchingAsync(char driveLetter, CancellationToken cancellationToken);
    public Task WaitForCatchUpAsync(char driveLetter, CancellationToken cancellationToken);
    // The no-list forms StartWatchingAsync(CancellationToken), StopWatchingAsync(CancellationToken) and
    // WaitForCatchUpAsync(CancellationToken) are deleted here and return with batched results in B7.
}
```

**Internal shape:**

```csharp
sealed class DriveRuntime
{
    public DriveRuntime(char driveLetter);
    public char DriveLetter { get; }
    public SemaphoreSlim LifecycleGate { get; } = new(1, 1); // start, rescan, recovery (B6), disposal (B5)
    // Guarded by _stateLock:
    public WatchInstance? Current;
    public WatchInstance? Retiring;     // until its Drained completes
    public bool WatchRequested;
    public long NextGeneration;
}
sealed class WatchInstance
{
    public WatchInstance(char driveLetter, long generation, DriveBlock armedBlock, CancellationToken disposalToken);
    public long Generation { get; }
    public DriveBlock ArmedBlock { get; }
    public CancellationTokenSource StartCancellation { get; } // linked to the disposal token
    public CancellationTokenSource PumpStop { get; }
    public WatchInstanceState State;       // Starting, Running, Faulted, Retiring, Drained; under _stateLock
    public IIndexDriveWatch? Handle;       // under _stateLock
    public Task? Pump;                     // under _stateLock
    public bool SubscriberFaultAnnounced;
    public Exception? OutstandingFault;    // rethrown once by StopWatchingAsync(X)
    public WatchCatchUpSlot CatchUp { get; } = new();
    public Task Drained { get; }            // completes after the handle is disposed and the pump has returned
}
readonly Dictionary<char, DriveRuntime> _driveRuntimes; // one per configured drive, created in OpenAsync
```

Behavior: the "Per-drive state machine" section above (amendment SM) is the contract. B1 implements every row of its linearization table except recovery (B6) and the disposal gate order (B5), with these specifics:

- `StartWatchingAsync(X)`: `ArgumentException` for a letter not in the index; take X's lifecycle gate; `InvalidOperationException` when X has no MFT-backed block, or the existing `RecordUnresumableCheckpointWatchFailureLocked` message for a cache-only unresumable drive; succeed without restarting when `Current` is `Running`; await `Retiring?.Drained`; under `_stateLock` create the instance in `Starting`, register it as `Current`, set `WatchRequested`, clear X's failure message (R2: registration happens before the source is invoked); call `source.StartAsync(target, instance.StartCancellation.Token linked with the caller's token)` outside every lock; under `_stateLock`, if the instance is still `Current` and `Starting`, store the handle, move to `Running`, start the pump; otherwise dispose the handle outside the lock and throw `OperationCanceledException`. A throw from `StartAsync` records the failure message, faults the instance's slot, clears `Current`, completes `Drained`, and propagates.
- `StopWatchingAsync(X)`: no lifecycle gate. Under `_stateLock` clear `WatchRequested`; move `Current` to `Retiring` (cancel `StartCancellation` for a `Starting` instance, `PumpStop` for a running one) and take its outstanding fault; outside the lock await `Drained` bounded by the caller's token (the pump's `finally` is the only code that disposes a published handle: stop cancels `PumpStop` and `ReadAsync` returns when its token is cancelled); the slot is cancelled; rethrow the taken fault once with `ExceptionDispatchInfo`.
- Pump: `await foreach (var item in handle.ReadAsync(PumpStop.Token))`. A `JournalBatch` goes to `ApplyJournalEntriesCore(X, instance, ...)`, which after taking the gate (`_swapGate` in B1, X's write gate from B5) checks under `_stateLock` that the instance is still `Current`, `Running`, and `ArmedBlock` is X's published block, and drops the batch otherwise (R1); `RaiseChanged` runs with no gate held; a handler throw raises `WatchFaulted(Subscriber, X)` once per instance. `DriveCaughtUp` completes the instance's slot if the instance is still `Current`. Ends: `PumpStop` cancellation is a stop; `DriveWatchFaultException` is `Drive`; an apply throw is `Apply`; any other exception, or a normal end before the stop, is `Channel` ("The watch for drive X ended without being stopped."). A fault is recorded only if the instance is still `Current`: message, outstanding fault, slot faulted, state `Faulted`, `RecordCheckpointLossForFaultedDrive(X)` before `WatchFaulted` is raised. The handle is disposed and `Drained` completed on every exit. In B1 a `Drive` or `Apply` fault leaves X `Faulted`; B6 adds recovery.
- `WaitForCatchUpAsync(X)`: waits on the `Current` instance's slot: completes on catch-up, faults with its fault, cancelled by stop, rescan, or disposal. The caller's token and the disposal token reach the wait through a token registration that cancels a queued-continuation completion source, not through `Task.WaitAsync` (today `FileIndex.WatchCatchUp.cs:374` and `:388` use `Task.WaitAsync`), so the awaiter's continuation never runs inline on the stack that cancels the token (spec 2.6.8).
- `RescanAsync(X)`: takes X's lifecycle gate and holds it to the end (R4), then `_rescanGate` (removed in B5); retires `Current` and awaits `Drained` before production; production holds neither `_swapGate` nor `_stateLock`; after a committed replacement it restarts through `StartWatchingCoreAsync(runtime, gateHeld: true, ...)` if `WatchRequested` is still set; after a failed scan it applies today's rule (`FileIndex.Rescan.cs:145-158`): a drive that was healthy restarts from its old cursor, a faulted or unresumable one stays faulted. Everything in `FileIndex.Rescan.Watch.cs` (suspend, resume, reclaim, restart, unreported-fault ledger) is deleted, not ported.
- `DisposeAsync`: set `_disposed`, cancel the disposal token (which cancels every `StartCancellation`), then for every drive retire `Current` and await `Drained`; never throw a fault; then today's gate-and-release sequence (B5 replaces it).
- Asynchronous completion (spec 2.6.8): every completion source a handler can settle, directly (a fault it raises completes waiters and `Drained`) or through a token it cancels, is created with `TaskCreationOptions.RunContinuationsAsynchronously`: `WatchInstance.Drained`, the catch-up slot's `Waiter` and `FaultWaiter` (already so at the base commit, `WatchCatchUpState.cs:49` and `:56`), and the completion source `WaitForCatchUpAsync` cancels from its token registration. The promise is non-inline execution only: nothing about ordering relative to the handler's return and nothing about which thread. B1 adds the internal test hook `internal Action<Action>? PumpFaultSettlementWrapperForTest`, which the pump calls as `wrapper(settleFault)` around exactly the step that faults X's slot (before `WatchFaulted` is raised), so a test can hold a `[ThreadStatic]` flag for the duration of that step.

- [ ] **Step 1: Rewrite the test doubles.** `FakeIndexWatchSource`: `StartAsync(target)` returns a `ScriptedDriveWatch` per call and records the target; the test drives each handle with `Publish(WatchStreamItem)`, `FailDrive(Exception)` (the read throws `DriveWatchFaultException`), `LoseChannel(Exception)` (the read throws the given exception), `End()` (normal end), `HoldStart(TestGate)` (the start waits), and `FailStart(Exception)`; `DisposeCount`, `ThrowOnSecondDispose` (a second `DisposeAsync` throws, so a double dispose fails a test), `Starts` (targets in order). `WatchHarness`: builds a `FileIndex` over synthetic MFT blocks for letters `T`, `U`, `V` with a fake producer and the fake source, and exposes `Changes` and `Faults` collections.
- [ ] **Step 2: Failing tests** in `FileIndexPerDriveWatchTests` (each named, each asserting exactly this):
  - `StartWatching_OneDrive_StartsOnlyThatDrive`: start `T`; `Starts` is `[T at its block cursor]`; `DriveStatus(T).WatchCatchUp` is `CatchingUp`, `U` is `NotStarted`.
  - `StartWatching_AlreadyWatching_DoesNotRestart`: second call completes, `Starts.Count` is 1.
  - `StartWatching_SourceStartThrows_ThrowsAndFaultsSlot`: the exception propagates; `WatchCatchUp` is `Faulted`; `WatchFailureMessage` set.
  - `StopDuringStart_CancelsTheSourceStart` (R2): the fake's `StartAsync` waits for its token; `StopWatchingAsync(T)` cancels that token, the start throws `OperationCanceledException`, no pump runs, `Current` is empty.
  - `DisposeDuringStart_CancelsTheSourceStart_AndCompletes` (R2): same fake; `DisposeAsync` completes without the test releasing anything.
  - `StartReturnsAfterStop_HandleDisposedByStartPath`: the fake ignores its token and returns a handle after the stop; that handle's `DisposeCount` is 1 and no pump runs.
  - `RestartAfterStop_AwaitsOldDrain_BeforePublishing` (R1): `T`'s old pump is held inside `ApplyJournalEntriesCore` on a test seam gate; `StopWatchingAsync(T)` with an already-cancelled token returns; `StartWatchingAsync(T)` does not publish the new handle (`Starts.Count` stays 1) until the gate opens and the old instance drains.
  - `RetiringPumpBatch_IsDroppedNotApplied` (R1): a batch accepted by the old pump before its drain is not applied to the block the successor armed against (the block's generation and row are unchanged).
  - `OldInstanceFault_AfterRestart_DoesNotTouchNewInstance` (R1): the old handle throws after a restart; no `WatchFaulted` for it, the new instance's slot stays `CatchingUp`.
  - `Rescan_HoldsLifecycleGateThroughProduction` (R4): with `T`'s producer gated, a `StartWatchingAsync(T)` waits until the rescan finishes.
  - `Batch_AppliesAndRaisesChanged`: publish a batch on `T`; one `FileChange` observed; `T`'s block cursor advanced.
  - `DriveCaughtUp_CompletesWait`: `WaitForCatchUpAsync(T)` completes after `DriveCaughtUp`.
  - `DriveFault_RaisesDriveKindAndFaultsOnlyThatDrive`: `FailDrive` on `T`; one `WatchFault(Drive, 'T')`; `U` keeps applying a later batch.
  - `ApplyFailure_RaisesApplyKind`: a batch the mutator rejects raises `WatchFault(Apply, 'T')`.
  - `ChannelLoss_RaisesChannelKind`: `LoseChannel(new IOException())` raises `WatchFault(Channel, 'T')`, `WatchFailureMessage` set, catch-up `Faulted`.
  - `NormalEndBeforeStop_IsChannelFault`.
  - `SubscriberThrows_AnnouncedOncePerWatch_DriveKeepsWatching`: two batches, a throwing handler, one `Subscriber` fault, both batches applied.
  - `BlockedChangedHandlerOnT_DoesNotDelayU`: the handler for changes on `T` waits on a `TestGate`; a `U` batch is applied and observed before the gate opens (spec 9, row 2).
  - `Stop_RethrowsOutstandingFaultOnce`: after `FailDrive`, the first `StopWatchingAsync(T)` throws that exception, the second completes.
  - `Stop_DuringRescan_ReturnsWhileScanRuns_AndRescanDoesNotRestart`: `T` watching; the producer for `T` waits on a gate; `StopWatchingAsync(T)` completes while gated; release; the rescan commits and `Starts.Count` for `T` stays at 1 (spec 9, "Stop during rescan").
  - `Rescan_RestartsWatchFromFreshCursor`: after a rescan the second start of `T` carries the new block's cursor.
  - `Dispose_StopsEveryDriveAndNeverThrows`: two drives watching, one faulted; `DisposeAsync` completes without throwing; both handles disposed.
  - `HandleDisposedExactlyOnce_AfterStop`, `HandleDisposedExactlyOnce_AfterDriveFault`, `HandleDisposedExactlyOnce_WhenStartReturnsAfterStop` and `HandleDisposedExactlyOnce_AfterIndexDisposal`: each with a `ThrowOnSecondDispose` handle; `DisposeCount` is 1 and no exception surfaces (single ownership of the handle).
  - `WaitForCatchUp_CallerTokenCancelled_ThrowsOperationCanceled` and `WaitForCatchUp_DisposalTokenCancelled_ThrowsOperationCanceled`: the wait faults with `OperationCanceledException` through the token registration and the slot is unaffected.
  - `PumpFaultSettlesWaiter_ContinuationNotInline` (spec 9): a `WaitForCatchUpAsync(X)` is pending; X faults; `PumpFaultSettlementWrapperForTest` sets a test `[ThreadStatic]` flag for exactly the fault-settlement step and clears it after; the waiter's continuation records the flag on its own thread and then calls `StopWatchingAsync(Y)`; assert the recorded value is false and the stop completes. No thread ids, no ordering assertion, no sleeps.
  - `CacheOnlyUnresumable_StartThrowsWithRescanMessage`.
- [ ] **Step 3: See them fail**, then implement the types, runtime, start, stop, pump, catch-up, rescan interplay and disposal above; delete the listed files; fix every compile error the build names by rewriting the caller, not by restoring a member.
- [ ] **Step 4: Verify.** Targeted `FileIndexPerDriveWatchTests`; the namespace boundary test (`NamespaceBoundaryTests`) passes with the new types; `NativeSeamIsolationTests` passes; whole suite; `aislop scan .`.
- [ ] **Step 5: Commit:** "FileIndex watches each drive through its own handle and pump". The commit message lists every deleted test file and which task ports it.

**Gate:** green (the suite shrinks; B2, B3, B4 and C6 restore coverage). **Depends on:** A3, A5. **Parallel with:** A4, C1.

