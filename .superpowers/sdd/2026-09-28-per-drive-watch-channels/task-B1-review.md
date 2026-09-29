### Plan Compliance
- **Issues found.**
  - `FileIndex.WatchDrive.cs:3803-3835` (stop on a drive that is not watching) and test `FileIndexPerDriveWatchTests.cs:881-893` do not match the spec. Details under Important 1.
  - A rescan cancelled during the retire step leaves the watch stopped but still requested, which breaks the spec's failed-scan rule (`FileIndex.RescanRestart.cs:2741`, `:2805-2808`). Details under Important 2.
- **Cannot verify from diff:**
  - Whether the commit message lists every deleted test file and the task that ports it. The diff shows only the subject line. The controller should check `git log -1 491a88b`.
  - Where coverage lands for the three methods removed from `FileIndexRescanCleanupTests.cs:967-1050`. That file was modified, not deleted, so B2, B3 and B4 (which port the deleted files from c1d43784) will not pick them up. The third method is still meaningful in per-drive form: a rescan after a watch that ended without a stop clears the faulted catch-up and the message. The controller should confirm a ported file covers it.
  - The test where a handler calls stop on its own drive and gets `InvalidOperationException` at once (spec row 1328). With B1 as written, a `Changed` handler that blocks on `StopWatchingAsync(X)` for its own drive waits on its own pump's `Drained` and deadlocks. The brief does not assign this to B1; the controller should confirm a later task owns it.

Line numbers below are line numbers in `review-B1-live.diff`, not in the source files.

### Strengths
- The scoping rule is implemented faithfully:
  - A batch takes `_swapGate`, then `IsRunningWatchOverItsBlock` checks under `_stateLock` that the instance is `Current`, `Running`, and that `ArmedBlock` is the published block (`FileIndex.Watch.cs:3171-3177`, `:3200-3207`).
  - Fault recording (`WatchPump.cs:4397-4411`), catch-up completion (`:4352-4363`), subscriber announcement (`:4324`) and checkpoint-loss recording (`WatchCheckpointLoss.cs:3695`, `:3722`) are all instance-scoped.
- The R2 start path is correct:
  - The `Starting` instance and its cancellation source are registered under the lock, with a `_disposed` recheck, before the source runs (`WatchDrive.cs:3944-3957`).
  - The publish check is the linearization point (`:3993-4010`).
  - When the handle comes back after the instance stopped being `Current`, the start path disposes it, completes the drain and throws `OperationCanceledException` (`:3924-3936`).
- The handle has a single owner. The pump's `finally` disposes it and then completes `Drained` (`WatchPump.cs:4073-4113`), and `ThrowOnSecondDispose` with the `DisposeCount` asserts pins this.
- Disposal sets `_disposed` under `_stateLock` before cancelling the token and draining (`FileIndex.cs:4675-4722`), so no instance can register after the drain sweep.
- The R4 gate order holds: the lifecycle gate is held from entry to exit, then `_rescanGate`, and production holds neither `_swapGate` nor `_stateLock` (`FileIndex.Rescan.cs:2658-2712`). The restart helper passes `gateHeld: true` (`RescanRestart.cs:2846`). I found no lock-order cycle: lifecycle, then `_rescanGate`, then `_swapGate`, then `_stateLock`.
- Nothing runs inline under `_stateLock`. Every completion settled under the lock is `RunContinuationsAsynchronously`, and `RequestStop` and `CompleteDrain` run outside it.
- `AwaitQueuedAsync` (`DriveRuntime.cs:2508-2524`) meets the "no `Task.WaitAsync`" requirement.
- Mutation testing killed 6 of 8 mutations. The added `RetiredInstanceFault_IsNotRecorded` test is the only one that exercises the fault-scope check for real.

### Issues

#### Critical (Must Fix)
None.

#### Important (Should Fix)
1. **Stop on a drive that is not watching must throw `InvalidOperationException`; the spec governs over the brief (decision d).**
   - Spec section 3 (lines 957-962): "Single-drive forms ... throw `InvalidOperationException` where the batched form reports `NotApplicable`", and `NotApplicable` for stop means "not watching".
   - Spec 2.6.4 (lines 445-447) defines watching for stop: "X counts as watching for stop when `WatchRequested` is set or an instance exists."
   - After the first stop in `Stop_RethrowsOutstandingFaultOnce`, neither holds, so the second stop must throw `InvalidOperationException`. `WatchDrive.cs:3798` documents "completes without effect", and the test at `FileIndexPerDriveWatchTests.cs:889` pins that.
   - This is plan-mandated: the brief's test text conflicts with the spec. The fix is to throw when `!WatchRequested && Current is null && Retiring is null`, and to change the test to assert the throw.
   - The report's "left to B7" is not supported: B7 adds the batched forms, not the single-drive contract.
2. **A rescan cancelled while it awaits the old watch's teardown silently loses a healthy watch (decision c's side effect).**
   - `RetireWatchForRescanAsync` retires `Current` and then awaits the drains with the rescan's token (`RescanRestart.cs:2795-2808`). Cancellation there leaves the method without restarting.
   - The drive then has `WatchRequested = true`, no `Current`, no `WatchFailureMessage` and no `WatchFaulted`. It reads `NotStarted`, and journal changes stop being applied, so the consumer's live watch disappears silently.
   - This breaks the spec's failed-scan rule (2.6.4 lines 478-480: "a drive that was healthy resumes from its old cursor"). The code already applies that rule when the swap throws (`RescanRestart.cs:2748-2762`), just not in this window.
   - Fix, either:
     - await the drain unbounded (it is already bounded by `PumpStop`) and check the token only before production; or
     - run the same healthy-drive restart on cancellation in this window.
   - Add a regression test.
