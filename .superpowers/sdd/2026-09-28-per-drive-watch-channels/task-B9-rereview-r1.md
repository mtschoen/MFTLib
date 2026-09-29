### Finding Verdicts

1. NOT ADDRESSED (Important). The new lock does not linearize `SettledCount` with the drive settlement/adoption point. `SettleDriveAsync` finishes `AddDriveAsync` before calling `ReportSettledDrive` (`MFTLib/Index/FileIndex.Scanning.cs:55-61`), while adoption and ordinal assignment already occurred under `_stateLock` (`MFTLib/Index/FileIndex.Scanning.cs:168-183`). Only afterward does the settling thread compete for `_settleReportLock` and increment `_settledDriveCount` (`MFTLib/Index/FileIndex.Scanning.cs:66-79`). An earlier-adopted thread can be preempted before taking that lock, allowing a later-adopted drive to receive the lower count and first callback. This still violates the task brief's settlement and ordinal contract (`task-B9-brief.md:18`); changing the README to call ordinal order "adoption order" (`README.md:513-514`) does not amend the brief.

   Invoking consumer code under `_settleReportLock` does not deadlock merely because the callback reads `Drives` or `DriveStatus`: `DescribeSettledDrive` releases `_stateLock` before `openProgress.Report` runs (`MFTLib/Index/FileIndex.cs:351-357`; `MFTLib/Index/FileIndex.Scanning.cs:75-83`). It does, however, serialize arbitrary consumer code. A first callback that blocks waiting for a later drive's callback holds `_settleReportLock`; that later drive waits at `FileIndex.Scanning.cs:69`, so neither callback nor `Task.WhenAll` at `FileIndex.Scanning.cs:35` can complete. Even an ordinary slow callback stalls every later report and settle task. The new documentation explicitly accepts that stall (`MFTLib/Index/FileIndexOptions.cs:68-70`), but the binding brief specifies synchronous per-thread reporting with the next count from `Interlocked.Increment` (`task-B9-brief.md:18`), not callback serialization. The new test only pins the altered serialization behavior (`MFTLib.Tests/Index/FileIndexConcurrentOpenTests.cs:100-137`); it does not prove count order against adoption order.

2. ADDRESSED. `SettleDrivesAsync` still waits for all settle tasks, then converts any resulting failure to `OperationCanceledException` when the open token is cancelled (`MFTLib/Index/FileIndex.Scanning.cs:33-42`). The new regression cancels from one drive's progress handler while that handler also throws, and asserts cancellation from the open (`MFTLib.Tests/Index/FileIndexConcurrentOpenTests.cs:140-155`; `MFTLib.Tests/Index/FileIndexConcurrentOpenTests.cs:281-295`).

3. ADDRESSED. The fix-round report now gives an exact command and failing assertion output separately for each lost-catch-up test (`task-B9-report.md:152-154`), including the scratch mutation used to obtain RED. The two tests added in this fix round also have an exact combined command plus the individual failing outputs (`task-B9-report.md:148-151`), satisfying W40-R1 for every new test in the round.

4. NOT ADDRESSED (Important). The four previously identified open/watch/override awaits now use `WaitAsync(HangGuard)` (`MFTLib.Tests/Index/FileIndexConcurrentOpenTests.cs:165-178`, `:191-203`, `:235`, `:373`, `:438`). However, the newly added cleanup helper bounds only the opening task and then directly awaits `FileIndex.DisposeAsync` without a timeout (`MFTLib.Tests/Index/FileIndexConcurrentOpenTests.cs:297-307`, especially `:302`). That disposal can itself await cancellation callbacks, watch teardown, and snapshot release (`MFTLib/Index/FileIndex.cs:254-271`). The helper is awaited directly at `FileIndexConcurrentOpenTests.cs:264`, so a disposal regression can still hang the test. The requirement covers helpers as well as test bodies.

### New Breakage in the Fix Diff

- No additional Critical, Important, or Minor breakage beyond the unbounded cleanup await already counted under Finding 4. The callback lock's semantic and liveness problems are counted under Finding 1.

### Out-of-Scope Observations

- None.

### Verdict

Findings remain open: 1 and 4.
