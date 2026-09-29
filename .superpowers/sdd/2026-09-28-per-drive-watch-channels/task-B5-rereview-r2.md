### Finding Verdicts

3. ADDRESSED. The report now supplies per-test scratch-mutation evidence meeting W40-R1 for all four tests.

   - `StartDuringRescanOfUnresumableDrive_JudgesTheFreshBlock`: the mutation is stated precisely at `.superpowers/sdd/2026-09-28-per-drive-watch-channels/task-B5-report.md:295`, the literal command is at `task-B5-report.md:301`, and the real one-test failure is at `task-B5-report.md:302-305`. The failure is plausible: production waits for the drive lifecycle gate before judging watchability (`MFTLib/Index/FileIndex.WatchDrive.cs:97-106`), while the test holds that gate through the rescan and expects the concurrent start to remain incomplete (`MFTLib.Tests/Index/FileIndexCatchUpLossTests.Operations.cs:83-90`). The inserted pre-gate judgment sees the old unresumable block and completes the start early, yielding the quoted `Assert.IsFalse` failure.

   - `Rescan_WatchedDrive_LostThenNoBlock_ThrowsProducerFailureAndRefusesTheWatch`: the mutation, literal command, and real failure are recorded at `task-B5-report.md:308-318`. The failure is plausible: the fixed path re-judges the currently published block through `RefuseWatchOverUnresumableBlock` (`MFTLib/Index/FileIndex.RescanRestart.cs:49-54`, `MFTLib/Index/FileIndex.RescanRestart.cs:72-87`). Removing that call makes the method try to restart; the unresumable check in the start path then throws (`MFTLib/Index/FileIndex.WatchDrive.cs:128-135`), and the restart wrapper combines that refusal with the producer failure in an `AggregateException` (`MFTLib/Index/FileIndex.RescanRestart.cs:56-64`), matching the quoted output. The test expects the direct producer failure and no second start (`MFTLib.Tests/Index/FileIndexCatchUpLossTests.cs:321-329`).

   - `Open_CatchUpLostOnce_RetriesAndSettlesReady`: the shared mutation is stated at `task-B5-report.md:321-325`, with this test's literal command and real failure at `task-B5-report.md:327-331`. The failure is plausible: `ScanOpenedDriveAsync` assigns `adopted` before testing whether to retry (`MFTLib/Index/FileIndex.CatchUp.cs:124-130`), so adding `adopted is not null ||` makes the first lost attempt return immediately. The resulting single production matches the quoted actual value of 1 against the test's expected 2 (`MFTLib.Tests/Index/FileIndexCatchUpLossTests.Open.cs:16-23`).

   - `OpenAsync_ThreeLostCatchUps_SettlesReadyAndRefusesTheWatch`: the same scratch mutation has a separate literal command and real failing output at `task-B5-report.md:333-337`. For the same control-flow reason, it stops after the first adoption, matching the quoted actual production count of 1 against the expected 3 (`MFTLib.Tests/Index/FileIndexCatchUpLossTests.Open.cs:36-48`).

   The report also records one exact combined green command at HEAD and four passing tests (`task-B5-report.md:340-344`).

### New Breakage in the Fix Diff

- Critical: None.
- Important: None.
- Minor: None. Fix round 2 changes only the evidence report; the reference code diff introduces no newly identified breakage under this round's finding.

### Out-of-Scope Observations

- None.

### Verdict

All findings addressed, no new Critical/Important breakage
