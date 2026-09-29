### Plan Compliance
- Issues found: the named README and index-format sections were rewritten, but required behavior from docs-common.md is missing for refused-start recovery, current fault disposition, bounded catch-up cursor validation, and the BrokerTestHarness fault surface. Details are under Important.
- Cannot verify from diff: the aislop gate. The report claims 99/100 with only the four baseline warnings and the ruled JournalBrokerHost constructor warning, but no scan artifact is in the review package and this read-only review could not run a scan that may write telemetry.
- Review-method note: I read README.md:285-704 and docs/index-format.md:290-330 separately because the supplied diff split or truncated complete code samples. The API checks outside the diff were limited to the concrete named accuracy risks.

### Strengths
- The public examples use real signatures and types: the broker sample at README.md:425-480 matches BrokerProcess.LaunchAsync, QueryVolumeAsync, GrowUsnJournalAsync, and ScanDriveAsync at MFTLib/Broker/Client/BrokerProcess.Launch.cs:28-49, MFTLib/Broker/Client/BrokerProcess.Control.cs:53-73, and MFTLib/Broker/Client/BrokerProcess.Scan.cs:21-22; the adapter ownership statement matches MFTLib/Broker/Client/BrokerMftBlockProducer.cs:5-39.
- The single-drive and batched examples at README.md:497-521 match the overloads at MFTLib/Index/FileIndex.WatchDrive.cs:31-58 and MFTLib/Index/FileIndex.Batched.cs:13-116. The result ordering and NotApplicable descriptions at README.md:505-525 and docs/index-format.md:320-326 match MFTLib/Index/FileIndex.Batched.cs:160-219 and MFTLib/Index/DriveOperationResult.cs:7-30.
- OpenProgress is documented accurately at README.md:553-570: settlement is concurrent, callbacks run with no index lock, arrival may be out of SettledCount order, and consumers keep the largest count. The implementation is at MFTLib/Index/FileIndex.Scanning.cs:13-71.
- Recovery states and callback reentrancy at README.md:632-695 and docs/index-format.md:305-330 match the public enums and guard behavior at MFTLib/Index/WatchFault.cs:4-57, MFTLib/Index/WatchCatchUpState.cs:8-38, and MFTLib/Index/FileIndex.WatchCatchUp.cs:72-93.
- The removed session, merged-stream, arm/disarm, and shared-pipe model is absent from README.md:117-704 and docs/index-format.md:290-330. My fixed-string checks returned zero hits for every deleted identifier listed in docs-common.md, and the Unicode scan returned zero em-dash and en-dash characters. The remaining "no longer" phrases at README.md:321, README.md:395, and README.md:640 describe runtime journal state, not implementation history.

### Issues
#### Critical (Must Fix)
None.

#### Important (Should Fix)
1. **The unresumable-drive instructions omit the required second watch start.** README.md:336-341 and README.md:573-579 stop after telling the consumer to rescan. An unresumable start throws before `WatchRequested` is set at MFTLib/Index/FileIndex.WatchDrive.cs:185-205, and a rescan restarts only when that flag is set at MFTLib/Index/FileIndex.RescanRestart.cs:163-179. The consumer must call `StartWatchingAsync` again after the successful `RescanAsync`; without that instruction, following the README leaves the drive unwatched.

2. **The required current fault-disposition behavior is not documented.** README.md:687-689 states only the ordinary outstanding-fault rule. It omits that a failed source start leaves a refused-start fault which stop clears without rethrowing (MFTLib/Index/FileIndex.WatchDrive.cs:58-100 and :321-339), that a fresh start supersedes a faulted instance and discards its outstanding fault (MFTLib/Index/FileIndex.WatchDrive.cs:285-300), and that a stop racing a rescan or recovery restart wins by clearing `WatchRequested` (MFTLib/Index/FileIndex.WatchDrive.cs:70-100 and MFTLib/Index/FileIndex.RescanRestart.cs:169-179). docs-common.md explicitly requires these open owner decisions to be described as current behavior.

3. **The bounded catch-up non-advancement failure is missing.** The Errors and recovery section at README.md:621-704 never states the W4fix contract. HEAD treats a bounded read that returns entries without advancing its cursor as an immediate `InvalidOperationException`, does not append the entries, and lets the journal check classify the scan as `CatchUpLost` or `Error` at MFTLib/Broker/Host/JournalBrokerHost.Scan.cs:237-258. This loud rejection is required documentation because silently accepting the entries would deliver duplicates.

4. **The BrokerTestHarness fault surface required by C2-Q1 is absent.** README.md:697-704 correctly describes the production BrokerProcess signals but never tells test authors that `BrokerTestHarness` exposes no separate host-fault channel. The harness returns only a production `BrokerProcess` at MFTLibTestExtensions/BrokerTestHarness.cs:5-32, logs a host failure and closes the host pipe ends at MFTLibTestExtensions/BrokerTestHarness.cs:52-75, and BrokerProcess reports termination through `HasEnded`/`Ended`, failed operations, and channels while disposal does not rethrow the host failure at MFTLib/Broker/Client/BrokerProcess.cs:62-89. Add the ruling explicitly so tests do not wait for or assert on a nonexistent harness fault surface.

#### Minor (Nice to Have)
None.

### Assessment
Task quality: Needs fixes
Reasoning: The rewrite accurately describes the main public API and per-drive model, but four explicitly required edge contracts are absent. Those omissions can leave a consumer unwatched or lead tests and recovery handling to assert the wrong fault behavior.
