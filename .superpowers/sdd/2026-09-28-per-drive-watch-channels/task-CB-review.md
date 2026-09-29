### Plan Compliance

- Issues found: the submitted aislop result exceeds the binding gate; three remaining uncovered groups are incorrectly placed in class (c); and two new tests violate the required deterministic wait rules. Details are under Important.
- The tranche target is reported as met at 2760/2779 lines, or 99.32 percent, and the diff is test-only as required (`task-CB-report.md:3-10`; `review-ca649bb..f4cdf46.diff:3-15`).
- Remaining-line classification audit: the four class (b) groups really are non-Windows branches guarded by `OperatingSystem.IsWindows`, so a non-elevated fake on Windows cannot reach them; they are platform-only, not elevation or real-volume paths (`MFTLib/Broker/BrokerDiagnostics.cs:52-65`; `MFTLib/Broker/Host/BrokerDiagnosticsLogFilter.cs:112-117,196-208`; `MFTLib/Broker/Host/JournalBrokerHost.Sources.cs:40-48`; `MFTLib/Mft/NtfsVolumeInformation.cs:52-58`). Of the class (c) claims, the synthetic closing-brace sequence point, private callback-state guards, and impossible `MftRecord` field combinations hold (`MFTLib/Broker/Client/BrokerProcess.Scan.cs:66-71`; `MFTLib/Journal/MftVolume.Journal.cs:152-173,190-205,259-274`; `MFTLib/Mft/MftRecord.cs:114-121,159-166,189-206,224-241`). The Session, Control, and BrokerDiagnostics claims do not hold, as detailed below.
- Cannot verify from diff: the reported 1905-test run and exact 97.98 to 99.32 percent coverage change require the controller to inspect the retained coverage output; the claimed Linux coverage of the platform-only branches requires the Linux job artifact (`task-CB-report.md:7-10,38-47,64-68`). Per dispatch, this review did not rerun tests.

### Strengths

- All 20 added tests were sampled and each checks observable behavior rather than merely executing a line. Evidence spans all seven files: diagnostics routing (`MFTLib.Tests/BrokerDiagnosticsWriterReplacementTests.cs:24-30`), three client lifecycle outcomes (`MFTLib.Tests/BrokerProcessTests.ControlPipe.cs:19-29,43-46,56-64`), four malformed payload outcomes (`MFTLib.Tests/BrokerProtocolTests.MalformedPayloads.cs:48-83`), cancelled connection (`MFTLib.Tests/DefaultElevatedEntryRunnerConnectTests.cs:12-25`), seven host channel outcomes including disposal signals (`MFTLib.Tests/JournalBrokerHostChannelTests.ChannelLifetime.cs:22-24,36-39,51-54,69-72,89-95,115-117,129-136`), two stalled-pipe outcomes (`MFTLib.Tests/JournalBrokerHostLivenessTests.StalledPipe.cs:37-42,87-92`), and two allocator outcomes (`MFTLib.Tests/ParseThreadAllocatorTests.cs:16-22,32-47`).
- C2-Q1 is respected: client fault tests observe `Ended`, `HasEnded`, or `BrokerChannelLostException`, while host tests observe wire frames and pipe closure through `HostChannelHarness`; no harness-only fault surface or disposal exception is introduced (`MFTLib.Tests/BrokerProcessTests.ControlPipe.cs:35-46,52-64`; `MFTLib.Tests/JournalBrokerHostChannelTests.ChannelLifetime.cs:120-136`).
- Process-wide diagnostics users are serialized, and the liveness partial class already carries the same protection (`MFTLib.Tests/BrokerDiagnosticsWriterReplacementTests.cs:5-8`; `MFTLib.Tests/JournalBrokerHostLivenessTests.cs:9-15`). Time-sensitive production limits use `FakeTimeProvider`, and the connect test calls the internal connector with a pre-cancelled token rather than a launcher or real elevated process (`MFTLib.Tests/JournalBrokerHostChannelTests.ChannelLifetime.cs:98-111`; `MFTLib.Tests/DefaultElevatedEntryRunnerConnectTests.cs:10-18`).
- The allocator cancellation edge is deterministic rather than timing-based: `AdmitAsync` installs its cancellation callback before returning the incomplete wait, the test then registers the later callback, cancellation callbacks run in LIFO order, and the result is bounded by `WaitAsync` (`MFTLib/Broker/Host/ParseThreadAllocator.cs:65-78`; `MFTLib.Tests/ParseThreadAllocatorTests.cs:31-47`).

### Issues

#### Critical (Must Fix)

- None.

#### Important (Should Fix)

- The submitted aislop run fails the task's binding gate. The report records six warnings and explicitly identifies a sixth doc-comment finding, while this task permits exactly the four named baseline warnings plus one ruled constructor parameter-count warning. The implementation cannot pass review until the extra finding is fixed or separately ruled acceptable (`.superpowers/sdd/2026-09-28-per-drive-watch-channels/task-CB-report.md:10,68`).
- Reachable non-elevated lines are misclassified as class (c). The report changes the brief's definition from "unreachable" to "unreachable or race-only" and then places the intentional post-lock cancellation branch and drain re-snapshot loop in that expanded category (`task-CB-report.md:14,25,27`). Both are reachable races represented by production checks, not dead branches (`MFTLib/Broker/Client/BrokerProcess.Control.cs:177-190`; `MFTLib/Broker/Host/JournalBrokerHost.Session.cs:126-146`). In particular, a gated control reply can pass the token check, let the session end, then complete and track its channel task, deterministically requiring the drain's second snapshot (`MFTLib/Broker/Host/JournalBrokerHost.Session.cs:109-114`; `MFTLib/Broker/Host/JournalBrokerHost.Channel.cs:28-40`). The diagnostics null arm is also directly reachable after `ResetToDefaults`; the new test performs exactly that sequence, so an unexplained coverage miss is not an unreachable classification (`MFTLib/Broker/BrokerDiagnostics.cs:118-124,143-147`; `MFTLib.Tests/BrokerDiagnosticsWriterReplacementTests.cs:19-24`; `task-CB-report.md:37`). These groups need deterministic coverage where feasible, or an explicit reachable-gap/controller disposition, and corrected counts.
- Two synchronization paths violate the binding test rules. `OpenChannel_FirstRequestReadEndsNormallyAfterTimeout_StillRepliesError` calls a helper that polls with a real 20 ms `Task.Delay`, despite the rule forbidding real-time polling sleeps (`MFTLib.Tests/JournalBrokerHostChannelTests.ChannelLifetime.cs:111`; `MFTLib.Tests/JournalBrokerHostChannelTests.cs:284-297`). `StalledPipe_FrameQueuedBehindTheStalledWrite_IsNotWritten` drives fake time in an unbounded loop; if the stalled write is never attempted, heartbeat visits keep each await completing and the test never fails (`MFTLib.Tests/JournalBrokerHostLivenessTests.StalledPipe.cs:76-81`). Replace both with signal-driven, bounded coordination.

#### Minor (Nice to Have)

- None.

### Assessment

Task quality: Needs fixes
Reasoning: The added tests are behavior-focused and substantially improve coverage, but the task fails its explicit aislop gate and leaves reachable lines under an invalid class (c) expansion. The real-time polling and unbounded fake-time loop also make two tests noncompliant with the plan's deterministic test rules.
