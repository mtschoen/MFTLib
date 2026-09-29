### Task D1: AGENTS.md and CHANGELOG

**Files:** `AGENTS.md`, `CHANGELOG.md`.

AGENTS.md paragraphs rewritten from the implementation (spec section 4 list, confirmed against the base commit):
- Architecture, MFTLib, "Checkpoint loss": keep the journal-read and `DetectedDuring` rules; replace everything from "The same check also runs mid-session, from `FileIndex.WatchPump`" to the end of the bullet (sessions, reclaim, restart, `_unreportedWatchFaults`, `ResumeDriveAfterRescanAsync`, PR 230 and 241 handling) with the per-drive fault, recovery and `Recovery` kind rules, the rule that a recovery rescan keeps a `LiveWatch` loss, and the lost-catch-up rule (proven by the journal on the host; `ScanCatchUp` detection, three consecutive lost catch-ups stop automatic recovery, a scan whose catch-up holds resets the count, the report carries the journal-size suggestion).
- "Watch start readiness": whole bullet, replaced by one sentence (`StartWatchingAsync(X)` returns once X's channel is connected and `StartWatch` is written).
- "Watch and catch-up lifetime": whole bullet, rewritten per drive with `Recovering`.
- "VolumeBroker": whole bullet, rewritten for `BrokerProcess`, control pipe, owned channel open, drive channels, the parse-thread allocator and its per-chunk rebalance, the `CatchUpLost` frame, heartbeats (control unconditional, independent per pipe), watchdog, stall limit, request ids.
- Add a "Per-drive state machine" bullet under FileIndex (watch instances, linearization points, recovery tickets, the reentrancy guard) from the plan's section of that name.
- Project list, "MFTLibTestExtensions": `BrokerTestHarness`.
- Test coverage, journal isolation: "may run on the watch-pump thread" becomes "may run on any drive's pump thread, concurrently for different drives".
- Add a "Lock order" bullet under FileIndex (spec section 5).

CHANGELOG "Unreleased": one entry per public change (every row of spec section 3; the deleted types; the frame renumbering; `RunBroker(string?)`; `MftRecordBatchSource` and `UsnJournalCatchUpSource` shapes; the thread allowance and cancellation on the native parse export; `ParseThreadAllowance`; the `CatchUpLost` frame; `JournalCatchUpLostException`; `MftBlockProduceResult.CatchUpLoss` and `BrokerDriveScanResult.CatchUpLoss`; `DriveStatus.ConsecutiveLostCatchUps`; `FileIndex.LostCatchUpRecoveryLimit`; `JournalCheckpointLossDetection.ScanCatchUp`; `WatchFaultKind.CatchUpLost` and `Recovery`; `WatchCatchUpState.Recovering`; `StreamRecords` and `ReadRecordBatches` shapes; `JournalBatchSource` and `IBrokerOperationReporter`; `BrokerTestHarnessOptions`; concurrent `OpenAsync` and `IndexDriveOpened.SettledCount`; `BlockFile.Flush` and `BlockWriter.Complete`; issue 252 closed). Describe what exists; no history notes.

- [ ] Write; grep AGENTS.md and CHANGELOG.md for every deleted identifier (`JournalBrokerClient`, `JournalBrokerScanSession`, `ScanSessionTestHarness`, `WatchStreamNotRunningException`, `DriveWatchFailure`, `ReadyOnFirstMoveWatchStream`, `WatchSession`, `_swapGate`, `_rescanGate`, `ArmEpoch`, `EndWatchAck`, `SendStartWatchAsync`, `StopLiveWatchAsync`, `QueryVolumesAsync`, `ArmScanAndCatchUpAsync`, `BrokerScanResult`, `BrokerDied`, `WriteWarning`), one search per name; only CHANGELOG deletion entries may name them. Commit.

