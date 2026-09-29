### Finding Verdicts

1. ADDRESSED. The 514f960 commit body moves `TryNormalizeDriveLetter_InvalidDrive_ReturnsFalseAndAnEmptyNormalizedDrive` and `RescanAsync_CancelledDuringQueryVolumes_DisposesClientAndSession` from Covered by to Dropped and names the current behavior for each. The invalid-input contract is the throwing contract pinned at `MFTLib.Tests/BrokerDriveLetterTests.cs:18-24`; the cancelled-request contract is the request-ID behavior pinned by `ControlRequestIds_CancelledWaitDropsLateReply_NextRequestSucceeds` at `MFTLib.Tests/BrokerProcessTests.cs:62` and implemented by the per-request registration and wait at `MFTLib/Broker/Client/BrokerProcess.Control.cs:90-106`.

2. NOT ADDRESSED. The three startup mappings are now ported at the missing strength: each test performs a fresh start after the cancelled start, source-start failure, or stop-during-start and verifies the successor at `MFTLib.Tests/Index/FileIndexPerDriveWatchTests.cs:83-101`, `:104-119`, and `:122-138`. The disposal mapping is still not truthful, however. The new test sends the C request, observes it at the host, sends the D request, and observes that too before disposal at `MFTLib.Tests/BrokerProcessTests.Disposal.cs:188-193`. Both requests are therefore transmitted and pending, not one active request plus one queued control operation. The current implementation registers each request independently and serializes only the frame write at `MFTLib/Broker/Client/BrokerProcess.Control.cs:90-106`, `:128-138`, and `:172-200`. The row was not reclassified with the valid reason that the old serialized queue no longer exists.

3. ADDRESSED. `RescanAsync_CancelledWhileTheWatchIsStarting_LeavesTheDriveUntouched` now passes `rescan.WaitAsync(HangGuard)` to the exception helper at `MFTLib.Tests/Index/FileIndexPerDriveWatchTests.Lifecycle.cs:210-215`.

Commit message and arithmetic: ADDRESSED. The 514f960 commit body carries all two corrected Dropped rows, all four corrected Ported rows, and the rescan-bound note. Its totals are 14 Ported + 116 Covered by + 103 Dropped + 12 Left to B7 = 245 rows, matching the audit total recorded by the prior review.

### New Breakage in the Fix Diff

Important:

1. W40-R1 RED evidence is absent for every new test in this round. The fix-round report contains only green aggregate results at `.superpowers/sdd/2026-09-28-per-drive-watch-channels/task-TB-report.md:345-356`; it gives neither an exact per-test command nor real failing output from before the fix or a scratch mutation. The four tests lacking the required evidence are `ControlExchange_DisposeDuringQuery_UnblocksTheReaderAndQueuedOperation`, `StartWatching_AfterACancelledStart_StartsFresh`, `StartWatching_AfterASourceStartFailure_StartsFresh`, and `StartWatching_AfterAStopDuringStart_StartsFresh`.

2. The new disposal test leaves its final asynchronous assertion unbounded. `MFTLib.Tests/BrokerProcessTests.Disposal.cs:202-203` passes the raw `QueryVolumeAsync` task to `Assert.ThrowsExceptionAsync`; a regression in the expected immediate rejection can hold the test until the production control timeout instead of failing at `HangGuard`. This violates the same every-await-bounded rule fixed for the rescan test. Pass `QueryVolumeAsync(...).WaitAsync(HangGuard)` to the assertion.

Critical: none.

Minor: none.

### Out-of-Scope Observations

None.

### Verdict

Findings remain open:

- Finding 2 remains open for the active-plus-queued disposal lifecycle assertion.
- All four new round-1 tests lack W40-R1 RED evidence.
- The new disposal test has an unbounded post-disposal request assertion.
