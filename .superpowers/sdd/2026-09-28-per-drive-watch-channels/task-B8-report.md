# Task B8 report: callback reentrancy guard

Worktree `C:\Users\mtsch\MFTLib-worktrees\265-B8`, branch `task/265-B8`, base `ca649bba0a69bf9dc83aeaa60ebb522485063fe6` (verified). Commit `782fc06` "FileIndex rejects a lifecycle call made from inside one of its own handlers". Status: DONE.

## What was implemented

- `MFTLib/Index/FileIndex.Reentrancy.cs` (new): static `AsyncLocal<DeliveryMarker?> s_delivery`; `DeliveryMarker` (names its `FileIndex`, volatile `Active`, `End()`); `Deliver<TArgument>(Action<TArgument>, TArgument)` sets a fresh marker for exactly one handler invocation, and in `finally` clears `Active` and restores the previous `AsyncLocal` value; `RejectInsideHandler(operation)` returns the spec message as `InvalidOperationException` only when the marker is non-null, names this index and is `Active`; `RejectedTask(operation)` / `RejectedTask<T>(operation)` return an already faulted task.
- Exception text is the brief's verbatim (asserted in full by every test): `FileIndex.<Operation> was called from inside a Changed or WatchFaulted handler; it can wait for a watch pump that is blocked in a handler. Queue the call to run after the handler returns, for example with Task.Run.`
- Docs: `Changed` and `WatchFaulted` XML docs state the rule; `AGENTS.md` gets a "Callback reentrancy" bullet under Architecture.

## Raise sites (all funnel through two methods, both now call `Deliver`)

