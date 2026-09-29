12:Task A3: minor (deferred): MFTLibTestExtensions.csproj:3-5 comment sentence about friend-listing is a weak leftover; optional trim
19:Task A2: minor (deferred): stale-writer race in TryEnqueue during test-seam swap loses a line uncounted and can hang FlushAsync (BrokerDiagnosticsWriter.cs:464-474)
20:Task A2: minor (deferred): LongRunning on an async drain loop is pointless (BrokerDiagnosticsWriter.cs:459)
21:Task A2: minor (deferred): queued lines lost at elevated broker exit - needs owner decision on a bounded shutdown flush (no plan task covers it)
22:Task A2: minor (deferred): no RED capture; sink-failure line worded "buffer full" per brief
24:Task A5: minor (deferred): ragged comment reflow FileIndex.Watch.cs StartWatchingAsync doc (B1 rewrites); "Internal" redundant wording in LiveWatchItem.cs / JournalBrokerClient.LiveWatch.cs (C1 deletes)
26:Task A5: minor (deferred): commit 8906157 says "C6 ports the cases" for all 11 files though C6's list omits six (Arming, Arming.LastDrive, PerDriveArm, PerDriveArm.Recovery, AbandonedStartTeardown, StartSendCancellation); tranche B audit must name every case as ported or dropped
33:Task A1: minor (deferred): MftParseControl declared inside pack(1) region while LoadSharedInt32 needs 4-byte alignment; state requirement or move struct (mft_api.h:73-76)
34:Task A1: minor (deferred): ParseThreadAllowance doc does not say one allowance attaches to one running parse (throws InvalidOperationException)
35:Task A1: minor (deferred): chunk-count recorder is process-global on the production path, mutex per chunk; comment should say hooks need DoNotParallelize (test_hooks.cpp:59-72) - RELEVANT to C1/C5 concurrent scans
36:Task A1: minor (deferred, plan-mandated brittleness): tests compare hardware_concurrency with Environment.ProcessorCount; differ under affinity/job limits/DOTNET_PROCESSOR_COUNT
37:Task A1: minor (deferred): no test for a token cancelled after StreamRecords returned
41:Task A1: minor (deferred): ShouldForceCancel runs before the control null check on every cancel check (test_hooks.cpp:90-93), negligible
50:Task A4: minor (deferred): negative-bound guard MftVolume.Journal.cs:271 has no test (uncovered line; matters for V1 100 percent coverage)
58:Task B1: minor (deferred): 7 items, see task-B1-review.md "Minor" section (final review triages)
63:Task C1: minor (deferred): 8 items, see task-C1-review.md "Minor" section; includes Environment.Exit hazard if the real-pipe runner test times out
69:Task B1: minor (deferred): stop check trusts the retiring-instance slot, narrow race can leave it stale; comment claims old watch shuts down promptly (false inside a handler); new handler test releases its held pump only when assertions pass; rescan wait for old drain is unbounded if old pump hangs (spec permits)
76:Task C1: minor (deferred): OCE-to-Error path in Session.cs has no failing-first test; RunBroker rethrows after the flush
77:Task C1: minor (deferred, out of scope, REAL): huge frame-length prefix (Frames.cs:73-79) throws OverflowException/OutOfMemoryException instead of InvalidDataException and ends the session - candidate for C2/C5 or final review
81:Task C1: minor (deferred): flush test has no gate release in finally if the IsCompleted assert fails; WhenAny no longer surfaces a flush fault
91:Task B4: minor (deferred): start issued during a rescan of an UNRESUMABLE drive (must judge the fresh block) is uncovered - candidate for B5; StopWatchingAsync assertions check only DriveWatchFaultException, base pinned the original exception type (cause no longer proven to surface); "scan fails without throwing" test does not say what it pins and keeps a history note; ArmedUsn - 50 needs a comment
97:Task B3: review (sonnet, task-B3-review.md): 0 Critical, 1 Important, 3 Minor. Needs fixes. Important: ~12 ported tests dropped their trailing StopWatchingAsync nothing-to-rethrow assertion. Port accounting complete. Concern 1 (old fault lost when restart fails after replacing a faulted drive) = SPEC GAP, stop after a failed restart is unspecified -> owner question at V1. New test red evidence is test-side only (production mutation was denied by classifier; owner decision pending).
98:Task B3: minor (deferred): 3 items, see task-B3-review.md
103:Task C3a: minor (deferred): 3 items, see task-C3a-review.md
106:Task B2: minor (deferred): caller-cancelled start assertions go beyond spec text and old source-token-cancelled check gone; no per-drive analogue for DriveCaughtUpItem_ForAFaultedDriveIsIgnored; no port of a pending wait surviving start-token cancellation; some awaits bounded only by the test-context token; SPEC GAP: spec silent on whether a fresh start discards a faulted instance's outstanding fault
108:SPEC GAPS for owner at V1: (1) what stop does after a failed restart (B3 concern 1); (2) whether a fresh start discards a faulted instance's outstanding fault (B2 minor 5, also B1 minor 7)
111:Task B3: minor (deferred): WhoseWatchRestartFailsAfterTheSwapSucceeded has no comment for the omitted T stop; both restart-failure tests get their T-stop assertion once the owner decides the spec gap
123:Task C3b: minor (deferred): unknown-kind and unknown-cause rejection tests assert only the exception type; BrokerProtocolTests.Scan.cs 525 lines no note; no golden for a non-empty JournalBatch or journal-entry encoding; CatchUpLost golden pins only cause 0; others in task-C3b-review.md
128:Task C3b: minor (deferred): TimeoutException from the helper's finally can mask the body's original exception (DefaultElevatedEntryRunnerTests.cs:317-320)
130:MERGE FIX by orchestrator: commit 29b8a6a removes duplicate [TestClass]/[DoNotParallelize] from JournalBrokerHostTests.WatchSupport.cs (unreviewed 3-line merge resolution; final review covers it). Suite + aislop on this head running: scratch/coverage-c3b-merged.log, aislop-c3b-merged.log
138:Task C2: minor (deferred): 8 items, see task-C2-review.md
154:Task C2: minor (deferred): DisposeAsync "never throws" doc overclaims (throwing Ended handler or channel close escapes) - doc fix folded into round 2; two host-fault tests dispose without a bound; in-memory pipes accept a write silently after the other end closes; small test-precision gaps
178:Task W40: minor (deferred): new Linux msync omits MS_INVALIDATE that the runtime's Unix flush passed
182:Task C2: minor (deferred): 8 items in task-C2-review.md; rereview-r1 Minors 2-6 (linked CTS per read, two host-fault tests dispose unbounded, in-memory pipes accept a write after peer close, ClosesPipe assertion satisfied by host teardown, TimerSignalingClock occurrence coupling); Ended handler can throw into disposal
195:- OPEN ITEM to assign: unknown-kind and unknown-cause rejection tests assert only the exception type (given to C4 if the files are in its list; otherwise assign to C6 or the final review).
209:Task W40: minor (deferred): Flush_NullCallback_Flushes stays green when the flush call is deleted (name overclaims 'Flushes'); Flush_CallbackThrows_PropagatesAndLeavesTheBlockUsable pins only the propagation half; msync omits MS_INVALIDATE (earlier minor)
247:Task C5: minor (deferred): HostPipeWriter uncovered paths (failed heartbeat write cancels the owner; late sender write after Close) - V1 coverage item
249:MERGE FIX by controller: 23709f6 (C4 ports adopt C5's UsnJournalCatchUpSource third parameter and BlockWriteReporting; 7 lines, unreviewed, final review covers it)
256:Task B5: minor (deferred): WaitForCatchUpAsync(X) while Recovering throws 'not being watched' (spec 2.6.4 says fault with X's fault) - passed to B6; RetireCurrentSnapshotLocked ?? throw ObjectDisposedException unreachable and uncovered (V1 coverage); AGENTS.md still names _cacheOnlyUnresumableCheckpointOrdinals and _swapGate (D1)
267:MERGE FIX by controller: 9725d0b renames C4's static ScriptedScan (TestSupport) to ScriptedScanSteps; collided with B5's WatchHarness ScriptedScan record (CS0101). Wave-4 suite rerun.
276:Task B9: minor (deferred): enumeration-limit test cleanup never disposes a successful late open (FileIndexConcurrentOpenTests.cs:203-205); README.md:516 joined sentences
295:Task B6: minor (deferred): a faulted rescan or recovery awaits the same completed Drained twice (RescanRestart.cs:107-138, WatchDrive.cs:145-150), harmless; CatchUpLostCount_SurvivesOperations comment still says recovery arrives with B6
359:KNOWN PRE-EXISTING (recorded for owner): a cold scan cancelled during OpenAsync leaves its partial canonical cache file (DeleteOnClose false); the next open rejects it as Incomplete, deletes it and cold-scans. Same at main 3597586.
368:Task CB: minor (deferred): StalledPipe.cs:78-79 comment says the Stalled write starts on the fourth interval (six visits per the constants)
