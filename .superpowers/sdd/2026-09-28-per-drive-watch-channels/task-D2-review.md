### Plan Compliance
- Issues found: the four named files and sections were rewritten, but `docs/broker-scan-tuning.md:70` states the wrong time at which an open-time lost-catch-up drive becomes faulted, and `docs/broker-integration.md:207` omits both open owner-decision behaviors required by `docs-common.md`.
- Cannot verify from diff: the implementer reports an aislop score of 99 with only the four baseline warnings and the ruled constructor warning, but the scan was not rerun because this review permits no generated files outside this review. Every changed C# sample was checked in full against the public signatures at HEAD; no sample-signature mismatch was found.

### Strengths

- The channel and liveness model is concise and matches HEAD: the documentation assigns one pipe per drive operation and describes idle, queued, processing, in-flight-write, and stalled behavior at `docs/broker-integration.md:19` and `docs/broker-integration.md:32`; the implementation defines the 5 second heartbeat, 30 second processing limit, and 30 second client silence limit at `MFTLib/Broker/BrokerLiveness.cs:15`, `MFTLib/Broker/BrokerLiveness.cs:22`, and `MFTLib/Broker/BrokerLiveness.cs:29`, with the processing heartbeat and stall branch at `MFTLib/Broker/Host/HostPipeWriter.cs:190`.
- The harness fault surface follows C2-Q1 exactly at `docs/broker-testing.md:24`: host termination closes the production pipes and logs host detail at `MFTLibTestExtensions/BrokerTestHarness.cs:52` and `MFTLibTestExtensions/BrokerTestHarness.cs:63`, while `BrokerProcess.DisposeAsync` does not rethrow the host failure at `MFTLib/Broker/Client/BrokerProcess.cs:81`.
- The public samples use the real per-drive APIs. The producer sample at `docs/broker-integration.md:102` matches `BrokerMftBlockProducer` and its optional arguments at `MFTLib/Broker/Client/BrokerMftBlockProducer.cs:26`; the fake watch sample at `docs/broker-testing.md:63` implements the exact members at `MFTLib/Index/IIndexWatchSource.cs:20` and `MFTLib/Index/IIndexDriveWatch.cs:13`.
- The OpenProgress and callback-reentrancy rulings are stated accurately at `docs/broker-integration.md:172` and `docs/broker-integration.md:258`, matching `MFTLib/Index/FileIndex.Scanning.cs:39` and `MFTLib/Index/FileIndex.Watch.cs:34`.
- Focused searches found zero deleted-identifier hits and zero en-dash or em-dash characters across all four changed documentation files.

### Issues
#### Critical (Must Fix)

None.

#### Important (Should Fix)

1. **Open-time catch-up loss is already faulted before a watch start.** `docs/broker-scan-tuning.md:70` says the drive settles Ready and that a later refused watch start "then reports Faulted." Ready is the drive state, but its watch catch-up state is already `WatchCatchUpState.Faulted` when open reaches the loss limit. `RecordLostCatchUp` installs `RefusedStartFault` at the limit during open at `MFTLib/Index/FileIndex.CatchUp.cs:185` and `MFTLib/Index/FileIndex.CatchUp.cs:196`, and the status projection returns Faulted whenever that field is present at `MFTLib/Index/FileIndex.WatchCatchUp.cs:33`. State the two dimensions together: the open settles `DriveState.Ready` with a queryable block and `WatchCatchUpState.Faulted`; a later start is refused but does not create that status.

2. **The two open owner-decision behaviors are not documented.** The lifecycle section at `docs/broker-integration.md:207` explains only how stop handles an existing watch instance's outstanding fault, and `docs/broker-integration.md:243` only says a consumer may start again after a fault. It never states the required current behavior that stopping after a source start failure clears the request and `RefusedStartFault` without rethrowing it, or that a fresh start supersedes a faulted instance and discards that instance's outstanding fault. HEAD implements the first behavior at `MFTLib/Index/FileIndex.WatchDrive.cs:78` through `MFTLib/Index/FileIndex.WatchDrive.cs:99`, and the second at `MFTLib/Index/FileIndex.WatchDrive.cs:286` through `MFTLib/Index/FileIndex.WatchDrive.cs:304`. Add both as descriptions of current behavior, without presenting either as settled design.

#### Minor (Nice to Have)

1. `docs/broker-integration.md:241` retains the explicitly discouraged phrase "no longer". It describes current journal loss rather than an old API, but `docs-common.md` lists that wording among the history-note forms to avoid. Rephrase it with plain target-state wording, such as saying the armed cursor had become unreadable.

### Assessment
Task quality: Needs fixes
Reasoning: The rewrite is broad, readable, and largely faithful to the per-drive implementation, but one stated state transition is inaccurate and two explicitly required current lifecycle behaviors are absent.