`RaiseChanged` (FileIndex.Watch.cs, one `Deliver` per subscriber per change), called from:
1. `FileIndex.WatchPump.cs` `ApplyWatchBatch` (the pump's `Changed` delivery).
2. `FileIndex.Watch.cs` public `ApplyJournalEntries` (caller's thread; same guard applies).

`RaiseWatchFaulted` (FileIndex.WatchPump.cs, `Deliver(subscribers, fault)`), called from:
1. `WatchPump.cs:141` `AnnounceSubscriberFault` (`Subscriber`).
2. `WatchPump.cs:219` `RecordPumpFault` (`Drive`, `Apply`, `Channel`, and `Recovery` when the recovery already failed).
3. `CatchUp.cs:66` `RunScanOperationAsync` (`CatchUpLost`, raised while X's lifecycle gate is held).
4. `Recovery.cs:225` `EndRecovery` (`Recovery`, after the recovery released its gate).

Grep for `Changed?.Invoke|WatchFaulted?.Invoke|RaiseChanged|RaiseWatchFaulted` shows no other invocation.

## Guarded entry points (check is the first statement, before any await)

- `StartWatchingAsync(char, ct)` (non-async, returns faulted task), `StopWatchingAsync(char, ct)`, `RescanAsync(char, ct)` (async, throw at top so the task is faulted), `DisposeAsync()` (ValueTask; runs before the `_disposed` idempotence check).
- Batched list forms `StartWatchingAsync(IReadOnlyList<char>, ct)`, `StopWatchingAsync(list, ct)`, `RescanAsync(list, ct)`; the no-list forms delegate to them, so they are covered (message names the same operation).
- `WaitForCatchUpAsync(char, ct)`: rejected only when the wait is unsettled (after the cancelled-token and not-watching outcomes, which are settled). `WaitForCatchUpAsync(list, ct)`: rejected when any drive's wait is unsettled; the no-list form delegates.
- Allowed and untouched: queries, `Drives`, `QueryUsnJournalSettings`, settled waits, `BrokerProcess.GrowUsnJournalAsync`.
- Internal restart paths (`StartWatchingCoreAsync(gateHeld: true)`, `StartWatchingWithGateHeldAsync`, `RescanWithGateHeldAsync`, `RunRecoveryAsync`) carry no check.

## Marker does not flow into a queued recovery

`RecordPumpFault` calls `QueueRecovery` (state only), then `RaiseWatchFaulted`, then `StartRecovery` (`Task.Run`). `Deliver` restores the previous `AsyncLocal` value in `finally` on the same synchronous frame, so the `Task.Run` that starts the recovery captures a context with no marker; and the recovery's restart uses internal helpers with no check, so even an active marker could not reject it. `ExecutionContext.SuppressFlow` was not needed. Test `RecoveryQueuedWhileAHandlerRan_StillRestartsTheWatch` (recovery queued, handler ran, watch restarted, `StartsFor('T').Count == 2`).

## OpenProgress decision

`FileIndexOptions.OpenProgress` is an `IProgress<IndexDriveOpened>`, not a `Changed`/`WatchFaulted` handler, and spec 2.6.8 names only those two events; B9-Q1 lets it run without a lock on the settling thread. It is not wrapped in `Deliver` and lifecycle calls from it are not rejected (during `OpenAsync` there is no index handle to call anyway).

## TaskCompletionSource audit (`MFTLib/Index`, Grep `TaskCompletionSource`)

All already created with `TaskCreationOptions.RunContinuationsAsynchronously`; no production change was needed:
- `FileIndex.BatchedCatchUpWait.cs:21-22` `_completion` (batched catch-up wait): RCA.
- `FileIndex.DriveRuntime.cs:141` `_drained` (`Drained`): RCA.
- `FileIndex.DriveRuntime.cs:320` `AwaitQueuedAsync` completion: RCA.
- `FileIndex.Recovery.cs:47` recovery ticket `_completion`: RCA.
- `Snapshot.cs:161` `_completed`: RCA. `Snapshot.cs:341` `_borrowsDrained`: RCA.
- `WatchCatchUpState.cs:57-58` `Waiter`: RCA. `WatchCatchUpState.cs:82` `FaultWaiter`: RCA.

Public waits' cancellation paths (Grep `WaitAsync(` in `MFTLib/Index`; only semaphore gates and `EnumerationWalkLimit` remain, none of them a public task wait):
- `WaitForCatchUpAsync(char)`: `AwaitQueuedAsync(wait, token, DisposalToken)`, registrations that `TrySetCanceled` an RCA source, no `Task.WaitAsync`.
- `StopWatchingAsync`: `AwaitQueuedAsync(drain, token, None)`, same.
- Batched wait: registrations in `BatchedCatchUpWait` (RCA completion).
- `RescanAsync`/`StartWatchingAsync` gate waits use `SemaphoreSlim.WaitAsync(token)` (not a handler-settled path).

## TDD evidence (W40-R1)

Build for all runs: `dotnet build MFTLib.Tests\MFTLib.Tests.csproj -c Release -p:Platform=x64`; test command prefix `dotnet test MFTLib.Tests\MFTLib.Tests.csproj -c Release -p:Platform=x64 --no-build --filter "<filter>"`.

### RED on the base (guard absent), whole class

Command: `dotnet test MFTLib.Tests\MFTLib.Tests.csproj -c Release -p:Platform=x64 --no-build --filter "FullyQualifiedName~FileIndexCallbackReentrancyTests"`
Result: `Failed!  - Failed: 11, Passed: 5, Skipped: 0, Total: 16, Duration: 50 s`. Failing outputs:
- `ChangedHandlerOnX_CallsStopOnY_ThrowsImmediately_YKeepsWatching`: `Assert.IsInstanceOfType failed. StopWatchingAsync was not rejected:` (the stop of Y succeeded).
- `ChangedHandlerOnX_CallsStopOnItsOwnDrive_Throws`: `System.TimeoutException: The operation has timed out.` (own-pump deadlock, bounded).
- `TwoHandlerCycle_EachStopsTheOtherDrive_BothFailAtOnce`: `System.TimeoutException: The operation has timed out.` (the cycle).
- `WatchFaultedHandler_CallsRescanOfAnotherDrive_Throws`: `RescanAsync was not rejected:`.
- `WatchFaultedHandler_CallsStartOfAnotherDrive_Throws`: `StartWatchingAsync was not rejected:`.
- `ChangedHandler_CallsDispose_Throws`: `DisposeAsync was not rejected: System.TimeoutException: The call did not settle.`
- `ChangedHandler_CallsUnsettledWaitForCatchUp_Throws`: `WaitForCatchUpAsync was not rejected: System.TimeoutException: The call did not settle.`
- `ChangedHandler_EveryLifecycleEntryPoint_IsRejectedNamingItself`: `System.TimeoutException: The operation has timed out.`
- `CatchUpLostHandler_CallsRescanOfAnotherDrive_Throws`: `RescanAsync was not rejected:`.
- `MarkerSurvivesAwaitInsideHandler_StopStillRejected`: `StopWatchingAsync was not rejected:`.
- `StopCancellationSettlesOffTheHandlersStack`: failed on the base only because of a test bug (the rescan after the applied batch needed a matching produced cursor); fixed, see mutation C for its real RED.

### RED by uncommitted scratch mutation (tests that pass on the base because they pin behavior the guard must keep)

Each mutation was applied to the finished implementation, built and run, then reverted (files restored byte for byte; final `git status` shows only the commit's files).
- A (rejects settled waits too, and `Search` calls `RejectInsideHandler`): `--filter "FullyQualifiedName~FileIndexCallbackReentrancyTests.ChangedHandler_CallsSettledWaitForCatchUp_ReturnsItsResult|FullyQualifiedName~FileIndexCallbackReentrancyTests.HandlerMayQueryAndGrow"` -> `Failed!  - Failed: 2, Passed: 0`; both `Assert.IsNull failed.`
- B (`DeliveryMarker.End()` does nothing, so `Active` never clears): `--filter "FullyQualifiedName~FileIndexCallbackReentrancyTests.QueuedWorkAfterHandlerReturns_IsAllowed"` -> `Failed!  - Failed: 1`; `System.InvalidOperationException: FileIndex.StopWatchingAsync was called from inside a Changed or WatchFaulted handler; ...`
- C (`AwaitQueuedAsync` body replaced by `await wait.WaitAsync(first)`): `--filter "FullyQualifiedName~FileIndexCallbackReentrancyTests.HandlerCancelsWaitersToken_ContinuationNotInline|FullyQualifiedName~FileIndexCallbackReentrancyTests.StopCancellationSettlesOffTheHandlersStack"` -> `Failed!  - Failed: 2, Passed: 0`; `StopCancellationSettlesOffTheHandlersStack`: `Assert.IsFalse failed. the caller's catch ran off the handler's stack`; `HandlerCancelsWaitersToken_ContinuationNotInline`: `System.IO.IOException: The process cannot access the file 'T-0BADF00D.mlix.lock' because it is being used by another process.` (the inline continuation ran `DisposeAsync` on the pump's own stack; the failure surfaces as a teardown IOException rather than the flag assertion, which is a less direct but real failure).
- D (guard also placed in `StartWatchingWithGateHeldAsync`, `End()` a no-op, `AsyncLocal` not restored): `--filter "FullyQualifiedName~FileIndexCallbackReentrancyTests.RecoveryQueuedWhileAHandlerRan_StillRestartsTheWatch"` -> `Failed!  - Failed: 1`; `Assert.AreEqual failed. Expected:<2>. Actual:<1>. the recovery restarted the watch`.

Tests that need nothing more (fail on the base as listed above): the 10 above plus the 5 mutation-covered ones; `PumpFaultSettlesWaiter_ContinuationNotInline` (B1) is re-run by the whole suite and passes.

### GREEN

`--filter "FullyQualifiedName~FileIndexCallbackReentrancyTests"` -> `Passed!  - Failed: 0, Passed: 16, Skipped: 0, Total: 16, Duration: 333 ms`. Also `...~FileIndexCallbackReentrancyTests|FullyQualifiedName~NativeSeamIsolationTests|FullyQualifiedName~NamespaceBoundaryTests` -> 32 passed.

## Whole suite and aislop (after the last edit)

- `.\scripts\run-coverage.ps1 -NonInteractive`: `Test Run Successful. Total tests: 1901, Passed: 1895, Skipped: 6`, line coverage 97.9%.
- `aislop scan .`: 99 / 100, 0 errors, 5 warnings, all baseline: the four in lane-common.md (`NativeSeamIsolationFixtures.cs:73` and `:79` AsyncFixer01; `CachedBlockDeletionOutcome.cs:8` and `:10` redundant doc) plus the ruled 8-parameter `JournalBrokerHost` constructor warning. An earlier scan showed 8 findings in the new test file (AccessToDisposedClosure, VariableHidesOuterVariable, single-part partial); all fixed.
- CRLF kept on every edited file and both new files; no em-dashes.

## Files changed

New: `MFTLib/Index/FileIndex.Reentrancy.cs`, `MFTLib.Tests/Index/FileIndexCallbackReentrancyTests.cs` (16 tests, 500+ lines with the one-line note at the top explaining why).
Modified: `FileIndex.Batched.cs`, `FileIndex.Rescan.cs`, `FileIndex.Watch.cs`, `FileIndex.WatchCatchUp.cs`, `FileIndex.WatchDrive.cs`, `FileIndex.WatchPump.cs`, `FileIndex.cs` (all `MFTLib/Index`), `AGENTS.md`.

## Self-review, concerns, adjacent notes

- The brief's `HandlerMayQueryAndGrow` builds a `JournalBrokerHost` and `InProcessBroker` directly (the `BrokerProcessTests.CreateHost` helper is private); it uses the same 8-argument constructor as that test.
- `ApplyJournalEntries` (public) raises `Changed` on the caller's thread; the guard therefore also rejects lifecycle calls from a `Changed` handler invoked through it. Consistent with "any callback of this index".
- Guard on the no-list batched forms happens after `AllDriveLetters()`, so on a disposed index those throw `ObjectDisposedException` first; the list forms and single-drive forms check the marker first.
- `[ThreadStatic]` flag tests register their continuation from the test thread (`ObserveCancellationAsync`) before cancelling, since a `Task.Run` awaiter raced the cancel in the first draft.
- Scratch files (`.superpowers/orig`, `mutate.py`, logs) are in the git-ignored `.superpowers` directory of the worktree.

Primary checkout: `git -C C:\Users\mtsch\MFTLib status --short` printed nothing (clean).

## Fix round 1

Commit: "Each handler invocation carries its own delivery marker and every active index is guarded" on `task/265-B8`.

1. Per-subscriber markers: `WatchFaulted` now goes through `DeliverToEach` (GetInvocationList, one `Deliver` per subscriber; `Changed` already iterated per subscriber). A subscriber's exception still stops later subscribers, as a multicast call did.
2. Nested delivery: `DeliveryMarker` keeps its `Outer` marker; `IsInsideHandlerOfThisIndex` walks the chain and rejects when any active marker names this index.
3. The three no-list batched forms (start, stop, rescan) check the guard first. The no-list `WaitForCatchUpAsync(ct)` still resolves its drive list first (it can only reject when a wait is unsettled, which needs the list; on a disposed index it throws `ObjectDisposedException` as any wait does). A batched wait with an already-cancelled token is treated as settled.
4. Every await in the test class and helpers is bounded (`StartBothAsync`, the single start, the helper awaits inside handlers and queued tasks).
5. Minor: the two "ran off" messages now read "ran on the handler's stack". `ObserveCancellationAsync` skips its follow-up call when the continuation ran inline, so mutation C reports the assertion, not a teardown IOException.

### RED evidence, new tests (against the previous commit's production code, tests only added; production stashed)
Command: `dotnet test MFTLib.Tests\MFTLib.Tests.csproj -c Release -p:Platform=x64 --no-build --filter "FullyQualifiedName~FileIndexCallbackReentrancyTests.WatchFaultedSubscriberA_QueuedWorkRunsWhileSubscriberBStillRuns_IsAllowed|FullyQualifiedName~FileIndexCallbackReentrancyTests.NestedDeliveryOnAnotherIndex_StillRejectsACallOnTheOuterIndex|FullyQualifiedName~FileIndexCallbackReentrancyTests.ChangedHandler_CallsNoListFormsWhileTheIndexIsDisposing_GetsTheGuardException"`
Result `Failed!  - Failed: 3, Passed: 0`:
- WatchFaultedSubscriberA...: `Assert.IsNull failed. work subscriber A queued ran after A returned, so it is allowed while B runs`
- NestedDeliveryOnAnotherIndex...: `Assert.IsInstanceOfType failed. StopWatchingAsync was not rejected:`
- ChangedHandler_CallsNoListForms...: `Assert.AreEqual failed. Expected:<FileIndex.StartWatchingAsync was called from inside a Changed or WatchFaulted handler; ...>. Actual:<Cannot access a disposed object. Object name: 'MFTLib.Index.FileIndex'.>`

### RED evidence, mutation-covered tests (uncommitted scratch mutations, each reverted; full literal commands)
All commands start `dotnet test MFTLib.Tests\MFTLib.Tests.csproj -c Release -p:Platform=x64 --no-build --filter "FullyQualifiedName~FileIndexCallbackReentrancyTests.<Test>"`, run with the literal test name below after a build of the mutated source:
- Mutation A (settled waits rejected; `Search` guarded), test `ChangedHandler_CallsSettledWaitForCatchUp_ReturnsItsResult` -> `Assert.IsNull failed.` (line 192); test `HandlerMayQueryAndGrow` -> `Assert.IsNull failed.` (line 347).
- Mutation B (`DeliveryMarker.End()` a no-op), test `QueuedWorkAfterHandlerReturns_IsAllowed` -> `System.InvalidOperationException: FileIndex.StopWatchingAsync was called from inside a Changed or WatchFaulted handler; ...`
- Mutation C (`AwaitQueuedAsync` uses `wait.WaitAsync(first)`), test `HandlerCancelsWaitersToken_ContinuationNotInline` -> `Assert.IsFalse failed. the continuation ran on the handler's stack`; test `StopCancellationSettlesOffTheHandlersStack` -> `Assert.IsFalse failed. the caller's catch ran on the handler's stack`.
- Mutation D (guard also inside `StartWatchingWithGateHeldAsync`, `End()` a no-op, marker not restored), test `RecoveryQueuedWhileAHandlerRan_StillRestartsTheWatch` -> `Assert.AreEqual failed. Expected:<2>. Actual:<1>. the recovery restarted the watch`.
The other 10 tests' RED (base, guard absent) is in the first report section.

### Verification after the last edit
- Targeted: `...--filter "FullyQualifiedName~FileIndexCallbackReentrancyTests|FullyQualifiedName~FileIndexBatched|FullyQualifiedName~FileIndexPerDriveWatchTests|FullyQualifiedName~NativeSeamIsolationTests|FullyQualifiedName~NamespaceBoundaryTests"` -> `Passed! Failed: 0, Passed: 102`; the class alone: 19 passed.
- `.\scripts\run-coverage.ps1 -NonInteractive`: Total tests 1904, Passed 1898, Failed 0, Skipped 6.
- `aislop scan .`: 99/100, 5 warnings, all baseline (4 from lane-common.md plus the ruled 8-parameter host constructor).
- Primary checkout `git -C C:\Users\mtsch\MFTLib status --short`: see the reply.

## Fix round 2

Commit "The no-list catch-up wait checks the handler guard first". `WaitForCatchUpAsync(CancellationToken)` now returns the guard's faulted task before resolving the drive list when the index is disposed and the call is inside a handler (a disposed index settles no wait). While not disposed, the list form's unsettled-wait rule applies as before.

RED (fix absent): `dotnet test MFTLib.Tests\MFTLib.Tests.csproj -c Release -p:Platform=x64 --no-build --filter "FullyQualifiedName~FileIndexCallbackReentrancyTests.ChangedHandler_CallsNoListWaitWhileTheIndexIsDisposing_GetsTheGuardException"` -> `Failed!  - Failed: 1`; `Assert.AreEqual failed. Expected:<FileIndex.WaitForCatchUpAsync was called from inside a Changed or WatchFaulted handler; ...>. Actual:<Cannot access a disposed object. Object name: 'MFTLib.Index.FileIndex'.>`
GREEN: same command after the fix passes; `--filter "FullyQualifiedName~FileIndexCallbackReentrancyTests|FullyQualifiedName~FileIndexBatched"` -> Passed 48, Failed 0. All awaits in the test are bounded (`WaitAsync(HangGuard)`, `BlockOn`).
`run-coverage.ps1 -NonInteractive`: Total 1905, Passed 1899, Failed 0, Skipped 6. aislop 99/100, baseline warnings only. Primary checkout clean.

## Fix round 3

Commit "The no-list catch-up wait inside a handler never reports disposal ahead of the guard". The no-list `WaitForCatchUpAsync(CancellationToken)` no longer pre-checks `_disposed`; it runs the drive-list resolution and the list form in a try, and an `ObjectDisposedException` seen while inside a handler of this index becomes the guard's faulted task (no lock). Seam: `internal Action? BeforeNoListWaitResolvesDrivesForTest`, invoked before the drive list resolves.

Test `ChangedHandler_CallsNoListWaitWhileDisposalBeginsBeforeTheDriveListResolves_GetsTheGuardException`: the seam starts `DisposeAsync` on a thread started with `UnsafeStart` (no execution-context flow, so disposal is not itself rejected) and joins it (bounded), so `_disposed` is set before resolution; no timing.
RED (fix absent, seam present): `dotnet test MFTLib.Tests\MFTLib.Tests.csproj -c Release -p:Platform=x64 --no-build --filter "FullyQualifiedName~FileIndexCallbackReentrancyTests.ChangedHandler_CallsNoListWaitWhileDisposalBeginsBeforeTheDriveListResolves_GetsTheGuardException"` -> `Failed!  - Failed: 1`; `Assert.AreEqual failed. Expected:<FileIndex.WaitForCatchUpAsync was called from inside a Changed or WatchFaulted handler; ...>. Actual:<Cannot access a disposed object. Object name: 'MFTLib.Index.FileIndex'.>`
GREEN: same command passes; `--filter "FullyQualifiedName~FileIndexCallbackReentrancyTests|FullyQualifiedName~FileIndexBatched"` -> Passed 49, Failed 0. Round 2's already-disposed test still passes through the same catch.
`run-coverage.ps1 -NonInteractive`: Total 1906, Passed 1900, Failed 0, Skipped 6. aislop 99/100, baseline warnings only (a `!` warning from the first draft was removed). Primary checkout clean.
