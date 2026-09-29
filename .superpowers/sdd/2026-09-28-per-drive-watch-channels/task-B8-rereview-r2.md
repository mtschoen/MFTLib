### Finding Verdicts

3. NOT ADDRESSED (Important). The new preliminary check returns the handler guard when `_disposed` is already true (`MFTLib/Index/FileIndex.Batched.cs:114`, `MFTLib/Index/FileIndex.Batched.cs:115`, `MFTLib/Index/FileIndex.Batched.cs:117`), and the new regression covers that already-disposed ordering (`MFTLib.Tests/Index/FileIndexCallbackReentrancyTests.cs:448`, `MFTLib.Tests/Index/FileIndexCallbackReentrancyTests.cs:463`, `MFTLib.Tests/Index/FileIndexCallbackReentrancyTests.cs:465`, `MFTLib.Tests/Index/FileIndexCallbackReentrancyTests.cs:467`). It does not make the check and drive-list resolution atomic. If `_disposed` is false at the new check, disposal can set it under `_stateLock` (`MFTLib/Index/FileIndex.cs:247`, `MFTLib/Index/FileIndex.cs:254`) before the no-list overload calls `AllDriveLetters` (`MFTLib/Index/FileIndex.Batched.cs:120`); `AllDriveLetters` then throws `ObjectDisposedException` (`MFTLib/Index/FileIndex.Batched.cs:144`, `MFTLib/Index/FileIndex.Batched.cs:146`) before the list overload can inspect the unsettled waits and apply the handler guard. Thus the no-list call can still surface `ObjectDisposedException` first during concurrent disposal, contrary to the ruling's "never" requirement.

The only new test in this round has RED evidence. The report gives its literal full test name in an exact `dotnet test` command and real pre-fix failing output showing the expected handler exception and actual `ObjectDisposedException` (`.superpowers/sdd/2026-09-28-per-drive-watch-channels/task-B8-report.md:147`), satisfying W40-R1 and W40-R1a. Its awaits are bounded (`MFTLib.Tests/Index/FileIndexCallbackReentrancyTests.cs:460`, `MFTLib.Tests/Index/FileIndexCallbackReentrancyTests.cs:462`, `MFTLib.Tests/Index/FileIndexCallbackReentrancyTests.cs:467`, `MFTLib.Tests/Index/FileIndexCallbackReentrancyTests.cs:468`), and its synchronous handler waits are bounded through `TestGate.WaitForRelease` and `BlockOn` (`MFTLib.Tests/Index/FileIndexCallbackReentrancyTests.cs:457`, `MFTLib.Tests/Index/FileIndexCallbackReentrancyTests.cs:458`, `MFTLib.Tests/Index/FileIndexCallbackReentrancyTests.cs:605`, `MFTLib.Tests/Index/FileIndexCallbackReentrancyTests.cs:609`, `MFTLib.Tests/TestSupport/TestGate.cs:30`, `MFTLib.Tests/TestSupport/TestGate.cs:32`).

### New Breakage in the Fix Diff

None beyond the incomplete fix described above.

### Out-of-Scope Observations

None.

### Verdict

Findings remain open:

- Important: the no-list `WaitForCatchUpAsync(CancellationToken)` still has a check-to-use window in which concurrent disposal can make drive-list resolution throw `ObjectDisposedException` before the handler guard is applied.
