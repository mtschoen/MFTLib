### Finding Verdicts

1. Important, open-time lost catch-up state: ADDRESSED.

   `docs/broker-scan-tuning.md:70-74` now states all three required points: the drive settles `DriveState.Ready` with the last queryable block while `WatchCatchUpState` is already `Faulted`, a later start is refused until a rescan succeeds, and the consumer then starts watching again.

   This matches the implementation. The open loop adopts every produced block and returns without throwing when the loss limit is reached (`MFTLib/Index/FileIndex.CatchUp.cs:141-147`). Reaching the limit installs `RefusedStartFault` (`MFTLib/Index/FileIndex.CatchUp.cs:185-203`), which projects as `WatchCatchUpState.Faulted` even with no current watch (`MFTLib/Index/FileIndex.WatchCatchUp.cs:21-33`). A published non-stale block projects as `DriveState.Ready` (`MFTLib/Index/FileIndex.cs:386-405`). A later start rejects the unresumable block before registering a watch request (`MFTLib/Index/FileIndex.WatchDrive.cs:201-205`, `MFTLib/Index/FileIndex.WatchDrive.cs:286-300`). A successful replacement clears the refusal (`MFTLib/Index/FileIndex.RescanRestart.cs:37-40`), but restart is conditional on an existing request (`MFTLib/Index/FileIndex.RescanRestart.cs:169-179`), so the consumer must call `StartWatchingAsync` again.

2. Important, lifecycle fault disposition and stop race: ADDRESSED.

   `docs/broker-integration.md:212-215` states that a source-start failure leaves the request and refused-start fault, a later stop clears both without rethrowing that start failure, and a fresh start supersedes a faulted instance and discards its outstanding fault. A non-caller-cancellation start failure records `RefusedStartFault` while leaving the request set (`MFTLib/Index/FileIndex.WatchDrive.cs:321-343`). Stop clears the request and refusal, and it rethrows only an outstanding fault obtained from a retired current instance (`MFTLib/Index/FileIndex.WatchDrive.cs:70-101`); the failed start has no current instance, so its source-start failure is not rethrown by stop. A new start waits for a faulted instance to drain (`MFTLib/Index/FileIndex.WatchDrive.cs:201-216`) and then retires and replaces it without transferring or rethrowing its `OutstandingFault` (`MFTLib/Index/FileIndex.WatchDrive.cs:286-305`).

   `docs/broker-integration.md:257-259` also states the requested stop-race behavior. A rescan leaves a faulted current instance in place through production (`MFTLib/Index/FileIndex.RescanRestart.cs:101-125`), allowing a racing stop to take and clear that instance's outstanding fault and rethrow it once (`MFTLib/Index/FileIndex.WatchDrive.cs:78-100`). Stop clears `WatchRequested`, and both rescan restart (`MFTLib/Index/FileIndex.RescanRestart.cs:169-179`) and recovery revalidation (`MFTLib/Index/FileIndex.Recovery.cs:157-165`) honor that state, so the racing stop prevents the restart.

3. Minor, target-state wording: ADDRESSED.

   `docs/broker-integration.md:246` now says the armed cursor "had become unreadable" and does not use "no longer." A focused search found no `no longer` occurrence in any of the four task documentation files: `docs/broker-integration.md`, `docs/broker-testing.md`, `docs/broker-scan-tuning.md`, or `docs/handoff-release-0.3.0.md`.

4. Owner ruling C2-Q1, BrokerTestHarness fault surface: CONFIRMED.

   `docs/broker-testing.md:24-34` says the harness has no separate fault event or stored host exception; faults reach tests through `BrokerProcess.Ended` or `HasEnded`, `BrokerChannelLostException` on pending operations, or operation handling of an `Error` frame; host detail is diagnostics-only; and process disposal does not rethrow the host fault. The harness logs a failed host session and closes all host pipe ends, without publishing a harness fault member (`MFTLibTestExtensions/BrokerTestHarness.cs:52-75`). `BrokerProcess` exposes `Ended` and treats the broker's end reason as already reported rather than rethrowing it from disposal (`MFTLib/Broker/Client/BrokerProcess.cs:74-109`). A closed watch pipe throws `BrokerChannelLostException`, while an `Error` frame is translated to the production drive fault (`MFTLib/Broker/Client/BrokerWatchChannel.cs:27-48`). This matches C2-Q1.

### New Breakage in the Fix Diff

Critical: 0. Important: 0. Minor: 0.

W40-R1 RED evidence: the fix diff adds or changes no tests. It changes only `docs/broker-integration.md` and `docs/broker-scan-tuning.md`, so no test in this round requires RED evidence and no test lacks it.

### Out-of-Scope Observations

None.

### Verdict

All findings addressed, no new Critical/Important breakage
