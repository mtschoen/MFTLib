### Finding Verdicts

1. ADDRESSED. README.md:336-342 and README.md:574-581 now tell the consumer to call `StartWatchingAsync` again after a successful manual rescan. This matches the code: the unresumable-block rejection occurs in `FileIndex.WatchDrive.cs:201-205` before `RegisterStartingInstance` can set `WatchRequested` at `FileIndex.WatchDrive.cs:293-298`; the refusal itself only records `RefusedStartFault` at `FileIndex.WatchTargets.cs:24-29`; and `RestartRequestedWatchAsync` restarts only when `WatchRequested` is set at `FileIndex.RescanRestart.cs:169-179`.

2. ADDRESSED. README.md:694-701 explicitly labels all three edge dispositions as current behavior. The source-start failure path removes the failed instance and records `RefusedStartFault` at `FileIndex.WatchDrive.cs:321-339`; stop clears that refused fault at `FileIndex.WatchDrive.cs:78-80` and rethrows only an actual instance's `OutstandingFault` at `FileIndex.WatchDrive.cs:81-100`. A fresh start retires a faulted current instance without harvesting its outstanding fault at `FileIndex.WatchDrive.cs:286-300`. A racing stop clears `WatchRequested` and takes the stopped instance's outstanding fault at `FileIndex.WatchDrive.cs:78-85`, while restart registration revalidates the request and returns without registering after the stop at `FileIndex.WatchDrive.cs:275-283` and `FileIndex.WatchDrive.cs:309-314`. The prose matches these paths.

3. ADDRESSED. README.md:652-655 states the non-advancing bounded-read rule and both classifications. `JournalBrokerHost.Scan.cs:250-258` throws when a read returns entries with an unchanged cursor, before the chunk can be appended at line 261 or any terminal batch can be written at lines 232-234. The catch at `JournalBrokerHost.Scan.cs:216-220` routes the failure to the live journal check, which emits `Error` when no loss is proven and `CatchUpLost` when a loss is returned at `JournalBrokerHost.Scan.cs:268-285`. Thus the entries are not delivered and the stated classifications are accurate.

4. ADDRESSED. The controlling C2-Q1 placement ruling requires the BrokerTestHarness fault-surface explanation in `docs/broker-testing.md` unless README names the harness. The fixed-string check `rg -n -F BrokerTestHarness README.md` returned no matches, so no README sentence is required. The code also confirms the ruling: `BrokerTestHarness.StartInProcess` returns only `BrokerProcess` at `MFTLibTestExtensions/BrokerTestHarness.cs:20-34`; a host fault is logged and closes the host pipe ends at `MFTLibTestExtensions/BrokerTestHarness.cs:52-75`; and the returned production surface exposes `HasEnded` and `Ended` while disposal does not rethrow the host fault at `MFTLib/Broker/Client/BrokerProcess.cs:62-89`.

### New Breakage in the Fix Diff

None.

The fix diff changes only README.md and adds or changes no tests. W40-R1 RED evidence is therefore not applicable to this round, and no new test lacks an exact RED command or real failing output.

### Out-of-Scope Observations

None.

### Verdict

All findings addressed, no new Critical/Important breakage
