### Findings
F1: ADDRESSED. Checked against the fix diff and base c1d43784 stop sites (RescanTests base lines 72, 99, 124, 201, 236, 266, 293, 442, 466, 490, 557; FailedRescan base 113, 160, 183, 230; CheckpointLoss base 61; FaultDuringProduction base 110).
- FileIndexWatchRescanTests: RestartsOnlyTheRescannedDrive (T,U), NeverStopsTheOtherDrive (T,U), Rescan_OfT_LeavesUsPumpRunning (T,U), DropsABatchThePumpHadAlreadyAccepted (T; only T is started), WhoseSwapFails (T,U; T was restarted from its old cursor and is watching, so completes is right), WhoseSwapAndWatchRestartBothFail (U), WhoseWatchRestartFailsAfterTheSwapSucceeded (U), CacheDeclinedDrive_AdoptsIt (T,U), WhoseScanFails (U completes; T throws InvalidOperationException because T is blockless and never watched, correct form), RescannedASecondTime (T,U), WhenOnlyInitialDriveFaults (T completes after the U rethrow). All present.
- FileIndexWatchFailedRescanTests: FailedRescan_PreservesFault (U added; T keeps its base-style rethrow assertion), HealthyDrive_ProducerFailure (T,U), SuccessfulRetry (T). CancelledPublication already asserted the fault rethrow (base 160) and is unchanged.
- FileIndexWatchRescanCheckpointLossTests: T stop added beside the U rethrow. RecoveryFaultTests: E stop added to the earlier-outstanding-fault test; the other tests already stopped.
- FaultDuringProduction already ends with the T stop (line 67 at head). Base tests with no trailing stop (NoWatch, CancelledSwapGate, StreamEnds, Unhandled) unchanged, correct.
- Unpinned T stop after a failed restart in the two restart-failure tests: right call. The spec (2.6.4) clears Current on start failure and does not say whether stop then throws InvalidOperationException or returns; pinning either would invent contract. The BothFail comment ("T's stop after a failed restart is left unpinned: the contract does not say") describes current state, no history.
- Fix touches test files only (4 files, +27 lines, no production). Report names the covering classes, command and result (five classes 29 passed; whole suite 1604/1598 passed/0 failed; aislop 99/100 baseline); not re-run.

### New Breakage in the Fix Diff
Critical: none.
Important: none.
Minor:
- FileIndexWatchRescanTests.cs, RescanAsync_WhoseWatchRestartFailsAfterTheSwapSucceeded (end of test, ~line 275): the report says the T-stop omission is commented in both tests, but this one has no comment; only the BothFail test does. Add the one-line "T's stop after a failed restart is unpinned: the contract does not say" note.

### Out-of-Scope Observations
- Spec gap on stop after a failed restart remains for the controller to settle (earlier review concern 1); nothing in the fix worsens it.

### Verdict
All findings addressed
