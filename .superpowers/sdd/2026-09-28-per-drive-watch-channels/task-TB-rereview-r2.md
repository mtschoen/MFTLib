### Finding Verdicts

1. ADDRESSED. The f49d3d9 commit body moves `ControlExchange_DisposeDuringQuery_UnblocksTheReaderAndQueuedOperation` from Ported to Dropped because the serialized control queue no longer exists. That reason matches the current implementation: each request registers its own pending reply before writing at `MFTLib/Broker/Client/BrokerProcess.Control.cs:90-96` and `MFTLib/Broker/Client/BrokerProcess.Control.cs:126-138`; only frame writes take `_controlWriteLock` at `MFTLib/Broker/Client/BrokerProcess.Control.cs:172-200`. The retained test is accurately named `Dispose_WithTwoPendingControlRequests_FailsBothWithChannelLost`, and it observes both requests on the host side before disposal at `MFTLib.Tests/BrokerProcessTests.Disposal.cs:185-193`. The report records the reclassification and rename at `.superpowers/sdd/2026-09-28-per-drive-watch-channels/task-TB-report.md:362-363`.

2. ADDRESSED. The disposal test's final post-disposal request assertion now awaits `QueryVolumeAsync(...).WaitAsync(HangGuard)` at `MFTLib.Tests/BrokerProcessTests.Disposal.cs:201-203`.

3. ADDRESSED. Under the controller ruling, RED evidence is not required because all four tests are ports of base cases:

   - `Dispose_WithTwoPendingControlRequests_FailsBothWithChannelLost` retains the base disposal behavior that outstanding control operations finish when the client is disposed. The base case starts an active request and another operation, disposes the client, and bounds both completions at `3597586:MFTLib.Tests/JournalBrokerClientTests.ControlExchangeLifetime.cs:37-61`; the port asserts that both currently valid pending requests fail through the production channel-loss surface on disposal at `MFTLib.Tests/BrokerProcessTests.Disposal.cs:185-203`. The removed serialized-queue portion is separately Dropped.
   - `StartWatching_AfterACancelledStart_StartsFresh` ports the cancelled-start-then-restart behavior from `3597586:MFTLib.Tests/Index/FileIndexWatchStartReadinessTests.cs:67-85`, with the current cancellation and successful fresh start asserted at `MFTLib.Tests/Index/FileIndexPerDriveWatchTests.cs:84-101`.
   - `StartWatching_AfterASourceStartFailure_StartsFresh` ports the failed-source-start-then-restart behavior from `3597586:MFTLib.Tests/Index/FileIndexWatchStartReadinessTests.cs:45-63`, with the original exception identity and successful fresh start asserted at `MFTLib.Tests/Index/FileIndexPerDriveWatchTests.cs:105-119`.
   - `StartWatching_AfterAStopDuringStart_StartsFresh` ports the stop-during-start cancellation and subsequent restart from `3597586:MFTLib.Tests/Index/FileIndexWatchStartReadinessTests.cs:89-103`, with both behaviors asserted at `MFTLib.Tests/Index/FileIndexPerDriveWatchTests.cs:123-138`.

4. ADDRESSED. The f49d3d9 commit body states 13 Ported, 116 Covered by, 104 Dropped, and 12 Left to B7. These counts are also recorded at `.superpowers/sdd/2026-09-28-per-drive-watch-channels/task-TB-report.md:364`, and 13 + 116 + 104 + 12 = 245 rows.

### New Breakage in the Fix Diff

Critical: none.

Important: none.

Minor: none.

### Out-of-Scope Observations

None.

### Verdict

All findings addressed, no new Critical/Important breakage
