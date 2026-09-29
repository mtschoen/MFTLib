### Finding Verdicts

1. NOT ADDRESSED (Important). The batch-owned completion source is still required by the specification, and it is constructed with `TaskCreationOptions.RunContinuationsAsynchronously` (`MFTLib/Index/FileIndex.BatchedCatchUpWait.cs:20-21`). Caller and disposal registrations are present and the coordinator does not use `Task.WaitAsync` (`MFTLib/Index/FileIndex.BatchedCatchUpWait.cs:48-54`). However, disposal does not actually cancel the public wait: the disposal callback calls `Cancel(disposalToken)`, but `Complete()` checks only `_callerToken` and otherwise completes successfully with per-drive results (`MFTLib/Index/FileIndex.BatchedCatchUpWait.cs:84-90`, `MFTLib/Index/FileIndex.BatchedCatchUpWait.cs:108-117`). This contradicts the required disposal-token cancellation path and the class's own contract. The RED record does meet the literal W40-R1 standard for `BatchedWait_SettledByPumpFault_ContinuationNotInline`: it gives an exact command and real failing output for a scratch mutation (`.superpowers/sdd/2026-09-28-per-drive-watch-channels/task-B7-report.md:151-155`). The batch-source-only mutation passes, so that test does not independently pin the batch source, but W40-R1 requires a per-test scratch mutation with an exact command and failing output, which the combined mutation supplies.

2. ADDRESSED. Generic batched operations await every per-drive task and then unconditionally call `cancellationToken.ThrowIfCancellationRequested()` (`MFTLib/Index/FileIndex.Batched.cs:112-120`). The catch-up coordinator also cancels for a pre-cancelled caller token, including an empty batch and all-null applicability (`MFTLib/Index/FileIndex.BatchedCatchUpWait.cs:36-40`, `MFTLib/Index/FileIndex.BatchedCatchUpWait.cs:108-117`). `Batched_CancelledTokenWithNothingToDo_StillThrows` covers all four empty forms and the stop/wait all-NotApplicable cases (`MFTLib.Tests/Index/FileIndexBatchedOperationTests.cs:126-140`). The named gated-start scenario now reaches U's source, cancels the observed token, releases its gate in `finally`, verifies zero published handles, and verifies `NotStarted` (`MFTLib.Tests/Index/FileIndexBatchedOperationTests.cs:68-93`).

3. NOT ADDRESSED (Important). The source gate is now bounded (`MFTLib.Tests/TestSupport/FakeIndexWatchSource.cs:123-128`), and the two cancellation scenarios release their gates and held scan in `finally` (`MFTLib.Tests/Index/FileIndexBatchedOperationTests.cs:76-88`, `MFTLib.Tests/Index/FileIndexBatchedOperationTests.cs:103-121`). Index-operation awaits in the two batched files are also bounded. But externally progressing cancellation awaits remain unbounded while callbacks are active: `CancelAsync()` is awaited directly in the gated-start cases (`MFTLib.Tests/Index/FileIndexBatchedOperationTests.cs:80`, `MFTLib.Tests/Index/FileIndexBatchedOperationTests.cs:110`) and in the batched catch-up cancellation case (`MFTLib.Tests/Index/FileIndexBatchedWaitTests.cs:225`). The moved aggregate test also uses implicit `await using` disposal with no hang guard (`MFTLib.Tests/Index/FileIndexWatchRescanTests.CacheDeclinedCatchUp.cs:14-15`). Thus the round does not satisfy the stated every-externally-progressing-await rule.

4. ADDRESSED. The report records the complete literal `dotnet test` command and actual failing assertion plus counts for the continuation mutation (`.superpowers/sdd/2026-09-28-per-drive-watch-channels/task-B7-report.md:151-155`).

Folded-in Minor 1 is addressed. `GetCatchUpWaitLocked` contains the same retried-loss, current-instance, refused-start, then-null selection (`MFTLib/Index/FileIndex.WatchCatchUp.cs:41-54`), and both batched and single-drive entry points use it (`MFTLib/Index/FileIndex.Batched.cs:63-70`, `MFTLib/Index/FileIndex.WatchCatchUp.cs:81-88`). The single-drive pre-cancellation and exception behavior around that selection is unchanged.

Folded-in Minor 2 is addressed. The aggregate test is in the focused 34-line partial file (`MFTLib.Tests/Index/FileIndexWatchRescanTests.CacheDeclinedCatchUp.cs:1-34`), and the original class is now partial (`MFTLib.Tests/Index/FileIndexWatchRescanTests.cs:13`).

### New Breakage in the Fix Diff

None beyond the open defects identified in findings 1 and 3.

### Out-of-Scope Observations

None.

### Verdict

Findings remain open:

- Finding 1: disposal-only cancellation returns a successful result list instead of cancelling the batched catch-up wait.
- Finding 3: externally progressing cancellation and implicit disposal awaits remain unbounded.
