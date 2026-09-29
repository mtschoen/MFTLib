### Finding Verdicts

1. ADDRESSED. B9-Q2 requires a cancelled drive not to settle, claim no `SettledCount`, and report nothing. `SettleDriveAsync` reaches its count lookup and callback only after `AddDriveAsync` returns, so cancellation propagating from that await bypasses the report (`MFTLib/Index/FileIndex.Scanning.cs:46-71`). The revised public documentation now describes that contract (`MFTLib/Index/FileIndexOptions.cs:63-73`, `MFTLib/Index/IndexDriveOpened.cs:3-12`, `README.md:504-515`, `docs/broker-integration.md:308-314`).

   Production did not change in this fix diff, so the report's statement is accurate. The required release was already present: `SettleDrivesAsync` awaits all settle tasks before rethrowing cancellation (`MFTLib/Index/FileIndex.Scanning.cs:13-35`), `OpenAsync` then calls `ReleaseUnpublishedBlocks` (`MFTLib/Index/FileIndex.cs:148-159`), and that method disposes every block in `_driveBlocks` before clearing the collection (`MFTLib/Index/FileIndex.cs:294-319`). A block adopted by a lost-catch-up attempt is stored in `_driveBlocks` even though it has not settled (`MFTLib/Index/FileIndex.Scanning.cs:148-169`; `MFTLib/Index/FileIndex.CatchUp.cs:90-134`), so it follows that unwind path. The new regression reaches exactly that state, cancels the parked retry, asserts no report and no active producer, then proves the restored canonical file is no longer held by opening it with `FileShare.None` (`MFTLib.Tests/Index/FileIndexConcurrentOpenTests.cs:482-508`).

4. NOT ADDRESSED (Important). The implicit `FileIndex.DisposeAsync` awaits are now bounded: all 13 `await using` sites wrap their index in `BoundedIndex`, whose `DisposeAsync` applies `WaitAsync(HangGuard)` (`MFTLib.Tests/Index/FileIndexConcurrentOpenTests.cs:47,73,99,128,157,188,216,277,430,451,532,549,571`; `MFTLib.Tests/Index/FileIndexConcurrentOpenTests.cs:366-371`). `DisposeIfOpenedAsync` is also bounded (`MFTLib.Tests/Index/FileIndexConcurrentOpenTests.cs:374-380`), and `OpenScenario.WaitForReportAsync` has its own timeout (`MFTLib.Tests/Index/OpenScenario.cs:90-96`). However, `Open_EnumerationWalks_NeverExceedTheWalkLimit` still directly awaits `File.WriteAllTextAsync` without `WaitAsync(HangGuard)` (`MFTLib.Tests/Index/FileIndexConcurrentOpenTests.cs:235-248`). Its `Token` is the test context cancellation token, not a local timeout (`MFTLib.Tests/Index/FileIndexConcurrentOpenTests.cs:20-24`). Under the explicit rule that every await be bounded, this remains an unbounded externally progressing await. The report itself identifies it as the remaining unbounded await (`task-B9-report.md:176`).

### New Breakage in the Fix Diff

- No new Critical, Important, or Minor breakage beyond the remaining Finding 4 defect. The unbounded file write predates this fix diff and is counted under that finding.
- RED evidence satisfies W40-R1 for the only new round-3 test. The report gives the exact focused command and identifies the uncommitted scratch mutation, then records the real failure: `IOException` because the canonical block file remained in use, with `Failed: 1` (`task-B9-report.md:174-175`). No new round-3 test lacks RED evidence.

### Out-of-Scope Observations

- None.

### Verdict

Findings remain open: 4.
