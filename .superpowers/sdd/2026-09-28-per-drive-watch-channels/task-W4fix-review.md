### Plan Compliance
- Plan compliant. The diagnosis matches the diff: both affected fakes returned the same entry and `AdvancedCursor` for every invocation, while the C5 loop calls again from that cursor (`MFTLib.Tests/BrokerMftBlockProducerTests.cs:13`, `MFTLib.Tests/MftProducerEndToEndTests.cs:34`, `MFTLib/Broker/Host/JournalBrokerHost.Scan.cs:251`). `CatchUpSources.ToTip` now advances once and returns an empty chunk at the tip (`MFTLib.Tests/TestSupport/CatchUpSources.cs:10`).
- Plan compliant. The production guard rejects entries paired with an unchanged cursor before appending them (`MFTLib/Broker/Host/JournalBrokerHost.Scan.cs:252`). That cannot reject a result produced by the real bounded native reader: it starts `nextUsn` at the requested cursor, breaks before copying records when the kernel returns that cursor unchanged, updates `nextUsn` before copying any records otherwise, and returns that value to managed code (`MFTLibNative/usn/usn_journal.cpp:257`, `MFTLibNative/usn/usn_journal.cpp:279`, `MFTLibNative/usn/usn_journal.cpp:283`, `MFTLibNative/usn/usn_journal.cpp:308`). A buffer ending exactly at a boundary advances to its continuation cursor; a terminal buffer contributes no records. A failed or invalid journal read carries an error and is rejected by the managed wrapper rather than returned as entries with an unchanged cursor (`MFTLibNative/usn/usn_journal.cpp:267`, `MFTLib/Journal/MftVolume.Journal.cs:97`).
- Plan compliant. The new `InvalidOperationException` is inside the existing catch-up `try`; it reaches `ReportFailedCatchUpAsync`, which checks the armed cursor and writes `CatchUpLost` only for a proven loss, otherwise `Error`, exactly like another bounded-read exception (`MFTLib/Broker/Host/JournalBrokerHost.Scan.cs:212`, `MFTLib/Broker/Host/JournalBrokerHost.Scan.cs:216`, `MFTLib/Broker/Host/JournalBrokerHost.Scan.cs:275`, `MFTLib/Broker/Host/JournalBrokerHost.Scan.cs:279`, `MFTLib/Broker/Host/JournalBrokerHost.Scan.cs:283`). This matches the scan terminal-frame contract (`docs/superpowers/specs/2026-09-28-per-drive-watch-channels-design.md:121`) and its journal-based classification rule (`docs/superpowers/specs/2026-09-28-per-drive-watch-channels-design.md:210`).
- Plan compliant. The two C4-port fixes change only their catch-up source setup, so no assertion was removed or weakened (`MFTLib.Tests/BrokerMftBlockProducerTests.cs:13`, `MFTLib.Tests/MftProducerEndToEndTests.cs:33`). The regression recreates the original two-call behavior, expects `Error`, checks the diagnostic, and uses a helper that rejects any `JournalBatch` (`MFTLib.Tests/JournalBrokerHostChannelTests.Scan.cs:153`, `MFTLib.Tests/JournalBrokerHostChannelTests.Scan.cs:164`, `MFTLib.Tests/JournalBrokerHostChannelTests.Scan.cs:181`).
- Plan compliant. The regression does not depend on time, so `FakeTimeProvider` is not applicable. Its frame reads use a cancellation deadline and harness disposal bounds the host task (`MFTLib.Tests/TestSupport/HostChannelHarness.cs:130`, `MFTLib.Tests/TestSupport/HostChannelHarness.cs:147`, `MFTLib.Tests/TestSupport/HostChannelHarness.cs:163`). The report supplies RED for the original port and the new regression, GREEN for focused and whole-suite runs, and the allowed five-warning aislop result, satisfying W40-R1 and the task evidence requirements.
- Cannot verify from diff: execution provenance for the reported RED, GREEN, whole-suite, and aislop outputs. The controller should retain or compare the task logs if independent proof is required; the reported commands and outcomes are internally consistent with the reviewed change.

### Strengths
- The fix addresses the integration defect without weakening C4's lifetime or catch-up assertions, and repairs the second latent fake that happened to pass because replay was idempotent (`MFTLib.Tests/BrokerMftBlockProducerTests.cs:14`, `MFTLib.Tests/MftProducerEndToEndTests.cs:34`).
- The production check fails closed at the contract boundary and preserves the specification's journal-based failure classification instead of inferring loss from exception text (`MFTLib/Broker/Host/JournalBrokerHost.Scan.cs:257`, `MFTLib/Broker/Host/JournalBrokerHost.Scan.cs:268`).
- The delegate documentation now states the invariant that the host relies on, while the reusable test fake models both the advancing read and the empty terminal read (`MFTLib/Broker/Sources/UsnJournalCatchUpSource.cs:4`, `MFTLib.Tests/TestSupport/CatchUpSources.cs:6`).

### Issues
#### Critical (Must Fix)
- None.

#### Important (Should Fix)
- None.

#### Minor (Nice to Have)
- None.

### Assessment
Task quality: Approved
Reasoning: The implementation fixes the actual C4/C5 integration defect, adds a safe production invariant check, preserves the specified failure routing, and supplies a focused regression without weakening existing coverage. No blocking or polish defects were found in the task diff.
