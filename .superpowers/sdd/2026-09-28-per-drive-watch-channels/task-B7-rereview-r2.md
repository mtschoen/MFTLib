### Finding Verdicts

1. ADDRESSED. `BatchedCatchUpWait` now retains the disposal token and, after all per-drive slots settle, completes the public wait as cancelled when disposal has been requested (`MFTLib/Index/FileIndex.BatchedCatchUpWait.cs:19-22`, `MFTLib/Index/FileIndex.BatchedCatchUpWait.cs:30-38`, `MFTLib/Index/FileIndex.BatchedCatchUpWait.cs:110-123`). The regression starts a pending all-drive catch-up wait, disposes the index through a bounded await, and requires `OperationCanceledException` from that wait (`MFTLib.Tests/Index/FileIndexBatchedWaitTests.cs:218-226`). This round adds one test. Its RED evidence satisfies W40-R1: the report names the uncommitted scratch mutation, gives the literal focused command, and records the real failing test name, assertion message, and counts (`.superpowers/sdd/2026-09-28-per-drive-watch-channels/task-B7-report.md:173-176`). No new test lacks RED evidence.

3. ADDRESSED. Every `CancelAsync()` in the two batched test files is now bounded by `WaitAsync(HangGuard)` (`MFTLib.Tests/Index/FileIndexBatchedOperationTests.cs:80`, `MFTLib.Tests/Index/FileIndexBatchedOperationTests.cs:110`, `MFTLib.Tests/Index/FileIndexBatchedOperationTests.cs:131`, `MFTLib.Tests/Index/FileIndexBatchedWaitTests.cs:237`). The moved aggregate test replaces implicit `await using` disposal with a `finally` whose `DisposeAsync` is bounded (`MFTLib.Tests/Index/FileIndexWatchRescanTests.CacheDeclinedCatchUp.cs:14-38`). The shared helper paths used by these tests are also bounded: source-start gates (`MFTLib.Tests/TestSupport/FakeIndexWatchSource.cs:123-128`), published items (`MFTLib.Tests/TestSupport/ScriptedDriveWatch.cs:44`), fault and recovery waits plus harness disposal (`MFTLib.Tests/TestSupport/WatchHarness.cs:105-119`, `MFTLib.Tests/TestSupport/WatchHarness.cs:200-218`, `MFTLib.Tests/TestSupport/WatchHarness.cs:225-231`).

### New Breakage in the Fix Diff

None.

### Out-of-Scope Observations

None.

### Verdict

All findings addressed, no new Critical/Important breakage
