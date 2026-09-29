# Pre-flight findings: per-drive watch channels plan

Plan: `docs/superpowers/plans/2026-09-28-per-drive-watch-channels.md` (line numbers below are the plan's).
Repository state checked: worktree `impl-265` at the plan's base. Evidence is from Grep over that worktree.

Totals: 3 block a wave, 3 need an orchestrator decision, 11 notes.

Category summary:
1. Contradictions: W2-1, W4-1, W5-1, W5-2, N-4 (below).
2. Same-wave file collisions: W4-3. Waves 1, 2, 3, 5, 6, 8 are clean (wave 1 verified file by file, see W1-2 and W1-3).
3. Green-gate impossibilities: W1-1, W4-1, W4-2.
4. Reviewer-defect mandates: N-1, N-2. No test asserts on wall-clock time; no mandated verbatim duplication found.
5. Dependency table: W4-1, W4-2, W4-4, W5-1.

---

## Wave 1 (running now)

### W1-1. A5 leaves unresolved doc crefs; the A5 aislop gate is likely red. Severity: blocks a wave (A5's gate)

Plan line 365: "the hits outside the files below are doc crefs in `MFTLib/Index/IIndexWatchSource.cs` and `FileIndex.Watch.cs`, which B1 rewrites".

Evidence (Grep `BrokerIndexWatchSource|CreateWatchSource` in `MFTLib/`): after A5's deletions these crefs remain, pointing at a type that no longer exists:

- `MFTLib/Index/FileIndex.Watch.cs:41` and `:62` `<see cref="BrokerIndexWatchSource" />`
- `MFTLib/Index/IIndexWatchSource.cs:42` `<see cref="BrokerIndexWatchSource" />`
- `MFTLib/Broker/Client/LiveWatchItem.cs:25` `<see cref="BrokerIndexWatchSource" />` (not mentioned by the plan)
- `MFTLib/Broker/Client/JournalBrokerClient.LiveWatch.cs:379` `<see cref="BrokerIndexWatchSource" />` (not mentioned by the plan)

`MFTLib.csproj` does not set `GenerateDocumentationFile`, so the compiler does not fail. However, `.aislop/config.yml` runs jb inspectcode on `MFTLib` with `jbSeverityFloor: WARNING` and `failBelow: 100`, and ReSharper reports an unresolved cref in an XML comment at warning severity. A5's gate (line 375, "green (build, tests, aislop)") then fails until B1 (wave 2) and C1 (wave 2) rewrite or delete those files.

Resolution: A5 turns those five crefs into plain text (or `<c>BrokerIndexWatchSource</c>`). No wave-1 task touches any of the four files, so there is no collision; B1 rewrites the two Index files and C1 deletes the two client files in wave 2 anyway.

### W1-2. A1's file list omits callers it must change. Severity: note

Plan line 249 lists `MFTLib/Mft/MftVolume.cs` and line 277 says "every caller ... passes the new arguments (Grep each method name)".

Evidence (Grep `\.(StreamRecords|ReadRecordBatches)\(`): production caller `MFTLib/Broker/Host/JournalBrokerHost.Sources.cs:11`; test callers `MftScanProgressTests.cs` (3), `MftVolumeAdminTests.cs` (13), `MockVolumeTests.cs` (20), `NativeMockTests.cs` (1), plus the listed `MftVolumeTests.cs`. None of these is touched by A2, A3 or A5 (A2 edits `JournalBrokerHost.cs`, a different file). No collision; flag only so the A1 reviewer expects these edits.

### W1-3. A2's file list omits callers of the changed `Log` signature. Severity: note

Plan line 322 names only `JournalBrokerHost.cs` and `JournalBrokerClient.Transport.cs`; line 331 changes `Log` to `Log(string channel, string message)`.

Evidence (Grep `BrokerDiagnostics\.(Log|LogFrame)\b`): also `MFTLib/Broker/Client/JournalBrokerClient.LiveWatchDemux.cs:34`, `:99`, `:113`, `:123` and `MFTLib.Tests/ElevatedEntryPointTests.cs:91`. Neither file is touched by A1, A3 or A5. No collision. (C1 deletes `LiveWatchDemux.cs` in wave 2.)

Wave 1 collision check: clean. A3 and A5 deletion sets are disjoint and each set's only external references are the files each task names (Grep of `JournalBrokerScanSession|JournalBrokerSessionState|ScanSessionTestHarness` and of `BrokerIndexWatchSource|CreateWatchSource`), apart from W1-1.

---

## Wave 2

### W2-1. C1 adds `maximumBufferReads` to `UsnJournalCatchUpSource`, but A4, which provides the bounded read, runs in parallel. Severity: needs an orchestrator decision

Plan line 552-553 (C1): `UsnJournalCatchUpSource(string driveLetter, UsnJournalCursor since, int maximumBufferReads)`. Line 310 (A4): `internal ... ReadUsnJournalBounded(...)`. Line 182: A4 "Parallel with B1, C1". Line 881 (C5): "`JournalBrokerHost.Sources.cs` (`ReadJournal` uses `ReadUsnJournalBounded`)".

Evidence: the production catch-up source is `MFTLib/Broker/Host/JournalBrokerHost.cs:335` `return volume.ReadUsnJournal(since);`. On C1's branch `ReadUsnJournalBounded` does not exist, so C1's production source has to accept the new parameter and ignore it until C5. A reviewer would flag the unused parameter (and it misleads the host tests that pass a bound). The spec does not define this parameter (Grep of the spec for `maximumBufferReads` finds nothing); it is plan scope (Appendix A, line 1283).

Smallest resolution: add the parameter in C5, which already depends on A4 and already edits `JournalBrokerHost.Sources.cs`. C1 keeps the current three-argument-free shape `(driveLetter, since)`.

### W2-2. C1's host constructor will have 8 parameters against aislop `maxParams: 6`. Severity: note

Plan lines 566-569 and spec lines 790-793 fix the constructor at 8 parameters. `.aislop/config.yml` sets `quality.maxParams: 6`. The spec mandates the shape, so this is not re-litigable here. `ParseMFTImpl` already has 8 parameters (`MFTLibNative/mft/mft.internal.h:171`) with the repository at score 100, which suggests the rule does not fire on this code, but C# is not proven. C1 should check the scan result for a parameter-count finding and, if one appears, surface it rather than restructure.

Wave 2 collision check: clean. A4 (`usn_journal.cpp`, `MFTLibNative.cs`, `MftVolume.Journal.cs`, `UsnJournalSyntheticTests.cs`), B1 (`MFTLib/Index/*`, index tests, `TestSupport/FakeIndexWatchSource.cs`, `WatchHarness.cs`, `ReadinessScriptedWatchSource.cs`) and C1 (broker, `UsnJournalSyntheticTests.Cancellation.cs`, `JournalCheckpointLoss.cs`, `NtfsVolumeInformation.cs`, `coverage-linux.sh`) share no file. B1 green gate checked: nothing outside B1's own list and the A5-deleted files references the old watch contract (Grep of `WatchStreamItem|DriveWatchFailure|IIndexWatchSource|StartWatchingAsync|...` over `MFTLib/Broker`, `MFTLibTestExtensions`, `TestProgram`, `Benchmark`); every `_watchSession`/`CatchUpCoordinator` user is in B1's rewrite or delete lists.

---

## Wave 3

### W3-1. C3b ports a base-commit test that uses the deleted client. Severity: note

Plan line 803: C3b creates `BrokerProtocolTests.Scan.cs` "from their base-commit versions".
Evidence: `MFTLib.Tests/BrokerProtocolTests.Scan.cs:16` `await using var client = new JournalBrokerClient(clientSide, ...`. `BrokerProcess` arrives with C2, which runs in parallel in wave 3. C3b's brief should say that case is rewritten on raw frames through `HostChannelHarness`, not on a client.

Wave 3 collision check: clean (B2, B3, B4 create disjoint `Index/*` test files; C2 owns client files and `MFTLib/Index/MftBlockProducer.cs`; C3a and C3b create disjoint `JournalBrokerHostTests*` partials; C3b's `coverage-linux.sh`, `DefaultElevatedEntryRunnerTests.cs` and `ElevatedEntryPointTests.cs` edits have no wave-3 sibling).

---

## Wave 4

### W4-1. C7 needs `BrokerLiveness`, which C5 defines in the same wave, and C5 names no file for it. Severity: blocks a wave

Plan lines 888-894 (C5 "Interfaces produced"): `internal static class BrokerLiveness { ... StallLimit ... // read by C7 }`. Line 927 (C7): "stall limit from `BrokerLiveness.StallLimit` on the injected `TimeProvider`". Line 194: C7 depends on C2 only and is "Parallel with B5, C4, C5". C5's Create list (line 880) has no file that holds `BrokerLiveness`.

Consequence: C7 cannot compile on its own branch, or C7 defines the class itself and the wave-4 merge has two definitions of one type. Additionally C7's `ScanChannelStalledFrame_FailsWithHostMessage` (line 929) runs through the in-process harness over a real `JournalBrokerHost`, which writes `Stalled` only once C5's watchdog exists.

Smallest resolution: put `MFTLib/Broker/BrokerLiveness.cs` (all four constants) in C2's file list (wave 3, not yet started), so both wave-4 lanes read it; and state that C7's `Stalled` test injects the frame (a scripted drive pipe through `InMemoryBrokerPipes`) rather than relying on the host watchdog.

### W4-2. C5 tests that need C6 and C7. Severity: blocks a wave

Plan line 913: `BlockedWriteOnX_DoesNotDelayHeartbeatsOnY_OnlyXFaults` (S2): "`X` and `Y` are idle watches; ... only `X` faults (client stall limit, `Channel`)". Line 912: `IdleSession_NoRequestsNoWatches_StaysAlivePastStallLimit` asserts "`BrokerProcess.HasEnded` stays false". Line 923: C5 depends on C2, C3a, C3b (plus A4 in the table, line 193).

Evidence: client-side watches (`BrokerProcess.Watch.cs`, `BrokerWatchChannel`, `BrokerIndexWatchSource`) are created by C6 (line 975, wave 5), and the client stall limit is C7 (line 927, parallel in wave 4). On C5's branch there is no client watch to be "idle" and nothing on the client that faults on silence, so the "only X faults" half cannot pass; and the `HasEnded stays false` assertion passes before and after C5's change, so it cannot be seen failing (test rule, line 125).

Smallest resolution: C5 keeps both tests host-side through `HostChannelHarness` (Y's pipe receives a heartbeat every interval; X's pipe receives none after the held write; the control pipe receives a heartbeat every 5 s), and the "only X faults" and "`HasEnded` stays false" client assertions move to C8 (which depends on C5, C6 and B6, and transitively C7).

### W4-3. C5's `BlockWriter.Complete` signature change collides with B5's files. Severity: needs an orchestrator decision

Plan line 896 (C5): `public void Complete(DateTime scanTimestampUtc, Action<long>? rangeFlushed); // BlockWriter; every caller updated`. Line 818-819 (B5, same wave): modifies `FileIndex.Scanning.cs`, `Index/FileIndexRescanCleanupTests.cs`, `Index/FileIndexWatchRescanTests.cs`.

Evidence (Grep `\.Complete\(`): production callers `MFTLib/Index/FileIndex.Scanning.cs:422` `writer.Complete(DateTime.UtcNow);` and `MFTLib/Broker/SharedMemory/RealBlockSectionWriter.cs:18`; test callers in 22 test files plus `TestSupport/RecordingBlockSectionWriter.cs`, including `Index/FileIndexRescanCleanupTests.cs:350` and `Index/FileIndexWatchRescanTests.cs` (B3's port of the base file). C5 would edit three files B5 also edits, one of them (`FileIndex.Scanning.cs`) heavily restructured by B5, and roughly 20 index test files outside the broker trunk.

Smallest resolution (pick one): (a) C5 leaves `BlockWriter.Complete(DateTime)` alone and the ranged flush is reached only from `RealBlockSectionWriter` through `BlockFile.Flush(Action<long>?)` plus a stamp-without-flush step on the writer; or (b) move the `Complete` signature change and its caller sweep into B5's lane; or (c) serialize C5 after B5.

### W4-4. B5's dependency row omits C2. Severity: note

Plan line 191: B5 depends on "B2, B3, B4". Line 826: "The signal is `MftBlockProduceResult.CatchUpLoss` (C2)". C2 merges in wave 3, so the wave order is still correct; only the table understates the dependency. Same for B9 (line 197), which reads `CatchUpLoss` through B5's loop.

---

## Wave 5

### W5-1. C6 ports Drive-fault assertions that B6, in the same wave, makes false. Severity: needs an orchestrator decision

Plan line 196: C6 "Parallel with B6". Line 975: C6 ports `Index/WatchFailureObservationTests.cs`, `BrokerLiveWatchErrorTests.cs` and others from the base commit. Line 943 (B6): a `Drive` fault "publishes `Recovering` under `_stateLock` ... then queues a recovery". Line 1000 acknowledges the conflict only for `HostError_IsDriveWatchFault_TriggersRecovery`.

Evidence: `MFTLib.Tests/Index/WatchFailureObservationTests.cs:77-84` writes an `Error` frame (a `Drive` fault under the new contract) and then asserts `WatchCatchUpState.Faulted` and `WatchFailureMessage == marker`. On C6's branch (no B6) those assertions hold; on the merged wave-5 head the drive reads `Recovering` and a recovery rescan runs, so the ported cases fail at the merge point.

Smallest resolution: brief C6 that any ported case asserting post-fault drive state uses a `Channel` fault (pipe closed), and `Error`-frame cases assert only the raised `WatchFault(Drive, X)`; the orchestrator runs C6's suite on the merged head after B6. Alternative: make C6 depend on B6 (moves C6, B7, C8, B8 back one wave).

### W5-2. B9 test mentions synthetic journal windows that B5 says the index never reads. Severity: note

Plan line 968: `Open_CatchUpLostThreeTimes_... (L1: ... synthetic journal windows, no clock)`. Line 848 (B5): "the index reads no journal for it, so no journal window is needed here". B9's case should use fake producers setting `CatchUpLoss`, as B5's do.

### W5-3. C6 test name promises recovery that C6 cannot assert. Severity: note

Plan line 1000: `HostError_IsDriveWatchFault_TriggersRecovery` ... "in C6's worktree assert `WatchFault(Drive, 'T')` only". The name asserts something the body does not; name it `HostError_IsDriveWatchFault` in C6 and let C8 (`HostErrorOverBroker_RecoversByRescan`, line 1040) own the recovery.

---

## Cross-wave notes

### N-1. `HeartbeatSender_RunsOnDedicatedThread` depends on thread-pool saturation. Severity: note (reviewer would flag as flaky)

Plan line 915: "the sender's `ManagedThreadId` differs from every thread-pool thread id recorded while the pool is saturated by blocked work items". The pool injects threads under starvation, so the recorded set is timing-dependent and saturating it slows the whole suite. Resolution: the sender records `Thread.CurrentThread.IsThreadPoolThread` (expected false) and `IsBackground` (expected true) through a test hook.

### N-2. C8 expects its tests to pass on arrival. Severity: note

Plan line 1040: "Most of these pass on arrival if C5, C6, C7 and B6 are right". This conflicts with the inherited rule at line 125 ("see each fail for the stated reason"). Resolution: C8's brief states these are integration scenarios exempt from see-it-fail, or names the failing precondition each proves.

### N-3. A5's commit message claim about ports is broader than C6's file list. Severity: note

Plan line 373: A5's commit "lists every deleted test file and that C6 ports it". C6 (line 975) recreates `BrokerIndexWatchSourceTests`, `...CaughtUpTests`, `...FaultTests`, `BrokerFileIndexRescanTests`, `WatchFailureObservationTests`, but not `BrokerIndexWatchSourceArmingTests.cs` (+ `.LastDrive.cs`), `BrokerPerDriveArmTests.cs` (+ `.Recovery.cs`), `BrokerWatchSourceAbandonedStartTeardownTests.cs` or `BrokerWatchStartSendCancellationTests.cs` (line 996 folds "arming" into the watch-source ports). A5 should word the message as "ported or dropped by C6", and the tranche-B audit (line 172) should find each case named.

### N-4. `WatchFaultKind` member order. Severity: note

Plan line 418 (B1): `enum WatchFaultKind { Subscriber, Drive, Apply, Channel } // CatchUpLost and Recovery are added by B5`. Spec line 874: `{ Subscriber, Drive, Apply, CatchUpLost, Channel, Recovery }`. B5 must insert `CatchUpLost` before `Channel`, not append it. Similarly `WatchCatchUpState.Recovering` goes between `CaughtUp` and `Faulted` (line 818 already says so; spec line 877 agrees).
