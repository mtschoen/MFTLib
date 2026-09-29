### Finding Verdicts

1. NOT ADDRESSED. The fix correctly makes restart registration revalidate `WatchRequested` and, for recovery, the ticket under `_stateLock`, and a restart does not set `WatchRequested` again (`MFTLib/Index/FileIndex.WatchDrive.cs:264-296`). A withdrawn restart creates no `WatchInstance`, and the earlier retirement cancels the old catch-up slot (`MFTLib/Index/FileIndex.DriveRuntime.cs:260-277`), so there is no leaked starting instance or stranded catch-up waiter. The lifecycle-gate-then-`_stateLock` order is unchanged (`MFTLib/Index/FileIndex.WatchDrive.cs:99-118`, `MFTLib/Index/FileIndex.WatchDrive.cs:174-206`, `MFTLib/Index/FileIndex.WatchDrive.cs:264-288`). The split also preserves the consumer-start path: it clears a queued recovery in `PrepareStart`, and only a non-restart registration sets `WatchRequested` (`MFTLib/Index/FileIndex.WatchDrive.cs:185-188`, `MFTLib/Index/FileIndex.WatchDrive.cs:275-285`).

   However, the recovery-window stop does not preserve `StopWatchingAsync`'s fault-rethrow contract. The pump stores the drive fault in `instance.OutstandingFault` (`MFTLib/Index/FileIndex.WatchPump.cs:188-191`). Before the new registration seam, `PrepareStart` retires that faulted instance (`MFTLib/Index/FileIndex.WatchDrive.cs:202-205`). Because an already drained faulted instance is removed from `Current` without being placed in `Retiring` (`MFTLib/Index/FileIndex.DriveRuntime.cs:267-277`), the stop at the seam finds neither instance. It clears the request but cannot copy or clear the outstanding fault, which `StopWatchingAsync` does only from the instance returned by `RetireCurrentLocked` (`MFTLib/Index/FileIndex.WatchDrive.cs:68-90`). The new recovery test confirms the omission by expecting the first stop to complete successfully (`MFTLib.Tests/Index/FileIndexWatchRecoveryTests.cs:348-366`), even though this drive has just faulted. The requested regression needed to prove both that registration loses to stop and that this stop still throws the outstanding `DriveWatchFaultException`. The rescan-window test does correctly cover the healthy-watch variant (`MFTLib.Tests/Index/FileIndexWatchRecoveryTests.cs:371-386`).

2. ADDRESSED. W40-R1 requires per-test scratch-mutation evidence with the exact command and failing output (`.superpowers/sdd/2026-09-28-per-drive-watch-channels/orchestrator-rulings.md:30`). The report now supplies a literal command and real failure for `QueuedRecovery_SupersededByConsumerStart_DoesNotScan` (`.superpowers/sdd/2026-09-28-per-drive-watch-channels/task-B6-report.md:299-303`) and separately for `StopBetweenFaultAndQueue_QueuesNoRecovery` (`.superpowers/sdd/2026-09-28-per-drive-watch-channels/task-B6-report.md:305-309`). The two tests newly added in this fix round also have one literal combined command whose output shows both failures (`.superpowers/sdd/2026-09-28-per-drive-watch-channels/task-B6-report.md:280-291`). No new test of this round lacks RED evidence.

### New Breakage in the Fix Diff

- Critical: None.
- Important: None separate from open finding 1.
- Minor: `HoldRestartBeforeRegistration` awaits `gate.WaitForReleaseAsync(CancellationToken.None)` with no timeout or cancellable token (`MFTLib.Tests/Index/FileIndexWatchRecoveryTests.cs:476-488`). `TestGate.WaitForReleaseAsync` merely forwards that token to `Task.WaitAsync` (`MFTLib.Tests/TestSupport/TestGate.cs:20-23`), so this await is literally unbounded, contrary to the review's test rule. `WatchHarness.TrackGate` releasing gates during teardown limits practical suite damage, but it does not make the await itself bounded (`MFTLib.Tests/TestSupport/WatchHarness.cs:148-157`, `MFTLib.Tests/TestSupport/WatchHarness.cs:180-192`).

### Out-of-Scope Observations

None.

### Verdict

Findings remain open

- Finding 1: the registration race is closed, but a stop in the recovery registration window loses the failed instance's outstanding fault instead of preserving the stop rethrow contract, and the new test encodes that incorrect success.
- New Minor: the new restart-registration gate contains an unbounded await.
