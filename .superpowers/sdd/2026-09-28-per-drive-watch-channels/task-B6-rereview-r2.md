### Finding Verdicts

1. ADDRESSED. `PrepareStart` now leaves a faulted instance in `runtime.Current` and returns its `Drained` task (`MFTLib/Index/FileIndex.WatchDrive.cs:203-206`). `RegisterStartingInstance` repeats restart and recovery-ticket validation, then retires the faulted instance and installs its replacement in the same `_stateLock` section (`MFTLib/Index/FileIndex.WatchDrive.cs:268-295`). A stop in between therefore clears `WatchRequested` and the recovery ticket, retires the still-current faulted instance, takes its `OutstandingFault`, awaits its drain, and rethrows the fault once (`MFTLib/Index/FileIndex.WatchDrive.cs:60-90`). The recovery and faulted-rescan regressions assert both the rethrow and the absence of a replacement start after stop (`MFTLib.Tests/Index/FileIndexWatchRecoveryTests.StopWindow.cs:19-40`, `MFTLib.Tests/Index/FileIndexWatchRecoveryTests.StopWindow.cs:48-69`).

   The dependent paths remain coherent. A consumer start still clears the recovery ticket and establishes a new request (`MFTLib/Index/FileIndex.WatchDrive.cs:186-188`, `MFTLib/Index/FileIndex.WatchDrive.cs:283-288`). Rescan and recovery restarts still honor the request and recovery-ticket revalidation (`MFTLib/Index/FileIndex.WatchDrive.cs:270-274`, `MFTLib/Index/FileIndex.WatchDrive.cs:303-304`). Disposal retires the still-current instance and includes its drain and all recovery completions in its wait (`MFTLib/Index/FileIndex.Disposal.cs:20-47`). `WaitForCatchUpAsync` continues to observe the faulted current instance's settled catch-up slot (`MFTLib/Index/FileIndex.WatchCatchUp.cs:61-83`). Recovery revalidation still requires `Current` to be the ticket's `FailedInstance` in the `Faulted` state (`MFTLib/Index/FileIndex.Recovery.cs:157-165`). The pump completes `Drained` on every exit (`MFTLib/Index/FileIndex.WatchPump.cs:19-45`), and moving retirement into registration adds no gate acquisition under `_stateLock`, preserving the documented lock order (`MFTLib/Index/FileIndex.DriveRuntime.cs:57-68`).

2. ADDRESSED. `HoldRestartBeforeRegistration` now bounds its gate wait with `WaitAsync(HangGuard)` (`MFTLib.Tests/Index/FileIndexWatchRecoveryTests.StopWindow.cs:96-106`).

RED evidence is complete for the new or corrected tests in this round. The report gives the exact two-test command against `f8b39a0` and real output showing both tests failed because the expected `DriveWatchFaultException` was not thrown (`.superpowers/sdd/2026-09-28-per-drive-watch-channels/task-B6-report.md:346-357`). No new test in this round lacks W40-R1 evidence; `StopWhileRescanRegistersItsRestart_StopWins` was only moved unchanged.

### New Breakage in the Fix Diff

- Critical: None.
- Important: None.
- Minor: A faulted rescan or recovery now awaits the same `Drained` task twice. `RetireWatchForRescanAsync` adds the still-current faulted instance's drain and awaits it before scanning (`MFTLib/Index/FileIndex.RescanRestart.cs:107-138`). After the scan, `PrepareStart` returns that same faulted instance's already-completed drain (`MFTLib/Index/FileIndex.WatchDrive.cs:203-206`), and the restart awaits it again before registration (`MFTLib/Index/FileIndex.WatchDrive.cs:145-150`). The second await is harmless because the task is already complete, but it does not satisfy this round's explicit check that no teardown is awaited twice.

### Out-of-Scope Observations

None.

### Verdict

All findings addressed, no new Critical/Important breakage
