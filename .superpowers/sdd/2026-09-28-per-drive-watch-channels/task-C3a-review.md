### Plan Compliance
Issues found (one Important, process/accounting; otherwise plan compliant).

Port accounting (base c1d43784 vs head 874c951, [TestMethod] and [DataRow] cases):
- Watch.cs: base 12 methods. Ported unchanged by name: StartWatch_StreamsBatches_UntilCancelled, StartWatch_ZeroCursor_QueriesCurrentCursorBeforeWatching, StartWatch_NoWatchSourceConfigured_EmitsErrorFrame (Watch.cs new :11, :46, :64). Renamed exactly as the brief names them: WatchChannel_SourceCompletesNaturally_ClosesWithoutError (:78), WatchChannel_BatchWriteHitsBrokenPipe_EndsQuietly (:91), WatchChannel_FaultWithBrokenPipe_ErrorFrameUnsendable_EndsQuietly (:123), WatchChannel_LeadingCaughtUpWriteHitsBrokenPipe_EndsQuietly (:151). Dropped: EndWatch_StopsWatchTasks_AndWritesEndWatchAck (reason holds: no EndWatch/EndWatchAck on the new wire; its "source is cancelled when the client goes" facet is still covered by StreamsBatches disposing the pipe and awaiting `cancelled`, Watch.cs:40-41). Moved to C3b: the 4 ServeAsync_* cases, as briefed. 12 = 7 + 1 + 4. Accounted.
- WatchCatchUp.cs: 6 base, 6 new, same names. Accounted.
- WatchRecovery.cs: 8 base; 6 ported by name; 2 ServeOnce_ScanProgress_* declared moved to C3b (see Important 1). Accounted in the commit message only.
- WatchFailureClassification.cs: 4 methods. 3 ported with all 10 + 2 + 2 DataRows intact (`RetainedCursor`, `LostCursor`, `UnknownJournal`). `NoQueryableCachedCursor` keeps the "C:0:0:1" case as `default` cursor (:58-72); the two DataRows ":7:100:1" and "invalid:7:100:1" are dropped. Reason holds: they exercised parsing of the old watch-spec string, which no longer exists (drive comes from OpenChannel).
- WatchDiagnosticsFilter.cs: 5 base, 5 new, same names. Accounted.
- The six "must survive" cases exist under the briefed names. Each of the three broken-pipe cases asserts, through AssertOnlyThatChannelEndedAsync (Watch.cs:185-195), that the failed pipe is closed (ReadFrameAsync null), that a sibling channel then delivers a batch released after the failure, and that the control pipe answers a QueryVolume while `harness.Serve` is not completed (WatchSupport.cs:112-121). SourceCompletesNaturally asserts no Error frame (Watch.cs:88-89) and control liveness.

Other checks:
- Rescan wording: kept. WatchFailureClassification.cs:31-38 asserts the exact decorated string ending "needs a rescan before it can be watched again"; WatchRecovery.cs:35-38 and :171 assert "rescan". Retained-cursor and unknown-journal cases still assert the undecorated message (:15-24, :41-52).
- No assertion weakened that I can see: the broken-pipe cases assert strictly more than base (base only awaited serveTask). Removed assertions are only EndWatchAck/arm-epoch ones, which the contract removes. Frame-order proofs now use ReadToEndAsync on a finite source, which is stronger for "no extra frame" (e.g. WatchCatchUp.cs:70-84).
- Diagnostics: the base tests (read at c1d43784) also never read the log; they proved the filter by observing which frames ship. The new ones assert the same frames (WatchDiagnosticsFilter.cs:60-63, :93-96, :135-138 and both ArmAndScan cases). The discarding writer (`ReplaceWriterForTest(new BrokerDiagnosticsWriter(_ => { }, ...))`, :24) only removes disk writes; it proves nothing less about the self-filter. `ResetDiagFilterSeams` still restores in finally.
- No test can start a real elevated process or call Environment.Exit: none touches BrokerLauncher, ElevationUtilities or DefaultElevatedEntryRunner; connections are in-memory (`BreakableDrivePipe` wraps `ConnectInMemoryAsync`, WatchSupport.cs:130-137).
- Bounded waits / time: every await is either HostChannelHarness.ReadFrameAsync/ReadToEndAsync (10 s deadline, harness :109-128), `.WaitAsync(HangGuard)`, or `GatedWatch`/`FaultingAfterGate` `WaitAsync(cancellationToken)` cancelled at host teardown. `Task.Delay(Timeout.Infinite, token)` (WatchSupport.cs:52) is not a real-time wait. No sleeps, no elapsed-time assertions. These host tests need no FakeTimeProvider directly (the harness injects one).
- Parallelism: class-level `[DoNotParallelize]` present (WatchSupport.cs:10) and covers JournalCheckpointCheck.OverrideJournalForTest and BrokerDiagnostics/BrokerDiagnosticsLogFilter global state.
- aislop: implementer reports 99/100, only the four baseline plus the two known base warnings; not re-run.

Cannot verify from diff: (a) that C3b's brief actually contains the two ScanProgress cases; (b) that HostChannelHarness.OpenWatchChannelAsync for a broken-on-connect pipe does not itself read the pipe (the test passed 6 times per the report, so likely fine).

### Strengths
- Faithful, mechanical port: every base case is ported, renamed or declared, with valid reasons for the drops.
- Broken-pipe cases go beyond base by proving isolation of sibling channel and control session, and use deterministic gating (break, then release gate, then await WriteFailureObserved).
- Sentinel/finite-source pattern makes frame-sequence assertions exact.
- No production or harness changes.

### Issues
#### Critical (Must Fix)
None.

#### Important (Should Fix)
1. Unbriefed move of two cases. `ServeOnce_ScanProgress_EmitsFinalFrameImmediatelyBeforeScanReady` and `ServeOnce_ScanProgress_ThrottlesNonFinalFrames` (base WatchRecovery.cs:249, :291) are neither ported nor dropped here, and the brief lists only the four ServeAsync cases as C3b's. The commit message declares them moved, but nothing in the brief guarantees C3b picks them up; grep of this branch finds no ScanProgress_ test. They are scan-operation cases (and, per the brief, `ScanProgress` frames now carry no drive letter, so they need a rewrite). Fix: the controller must confirm C3b's brief (or another task) owns them, or C3a ports them as scan-channel tests. Until confirmed, a base case is unaccounted for.

#### Minor (Nice to Have)
1. WatchSupport.cs:8-10 declares `[TestClass]`/`[DoNotParallelize]` because the main partial is absent; C3b's main partial will duplicate them (CS0579). The implementer notes this; the merge must delete one set. The comment at :7-9 should be updated then, since it describes the attributes as belonging to this file.
2. WatchFailureClassification.cs:58-72 (`NoQueryableCachedCursor`) lost its DataRow attribute shape; fine, but the remaining single case checks only `default`. A second row with a non-zero journal id and zero USN (`new UsnJournalCursor(7,0)`) would keep the "zero cursor is not queryable" edge if that is still defined by the contract.
3. WatchDiagnosticsFilter.cs:6-8 keeps hard-coded `C:\broker-diag-tests` strings; inherited from base and synthetic, but the WatchSupport comment could say they are never touched on disk.

### Assessment
Task quality: Needs fixes
Reasoning: The port is complete, faithful and stronger than base on the required broken-pipe isolation, with valid drop reasons; the one blocker is the unbriefed hand-off of the two ScanProgress cases, which leaves two base cases unowned until the controller confirms C3b takes them.
