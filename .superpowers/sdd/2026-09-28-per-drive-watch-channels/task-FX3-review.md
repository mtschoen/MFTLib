### Plan Compliance

- Issues found: The code change is plan compliant, but the reported aislop result does not meet the task gate. `task-FX3-report.md:68` and `task-FX3-report.md:128` report five warnings, including `MFTLib/Index/JournalBrokerHost.cs:46`; `reviewer-common.md:13-15` permits only the four named baseline warnings. The report calls the fifth warning "ruled", but no orchestrator ruling accepting it was supplied in the dispatch.
- The implementer's cause is correct. `MFTLib.Tests/TestSupport/WatchHarness.cs:70` subscribes `RecordFault` while constructing the harness, before the test subscribes its handler at `MFTLib.Tests/Index/FileIndexWatchRecoveryTests.cs:142`. Production snapshots the multicast delegate and calls `DeliverToEach` at `MFTLib/Index/FileIndex.WatchPump.cs:226-232`; `DeliverToEach` walks `GetInvocationList()` in order and calls each handler synchronously at `MFTLib/Index/FileIndex.Reentrancy.cs:35-45`. There is no per-subscriber await. The harness creates a `RunContinuationsAsynchronously` waiter at `MFTLib.Tests/TestSupport/WatchHarness.cs:106-118`, and its first subscriber completes matching waiters at `MFTLib.Tests/TestSupport/WatchHarness.cs:262-292`. The test continuation can therefore run before the later test subscriber assigns its old nullable capture. This matches the reported Linux diagnostic of `null`, so the test, not production, was wrong.
- Production behavior is unchanged and the new assertion still checks the intended instant. `RecordPumpFault` limits recovery to `Drive` and `Apply`, queues that recovery before raising the fault, and starts it only after the raise returns at `MFTLib/Index/FileIndex.WatchPump.cs:216-222`. `QueueRecovery` is the operation that stores `runtime.Recovery` at `MFTLib/Index/FileIndex.Recovery.cs:85-99`. A `Channel` fault does not call it, so the initial channel fault in this test cannot find a recovery that it queued. The handler now records that exact lookup in its own completion at `MFTLib.Tests/Index/FileIndexWatchRecoveryTests.cs:141-147`, and the test asserts the recorded value at `MFTLib.Tests/Index/FileIndexWatchRecoveryTests.cs:150-157`.
- Every changed test keeps or strengthens its prior assertion. `BatchedWait_SettledByPumpFault_ContinuationNotInline` captures the same thread-static flag immediately after the failed wait and returns it after stopping the other drive at `MFTLib.Tests/Index/FileIndexBatchedOperationTests.cs:294-300`; line 290 still requires false. `PumpFaultSettlesWaiter_ContinuationNotInline` does the same at `MFTLib.Tests/Index/FileIndexPerDriveWatchTests.Lifecycle.cs:420-425`, with the false assertion at line 416. `TokenCancelledInsideAHandler_NeverRunsTheWaitersContinuationInline` now returns both observed flags and asserts both positions at `MFTLib.Tests/Index/FileIndexPerDriveWatchTests.Lifecycle.cs:129-144`. None can pass if the continuation runs on the flagged stack. `ChannelFault_NoRecovery` now also fails on a missing or misclassified event by timing out its handler-owned signal, and fails when a recovery is present because the recorded boolean is true (`MFTLib.Tests/Index/FileIndexWatchRecoveryTests.cs:141-153`).
- The sweep is complete for the stated defect. The only remaining syntactic case that subscribes a handler, awaits the harness fault waiter, and later reads the handler capture is `RecoveringPublishedBeforeWatchFaultedRaised` at `MFTLib.Tests/Index/FileIndexWatchRecoveryTests.cs:239-257`. It is ordered: recovery is started only after `RaiseWatchFaulted` returns from all subscribers (`MFTLib/Index/FileIndex.WatchPump.cs:219-222`), and `WaitForRecoveryAsync` awaits that ticket with a hang guard (`MFTLib.Tests/TestSupport/WatchHarness.cs:221-231`) before line 257 reads the capture. Other subscriber observations use their own completion signals, including `MFTLib.Tests/Index/FileIndexMidSessionCheckpointLossTests.cs:291-305`, `MFTLib.Tests/Index/FileIndexWatchRecoveryTests.cs:418-430`, and `MFTLib.Tests/Index/FileIndexWatchRescanTests.cs:552-563`. No unordered instance remains.
- The changed synchronization follows the test rules. New waits are bounded by `HangGuard` at `MFTLib.Tests/Index/FileIndexWatchRecoveryTests.cs:153`, `MFTLib.Tests/Index/FileIndexBatchedOperationTests.cs:290`, and `MFTLib.Tests/Index/FileIndexPerDriveWatchTests.Lifecycle.cs:135`; lifecycle operations retain cancellation tokens at `MFTLib.Tests/Index/FileIndexPerDriveWatchTests.Lifecycle.cs:121-130,422-424`. Ordering is established by task completions and the existing test gate at lines 124-138, with no real-time delay in any changed method.
- Cannot verify from diff: The reported Linux diagnostic loop, repeated green runs, whole-suite results, and aislop output were not rerun, as required by the review instructions. The controller should verify whether an explicit ruling exists for `MFTLib/Index/JournalBrokerHost.cs:46`; without one, the reported quality gate is not green.

### Strengths

- The fix targets the actual missing happens-before edge without changing production or weakening the behavior assertion (`MFTLib.Tests/Index/FileIndexWatchRecoveryTests.cs:139-153`).
- The handler filters both fault kind and drive, uses `RunContinuationsAsynchronously`, and bounds the await, making failure modes precise (`MFTLib.Tests/Index/FileIndexWatchRecoveryTests.cs:141-153`).
- The adjacent refactors remove nullable shared captures while preserving the exact observation point and assertion messages (`MFTLib.Tests/Index/FileIndexBatchedOperationTests.cs:290-300`; `MFTLib.Tests/Index/FileIndexPerDriveWatchTests.Lifecycle.cs:135-144,416-425`).

### Issues

#### Critical (Must Fix)

- None.

#### Important (Should Fix)

- Quality gate evidence contains an unapproved fifth aislop warning. The task gate permits only four named baseline warnings, but `task-FX3-report.md:68,128` reports `MFTLib/Index/JournalBrokerHost.cs:46` as a fifth. Supply the missing orchestrator ruling or bring the result back to the stated gate.

#### Minor (Nice to Have)

- None.

### Assessment

Task quality: Needs fixes
Reasoning: The test-only implementation is correct, assertion-preserving, synchronized by signals, and complete for the defect. Approval is blocked only because the supplied validation evidence does not satisfy the explicit aislop gate and no accepting ruling was provided.
