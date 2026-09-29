# Orchestrator rulings on pre-flight findings

Source: `preflight-findings.md` (same directory). Each ruling amends the named task's brief; the
dispatch for that task carries the ruling text. The owner may overrule any of these; they are
sequencing and test-placement corrections, none reopens an owner ruling or the spec.

| Finding | Task(s) | Ruling |
|---|---|---|
| W1-1 | A5 | A5 rewords all five dangling `BrokerIndexWatchSource` crefs (done by message to the lane). |
| W1-2 | A1 review | Reviewer expects caller edits in `JournalBrokerHost.Sources.cs`, `MftScanProgressTests.cs`, `MftVolumeAdminTests.cs`, `MockVolumeTests.cs`, `NativeMockTests.cs`. |
| W1-3 | A2 review | Reviewer expects caller edits in `JournalBrokerClient.LiveWatchDemux.cs` and `ElevatedEntryPointTests.cs`. |
| W2-1 | C1, C5 | C1 keeps `UsnJournalCatchUpSource(string driveLetter, UsnJournalCursor since)`. C5 adds `int maximumBufferReads` together with the bounded read loop it already owns. |
| W2-2 | C1 | If aislop reports a parameter-count finding on the 8-parameter host constructor the spec fixes, C1 reports it and does not restructure. |
| W3-1 | C3b | The `BrokerProtocolTests.Scan.cs` case that builds a `JournalBrokerClient` is rewritten on raw frames through `HostChannelHarness`. |
| W4-1 | C2, C7 | C2 creates `MFTLib/Broker/BrokerLiveness.cs` with all four constants exactly as C5's brief declares them. C5 uses it and does not redefine it. C7's `ScanChannelStalledFrame_FailsWithHostMessage` injects the `Stalled` frame through a scripted drive pipe. |
| W4-2 | C5, C8 | C5's `BlockedWriteOnX_...` and `IdleSession_...` tests assert host-side behavior only, through `HostChannelHarness` (Y's pipe gets a heartbeat every interval, X's gets none after the held write, the control pipe gets one every 5 s). The client assertions ("only X faults", "`HasEnded` stays false") move to C8. |
| W4-3 | C5, B5 | OPEN until wave 3 has merged. Leading option: a small mechanical task after wave 3 and before wave 4 that changes `BlockWriter.Complete` to the plan's two-argument shape and sweeps every caller (passing null), so that neither C5 nor B5 sweeps callers in wave 4. |
| W4-4 | none | Table note only; wave order already correct. |
| W5-1 | C6 | Ported cases that assert post-fault drive state use a `Channel` fault (pipe closed). `Error`-frame cases assert only the raised `WatchFault(Drive, X)`. The orchestrator runs C6's test classes on the merged wave 5 head after B6. |
| W5-2 | B9 | `Open_CatchUpLostThreeTimes_...` uses fake producers that set `CatchUpLoss`, as B5's tests do; no synthetic journal window. |
| W5-3 | C6 | The test is named `HostError_IsDriveWatchFault`; C8's `HostErrorOverBroker_RecoversByRescan` owns the recovery assertion. |
| N-1 | C5 | `HeartbeatSender_RunsOnDedicatedThread` asserts, through a test hook, that the sender thread has `IsThreadPoolThread == false` and `IsBackground == true`. No pool saturation. |
| N-2 | C8 | C8's cases are integration scenarios; for each, the report names the precondition it proves, and see-it-fail is shown by disabling that precondition in a scratch edit that is not committed, where that is practical. |
| N-3 | A5, tranche B audit | A5's message reads "ported or dropped by C6"; the tranche B audit names every case of the six unported files. |
| N-4 | B5 | `CatchUpLost` is inserted before `Channel`; final order `{ Subscriber, Drive, Apply, CatchUpLost, Channel, Recovery }`. `Recovering` sits between `CaughtUp` and `Faulted`. |
| C1-Q1 | C1, B5 | C1 adds `JournalCheckpointLossDetection.ScanCatchUp` after `LiveWatch`; B5 does not add it. |
| B4-Q1 | B5 | Spec 2.6.4 (line 481): a rescan whose producer returns no block throws `InvalidOperationException` carrying `DriveStatus.MftProducerFailureMessage`. The code on the integration branch returns normally. B5 implements the spec rule in its production loop, with a failing-first test, and updates the B4-ported test that pins the old behavior. |
| C2-Q1 (OWNER, 2026-09-29) | C2, C4, C6, D2, F1, G2 | `BrokerTestHarness` has no fault surface of its own. An in-process host fault reaches tests only through the production surface: `BrokerProcess.Ended` / `HasEnded`, `BrokerChannelLostException` on pending operations, `Error` frames. Disposal never throws a host fault. The harness closes the host pipe ends when the host session ends, mirroring a process exit. Host exception detail goes to the diagnostics log only. No new public member. Owner principle: tests surface errors and lifecycle just like production code. |
