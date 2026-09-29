# Task C3b report: port host tests (scan, control, protocol, entry point)

Worktree: C:\Users\mtsch\MFTLib-worktrees\265-C3b, branch task/265-C3b, base cb13c86e. Commit ecd3f52 "Port host scan, control, protocol and entry-point tests".
Status: DONE_WITH_CONCERNS (see Concerns).

## Implemented

Test files only, plus the two renamed lines in scripts/coverage-linux.sh. No production code changed (git status of MFTLib/ clean).

- BrokerProtocolTests.cs, .Frames.cs, .Scan.cs, .GrowUsnJournalFrames.cs: every kind of the C1 wire table round-trips (OpenChannel, ChannelOpened, QueryVolume, VolumeInfo, GrowUsnJournal, UsnJournalSettings, Error incl. RequestId 0 on a drive pipe, Heartbeat, Stalled, ArmAndScan, Cursor, ScanProgress, CatchUpLost, ScanReady, JournalBatch, StartWatch, CaughtUp). CatchUpLost: all fields present, BytesBehind and SizeThatWouldHaveRetained null (JournalRecreated), BytesBehind = -1 (present), unknown cause rejected, plus a hand-computed golden. Unknown frame kind, unknown scan profile and bad ScanProgress phase rejected as InvalidDataException. Golden bytes for every fixed-layout kind. The base-commit client case (PrepareDriveScan_EmitsExactlyFiveFieldsPerDrive) is rewritten as ArmAndScan_RawFrameOnDrivePipe_ReachesSectionWriterWithSectionProfileAndKeepNames on HostChannelHarness (R-W3-1).
- JournalBrokerHostTests.cs (owns [TestClass] [DoNotParallelize] and the scan/control helpers), .Scan.cs, .Progress.cs, .RequestDisconnect.cs, .ControlLoop.cs (the four ServeAsync_* cases from the base Watch.cs:134-199).
- JournalBrokerHostRealSeamsTests.cs/.Operations.cs, JournalBrokerHostBlockScanTests.cs, GrowUsnJournalHostTests.cs, VolumeQueryHostTests.cs ported to raw frames on HostChannelHarness.
- DefaultElevatedEntryRunnerTests.cs: kept C1's two diagnostics tests; real-pipe test is now RunBroker_ValidControlPipe_ServesUntilControlCloses_ExitsWithCode0; added the two client-disconnect cases (watch, arm-and-scan) on real control + drive named pipes. Hazard fix: every test closes the caller's pipes and awaits the runner with a bounded wait (EnsureRunnerLeavesAsync); if the runner does not leave, `_runnerStillServing` makes Cleanup keep a no-op exit seam instead of restoring Environment.Exit under the still-serving thread. Cleanup also resets MFTLibNative/FileUtilities again.
- ElevatedEntryPointTests.cs: already finished by C1 (no --once cases, RunBroker(controlPipeName) recorded); verified, unchanged.
- scripts/coverage-linux.sh: the two lines renamed; no exclusion added.

Dropped and renamed methods are listed one per line with reasons in the commit message (built by comparing [TestMethod] names of the base files with the new files; 131 base names, 128 new).

## Harness
No member added to or changed in HostChannelHarness. ServeAsync_TokenAlreadyCancelled needs a token the harness constructor cannot pass, so that one case calls host.ServeAsync directly over an InMemoryPipePair.