3. **The `wait-async` mutation survivor marks a missing test, not a redundant line.**
   - The brief requires that "the caller's token and the disposal token reach the wait through a token registration ... not through `Task.WaitAsync`" (spec 2.6.8).
   - `PumpFaultSettlesWaiter_ContinuationNotInline` (`Lifecycle.cs:563-596`) exercises only the fault path, where the slot's waiter is already `RunContinuationsAsynchronously`, so it cannot tell the two apart.
   - No test cancels a token from a flagged stack. The fix is a test where a `Changed` handler sets the `[ThreadStatic]` flag, cancels the token of a pending `WaitForCatchUpAsync(Y)` (and of a pending `StopWatchingAsync(Y)`, spec row 1339), and clears the flag; the awaiter records the flag, which must be false.
   - The implementation looks correct; the requirement simply has no regression detector.
   - The other survivor, `no-disposal-link`, really is redundant in B1. `_disposed` is set under `_stateLock` before disposal retires every `Current` (`FileIndex.cs:4675-4697`, `Disposal.cs:2257-2279`), and `RegisterStartingInstance` rechecks `_disposed` under the same lock (`WatchDrive.cs:3949`). No test is missing there.

#### Minor (Nice to Have)
1. **Decision (a), `RefusedStartFault`, is an acceptable reconciliation of a brief-versus-spec conflict, but it has inconsistencies.**
   - The conflict: spec 2.6.1 maps "no current instance" to `NotStarted`, while the brief's `StartWatching_SourceStartThrows_ThrowsAndFaultsSlot` requires `Faulted`. The orchestrator should record a ruling.
   - Inconsistencies in the current form:
     - `StopWatchingAsync` clears it (`WatchDrive.cs:3813`), so an unresumable cache-only drive flips to `NotStarted` after a stop while its `WatchFailureMessage` still says it is refused. Spec 2.6.1 says such a drive reads `Faulted`.
     - Deriving the unresumable case from `_cacheOnlyUnresumableCheckpointOrdinals` in `GetWatchCatchUpStateLocked` (`WatchCatchUp.cs:3347-3412`) would remove that case from the field entirely.
     - A source-throw refusal leaves `WatchRequested = true`, so a replacing rescan auto-starts the watch, while the unresumable refusal leaves it false (decision 5). That matches the spec's register-then-invoke order, but it deserves a sentence in the `RescanAsync` remarks.
2. **Decision (b) is sound. A rescan keeps a `Faulted` `Current` in place instead of retiring it (`RescanRestart.cs:2793-2801`).**
   - Retiring it literally would make a failed scan leave the drive `NotStarted`, which contradicts the spec's own "a drive that was faulted stays faulted".
   - It departs from the letter of spec 2.6.4 ("retire `Current` ... before production"), and B6's recovery ("retire `FailedInstance`, produce") will need to agree with it. Record it as a ruling.
   - Also, the `Starting` arm of that condition is dead code, since only a start holding the same gate can create a `Starting` instance.
3. **Dropping `WatchInstance.Handle` and `WatchInstance.Pump` is acceptable for `Handle`, but `Pump` costs something.**
   - `Handle` is fine: the pump closure owns it, and single ownership is tested.
   - Without `Pump`, the fire-and-forget `Task.Run` (`WatchDrive.cs:4007`) turns any unexpected throw in the pump's post-read work into an unobserved task exception. That work includes `RecordCheckpointLossForFaultedDrive`, and `JournalCheckpointCheck.ReadJournal` catches only `IOException`, `Win32Exception` and `OverflowException`. Such a throw would leave the drive `Faulted` with `WatchFaulted` never raised.
   - The old session's pump was awaited by stop, which surfaced such throws. Either keep the task, or catch and route to `WatchFaulted` in `PumpAsync`.
4. **`PumpAsync` swallows a handle `DisposeAsync` failure with no diagnostic (`WatchPump.cs:4100-4108`).** It is deliberate and commented, but consider routing it through `FileIndexOptions.Diagnostics` so a faulty source is visible.
5. **`OldInstanceFault_AfterRestart_DoesNotTouchNewInstance` (`Lifecycle.cs:361-376`) is vacuous by construction.** The restart awaits the old instance's drain, so the old pump has already exited before `LoseChannel` runs. `RetiredInstanceFault_IsNotRecorded` is the test that exercises the check; consider renaming or commenting the former.
6. **A `Starting` instance reports `CatchingUp` through the slot's default (`WatchCatchUpState.cs:5028`).** The spec defines `CatchingUp` only for a `Running` instance, so this is harmless but unspecified. A one-line note would help.
7. **A start whose caller cancels while awaiting the drain of a `Faulted` instance it superseded (`WatchDrive.cs:3887-3901`) discards that instance's `OutstandingFault`.** The drive then reads `NotStarted` with `WatchRequested` still set. This is an edge case, but it is the same silent-state shape as Important 2.

### Assessment
**Task quality: Needs fixes**

Reasoning: The state machine, the scoping rule, R2 and R4 are implemented correctly and cleanly. However, single-drive stop contradicts spec section 3, a cancelled rescan can silently drop a healthy watch in violation of the failed-scan rule, and the spec 2.6.8 token path has no regression test.
