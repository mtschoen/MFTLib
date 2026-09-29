### Finding Verdicts

3. ADDRESSED (Important). The no-list overload now runs both drive-list resolution and delegation to the list overload inside one `try` (`MFTLib/Index/FileIndex.Batched.cs:121`, `MFTLib/Index/FileIndex.Batched.cs:124`). If either step observes disposal while the call remains inside a handler of this index, the filtered catch converts the `ObjectDisposedException` into the guard's already-faulted task (`MFTLib/Index/FileIndex.Batched.cs:126`, `MFTLib/Index/FileIndex.Batched.cs:128`, `MFTLib/Index/FileIndex.Batched.cs:129`). Outside a handler the filter is false, so the `ObjectDisposedException` from `AllDriveLetters` or batch validation still surfaces unchanged (`MFTLib/Index/FileIndex.Batched.cs:126`, `MFTLib/Index/FileIndex.Batched.cs:154`, `MFTLib/Index/FileIndex.Batched.cs:156`).

The regression deterministically starts disposal on a thread whose execution context is not flowed, then joins that thread with `HangGuard`, so `_disposed` has been set before the drive-list resolution continues and no timing delay or polling is involved (`MFTLib.Tests/Index/FileIndexCallbackReentrancyTests.cs:477`, `MFTLib.Tests/Index/FileIndexCallbackReentrancyTests.cs:479`, `MFTLib.Tests/Index/FileIndexCallbackReentrancyTests.cs:480`, `MFTLib.Tests/Index/FileIndexCallbackReentrancyTests.cs:481`). The async observations are bounded (`MFTLib.Tests/Index/FileIndexCallbackReentrancyTests.cs:486`, `MFTLib.Tests/Index/FileIndexCallbackReentrancyTests.cs:490`, `MFTLib.Tests/Index/FileIndexCallbackReentrancyTests.cs:491`), and the handler's synchronous task wait is bounded by `HangGuard` (`MFTLib.Tests/Index/FileIndexCallbackReentrancyTests.cs:485`, `MFTLib.Tests/Index/FileIndexCallbackReentrancyTests.cs:628`, `MFTLib.Tests/Index/FileIndexCallbackReentrancyTests.cs:632`).

The seam is an internal, non-static member of each `FileIndex` instance and is invoked null-conditionally (`MFTLib/Index/FileIndex.Batched.cs:113`, `MFTLib/Index/FileIndex.Batched.cs:123`). Its only assignment in the repository is the regression test (`MFTLib.Tests/Index/FileIndexCallbackReentrancyTests.cs:477`), so it remains null and cannot fire in production. The round adds one test. Its report supplies the exact focused command with the literal full test name and real pre-fix failing output showing `ObjectDisposedException` instead of the guard exception (`.superpowers/sdd/2026-09-28-per-drive-watch-channels/task-B8-report.md:156`), satisfying W40-R1 and W40-R1a.

### New Breakage in the Fix Diff

None.

### Out-of-Scope Observations

None.

### Verdict

All findings addressed, no new Critical/Important breakage