## TDD / verification evidence
Ports are not test-first. 
- Targeted: `dotnet test ... --filter "BrokerProtocolTests|JournalBrokerHostTests|JournalBrokerHostRealSeamsTests|JournalBrokerHostBlockScanTests|GrowUsnJournalHostTests|VolumeQueryHostTests|DefaultElevatedEntryRunnerTests|ElevatedEntryPointTests"` -> `Passed! Failed: 0, Passed: 132, Total: 132`. Repeated 8 times on the host/real-seams/block-scan/runner subset: 47/47 each time.
- Whole suite: `.\scripts\run-coverage.ps1 -NonInteractive` -> Total 1424, Passed 1418, Skipped 6, Failed 0; line coverage 97.6%, branch 94%. (Run before three small aislop-driven edits: lambda parameter discards, a TaskCompletionSource in place of ManualResetEventSlim, dropping a `using` on the runner test's server; the targeted set was re-run green after them.)
- aislop: `aislop scan .` 99/100, 6 warnings, none in files I touched: the four documented baseline warnings plus two outside my files: `MFTLib/Index/DriveStatus.cs:42` (`CompactionNeeded.get` never used) and `MFTLib/Broker/Host/JournalBrokerHost.cs:43` (constructor has 8 parameters, max 6; C1's constructor, deliberate per the C1 interface). Neither is in the documented baseline of four; both come from the base tree.
- The native DLL is not copied into MFTLib.Tests/bin by `dotnet build` of the test project; I copied MFTLibNative.dll manually for targeted runs (run-coverage handles it).

## Concerns
1. Scratch-mutation evidence for the NEW tests was not produced. My attempt to mutate production code (BrokerProtocol.Write.cs / BrokerProtocol.cs, uncommitted, to be reverted) was denied by the auto-mode classifier ("Modify Shared Resources"), so I did not retry. Substitute evidence: the CatchUpLost round trips are backed by an independent hand-computed golden (WireBytes_Golden_CatchUpLostFrame), and every other kind has a golden, so a field swap or dropped RequestId cannot pass. The unknown-kind, unknown-profile and unknown-cause rejections rely on the reader throwing and were not shown failing.
2. Linux: the new client-disconnect runner tests and the real-seams scan test use real named pipes and Windows-only sizing. The scan seam tests guard with Assert.Inconclusive off Windows; the two disconnect tests are not in the Linux exclusion list (the brief allows only the two renames). The old disconnect tests were in the same position, but I could not run Linux to confirm.
3. BrokerProtocolTests.Scan.cs is 525 lines (soft limit about 500); no top-of-file note added.
4. Merge: C3a's WatchSupport.cs repeats [TestClass]/[DoNotParallelize] on the same partial class; I messaged impl-C3a that the attributes live in my main partial. Member names I use are listed in that message.
5. Suspected defects: none. No ported test failed against the code.
6. The frame-length defect is not tested (assigned to C2).

## Files changed
MFTLib.Tests/{BrokerProtocolTests.cs, .Frames.cs, .Scan.cs, .GrowUsnJournalFrames.cs, JournalBrokerHostTests.cs, .Scan.cs, .Progress.cs, .RequestDisconnect.cs, .ControlLoop.cs, JournalBrokerHostRealSeamsTests.cs, .Operations.cs, JournalBrokerHostBlockScanTests.cs, GrowUsnJournalHostTests.cs, VolumeQueryHostTests.cs, DefaultElevatedEntryRunnerTests.cs}; scripts/coverage-linux.sh.

## Cases received from C3a
Six cases, none dropped. Four control-loop cases (ServeAsync_TokenAlreadyCancelled_ReturnsImmediatelyWithoutReading, ServeAsync_ClientClosesAfterOneRequest_ReturnsCleanlyOnEof, ServeAsync_TruncatedFrameBody_ThrowsEndOfStreamException, ServeAsync_HeaderOnlyThenEof_ThrowsEndOfStreamException) were already in ecd3f52, in JournalBrokerHostTests.ControlLoop.cs, names kept. Two scan-progress cases from the base WatchRecovery partial are in the second commit, in JournalBrokerHostTests.Progress.cs: ServeOnce_ScanProgress_EmitsFinalFrameImmediatelyBeforeScanReady -> ScanProgress_FinalFrameImmediatelyPrecedesScanReadyAndCatchUp; ServeOnce_ScanProgress_ThrottlesNonFinalFrames -> ScanProgress_ThrottlesNonFinalFrames. Verification: JournalBrokerHostTests 30/30, three runs, 0 warnings in build. Whole-suite and aislop not re-run for this small addition.

## Fix round 1
Finding (Important): RunBroker_SessionFails_FlushesDiagnosticsBeforeLeaving bypassed EnsureRunnerLeavesAsync. Fixed in a new commit "Every runner test leaves the exit seam faked until its runner has left": body in try/finally; finally releases the sink gate (idempotent) then calls EnsureRunnerLeavesAsync(runTask, server). EnsureRunnerLeavesAsync now observes completion via ContinueWith so a runner that faulted (this test expects InvalidDataException) counts as having left; only a timeout sets _runnerStillServing.
Per-test audit of the runner class: RunBroker_NullPipeName (synchronous, seam faked first, no thread left behind: ok); RunBroker_ValidControlPipe (helper in finally: ok); RunBroker_ClientDisconnectsDuringWatch (helper in finally, mock released in finally: ok); RunBroker_ClientDisconnectsDuringArmAndScan (same: ok); RunBroker_SessionFails_Flushes (fixed).
Verification: class 5/5 three runs; run-coverage -NonInteractive: total 1426, passed 1420, skipped 6, failed 0, line coverage 97.6% (covers the second commit too); aislop as below.
aislop after fix: 99/100, 6 warnings, none in touched files (4 baseline + CompactionNeeded.get + JournalBrokerHost ctor params). An intermediate scan flagged 'Dispose created' on the flush test's server; fixed with await using before the final commit (amended 640360a).
