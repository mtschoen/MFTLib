Title: Each watched drive gets its own broker channel, with scans and watches running independently per drive.

## Problem

Every drive shared one broker pipe, one frame demultiplexer, and one watch generation. A scan occupied the shared host frame loop, delaying other drives' requests. A fault on one drive ended every drive's watch. When a stop timed out, a late `EndWatchAck` could end the next watch.

Closes [#252](https://gitea.fleet.sticktoitive.net/schoen/MFTLib/issues/252)
Refs [#265](https://gitea.fleet.sticktoitive.net/schoen/MFTLib/issues/265)

## What changes

One elevated broker process owns a control pipe plus independent drive pipes. Each drive pipe carries exactly one scan or watch; closing it stops that operation. Control requests use request identifiers and run concurrently. Closing the control pipe ends the process session and all channels. A delayed frame from a retired channel cannot reach a replacement watch.

`BrokerProcess` replaces `JournalBrokerClient`. Consumers use `LaunchAsync`, `QueryVolumeAsync`, `GrowUsnJournalAsync`, `ScanDriveAsync`, `HasEnded`, `Ended`, and `DisposeAsync`. `BrokerDriveScanResult` carries the armed cursor, optional advanced cursor, catch-up entries, optional `CatchUpLoss`, and completed `BlockScanOutcome`. `BrokerMftBlockProducer` and `BrokerIndexWatchSource` take a factory returning `BrokerProcess`.

Each configured drive has its own `DriveRuntime`, lifecycle gate, write gate, pump, and catch-up slot. Each start creates a distinct `WatchInstance`: `Starting -> Running` when its ready handle is published; a pump failure produces `Faulted`; stop, rescan, recovery, or disposal moves the instance to `Retiring -> Drained`. A replacement waits for its predecessor to drain. Batches, faults, catch-up completion, and recovery act only on the current instance and its armed block. Block and snapshot publication occurs under the shared state lock.

`IIndexWatchSource.StartAsync` returns an `IIndexDriveWatch` when its channel is connected and `StartWatch` is written. Its `ReadAsync` yields `JournalBatch` and `DriveCaughtUp` without drive fields; the handle supplies the drive. `DriveStatus.WatchCatchUp` reports `NotStarted`, `CatchingUp`, `CaughtUp`, `Recovering`, or `Faulted`. `WaitForCatchUpAsync` follows that watch instance and is cancelled when it retires.

`WatchFaulted` carries a drive-named `WatchFault`. `WatchFaultKind.Drive` and `Apply` enter `Recovering` and rescan and restart only that drive. A failed recovery or another fault before the restarted watch reaches `CaughtUp` produces `Recovery` and leaves the drive `Faulted`. `Channel` faults, including `BrokerChannelLostException`, do not recover automatically. `Subscriber` faults leave the watch running. Stop clears the watch request even during a rescan or recovery, preventing its restart; single-drive stop rethrows the outstanding watch fault once.

A dedicated heartbeat thread visits control and drive pipes every five seconds. Idle, volume-waiting, queued, and still-within-limit processing operations remain live; processing without progress beyond 30 seconds produces `Stalled`, and a client receiving no frame for 30 seconds closes that pipe. Heartbeat writes are independently bounded, so a blocked pipe does not delay another pipe. `IBrokerOperationReporter` supplies named processing and volume-waiting states. Timing limits remain subject to attended measurement.

`ParseThreadAllocator` admits at most one scan per processor and divides the process-wide parse-thread budget among running scans in admission order. `ParseThreadAllowance` is reread at each native chunk and before path resolution. A chunk already running keeps its allocation until it finishes. Callers supply no scan thread count. Native cancellation stops parsing before the operation releases its volume, section, and allocator slot; ranged block flushes and bounded catch-up reads report progress.

`FileIndex.OpenAsync` settles drives concurrently and waits for every settle before returning or throwing. `FileIndexOptions.OpenProgress` reports `IndexDriveOpened.SettledCount` from each settling thread. Reports can overlap and arrive out of count order; consumers retain the highest count. Scan progress can interleave across drives.

A journal-proven scan catch-up loss produces `CatchUpLost`, carried through `BrokerDriveScanResult.CatchUpLoss` and `MftBlockProduceResult.CatchUpLoss`. The complete block is published as queryable but unresumable, with `JournalCheckpointLossDetection.ScanCatchUp` and `DriveStatus.CheckpointLoss`. `WatchFaultKind.CatchUpLost` carries `JournalCatchUpLostException`; `DriveStatus.ConsecutiveLostCatchUps` persists across operations. Automatic rescans stop when the count reaches `FileIndex.LostCatchUpRecoveryLimit` of three, and `RecoveryStopped` becomes true. Open leaves the drive `Ready`; rescan reports failure and watching is refused. A manual rescan at the limit makes one attempt; success resets the count and permits watching. No path substitutes the journal's current cursor for a lost checkpoint.

List and all-drive overloads of `StartWatchingAsync`, `StopWatchingAsync`, `RescanAsync`, and `WaitForCatchUpAsync` run concurrently and return ordered `DriveOperationResult` values with `DriveOperationOutcome.Succeeded`, `Failed`, or `NotApplicable` and the per-drive `Failure`. All started work settles before return, including cancellation. Single-drive forms throw their failures.

`Changed` and `WatchFaulted` delivery is serialized within each drive and concurrent across drives. Lifecycle calls and unsettled catch-up waits from an active callback fail immediately with `InvalidOperationException`; queued work may run after the callback returns. Disposal cancels admitted operations and drains their work. Cancellation can follow block publication, so `OperationCanceledException` from rescan does not prove that no block was published.

`BrokerTestHarness.StartInProcess` connects the production `BrokerProcess` and `JournalBrokerHost` through in-memory control and drive pipes. `BrokerTestHarnessOptions` supplies a client clock, connection failures, and held writes. Diagnostics carry channel tags and use a bounded background writer.

## Waves

- Wave 1: A1, A2, A3, A5 - native cancellation and allocation foundations, asynchronous diagnostics, and removal of shared session layers.
- Wave 2: A4, B1, C1 - bounded journal reads, the per-drive state machine, and the channel host and allocator.
- Wave 3: B2, B3, B4, C2, C3a, C3b - index and host test migration, the process client, scan channels, and the harness.
- Wave 4: B5, C4, C5, C7 - per-drive publication gates, catch-up loss handling, scan test migration, and host and client liveness.
- Wave 5: B6, B9, C6 - automatic recovery, concurrent open, and watch channels.
- Wave 6: B7, C8 - batched entry points and cross-drive liveness scenarios.
- Wave 7: B8 - callback reentrancy protection.
- Wave 8: D1, D2, D3, then V1 - durable documentation, final verification, and the pull request.
- After merge: M1, M2, M3 - attended hardware measurements.
- After MFTLib lands: F1; G1 then G2 - consumer pin bumps and migration.

## Consumer migration

The consumer pin-bump pull requests carry this work and move `external/MFTLib` to the landed implementation commit. F1 carries file-wizard; G1 and G2 ship together for git-wizard.

### file-wizard

| File | Change |
| --- | --- |
| `FileWizard/FileWizardAPI.cs` | Launch through `BrokerProcess.LaunchAsync` in the `BrokerSessionHost` factory. |
| `FileWizard/BrokerSessionHost.cs` | Hold `BrokerProcess`, subscribe to `Ended`, and change the `ConnectAsync` factory and `Client` property types. |
| `FileWizard/FileIndexHost.cs` | Retain the producer construction with the process factory; add batched `RescanAsync`, reporting cache-only refusals as per-drive `Failed` results. |
| `FileWizard/JournalWatcher.cs` | Handle drive-named faults; keep `Drive`, `Apply`, and recoverable `CatchUpLost` active as recovering; deactivate `Channel`, `Recovery`, and stopped catch-up recovery. Carry `LiveWatch` and `ScanCatchUp` loss reports. Audit downstream callbacks for blocking lifecycle calls. |
| `FileWizardMaui/MainPage.LiveUpdates.cs` | Handle batched start failures through per-drive health; set the aggregate start error only when every applicable drive fails. Use batched stop. Audit journal callbacks and queue lifecycle work after delivery. |
| `FileWizardMaui/MainPage.Scanning.cs` | Replace rescan loops with the batched host wrapper and report each failure. Accept interleaved scan progress; render the highest `SettledCount` received. |
| `FileWizard/IUpdateHandler.cs` | Confirm `ScanDriveProgressTracker` accepts interleaved drive progress. |
| `FileWizardMaui.Logic/JournalHintLogic.cs` | Add `ScanCatchUp` hints with the consecutive-loss count, stopped recovery, and suggested journal size; recreated journals request a rescan without a size change. |
| `FileWizardMaui/SettingsPage.xaml.cs`, `file-wizard/JournalCommand.cs` | Use `BrokerProcess`; retain `GrowUsnJournalAsync`. |
| `file-wizard/BrokerSmoke.cs` | Launch through `BrokerProcess`; print per-drive start and stop results and use a batched rescan. |
| `file-wizard/CliRunner.Database.cs` | Use batched start and stop and print failed results. |
| `Directory.Build.targets` | Name `BrokerTestHarness` in the comment. |
| `FileWizardMaui.Logic/OpenProgressPresenter.cs`, `FileWizardMaui.Logic/ScannerState.cs` | Read `SettledCount` and display the number of drives settled. |
| `FileWizardMaui/MainPage.xaml.cs` | Preserve thread-safe progress handoff and prevent out-of-order reports from decreasing the displayed settled count. |
| `FileWizard/FileIndexHostOptions.cs` | Document progress from settling threads, with concurrent and potentially out-of-count-order delivery. |
| `FileWizardTests/FileIndexHostTests.cs` | Assert one report per drive and counts from one through the configured total; cover per-drive cache-only refusal in batched rescan. |
| `FileWizardTests/Maui/OpenProgressPresenterTests.cs`, `FileWizardTests/Maui/ScannerStateTests.cs`, `FileWizardTests/Maui/ScannerErrorBannerTests.cs` | Construct and assert `SettledCount` reports. |
| `AGENTS.md` | Describe `BrokerProcess` and `Ended`. |
| `FileWizardTests/BrokerDeathTests.cs` | Use `BrokerTestHarness.StartInProcess`, dispose the process, and assert `Ended` is forwarded once. |
| `FileWizardTests/BrokerSessionHostTests.cs`, `FileWizardTests/CliServicesTests.cs` | Replace fake client construction with `BrokerTestHarness.StartInProcess`. |
| `FileWizardTests/JournalCommandTests.cs` | Use `BrokerProcess` on the real path. |
| `FileWizardTests/JournalWatcherTests.cs` | Remove the shared-source all-drive failure case; cover recovering drive faults, terminal channel and recovery faults, both catch-up loss outcomes, and `ScanCatchUp` status. |
| `FileWizardTests/Maui/JournalHintLogicTests.cs` | Cover trimmed, recreated, and unavailable-size `ScanCatchUp` hints and journal growth eligibility. |

### git-wizard

| File | Change |
| --- | --- |
| `GitWizard/MftBrokerConnection.cs` | Hold and return `BrokerProcess`; subscribe to `Ended`. |
| `GitWizard/MftIndexSession.cs` | Change factory types and expose `GetBrokerProcess`; retain `Profile` and `KeepFileNames`; confirm the scan progress handler accepts interleaved drives. |
| `GitWizard/MftIndexSession.Windows.cs` | Use `BrokerProcess.LaunchAsync` and process factory types. |
| `GitWizard/Watch/IndexVolumeChangeSource.cs` | Grow through `GetBrokerProcess`; process batched start failures through `RecordStartupDriveFailure`; use batched stop and remove fault-deduplication bookkeeping. Recoverable drive faults retain usable drives; channel, recovery, and stopped catch-up recovery faults exclude them. Audit downstream callbacks for blocking lifecycle calls. |
| `GitWizard/Watch/RepositoryWatchService.cs` | Audit `DriveFailed` and stopped-event delivery; retain the verified asynchronous cancellation and disposal path without blocking the callback. |
| `GitWizard/Watch/IndexVolumeChangeSource.Journals.cs` | Accept `LiveWatch` and `ScanCatchUp` losses and raise the final catch-up-loss journal warning; protect usable-drive additions. |
| `GitWizard/Watch/JournalHintBuilder.cs` | Add scan-time loss wording to recreated and rescan hints; retain trimmed-journal size suggestions. |
| `GitWizard/Watch/IndexVolumeChangeSource.Readiness.cs` | Treat `Recovering` as recovering and `Faulted` as failed; protect usable-drive reads. |
| `GitWizard/Watch/IndexVolumeChangeSource.Startup.cs` | Protect usable-drive removals; retain the single-drive catch-up wait and verified asynchronous teardown path. |
| `GitWizard/Watch/IndexVolumeChangeSource*.cs` | Put every `_usableDrives` access under a dedicated leaf `_usableDrivesLock`, with no calls out or other lock acquisition while holding it. |
| `GitWizard/Discovery/RepositoryDiscoveryCoordinator.cs` | Report scanning for every selected drive, then make one batched rescan; accumulate successes and per-drive errors. |
| `Directory.Build.targets` | Name `BrokerTestHarness` in the comment. |
| `GitWizardTests/TestSupport/BlockBrokerFixture.cs` | Use `BrokerTestHarness.StartInProcess` with a real host, fixture section writer, and fake cursor, catch-up, and watch sources; remove hand-written frames and session plumbing. |
| `GitWizardTests/BlockBrokerFixtureTests.cs` | Call `BrokerProcess.ScanDriveAsync`. |
| `GitWizardTests/TestSupport/DuplexStream.cs` | Delete if no remaining caller uses it. |
| `GitWizardTests/TestSupport/ScriptedIndexWatchSource.cs` | Return one scripted handle per drive and expose drive-scoped `Publish`, `FailDrive`, and `LoseChannel`. |
| `GitWizardTests/Discovery/WindowsWatchStartupTests.cs` | Use per-drive failure helpers; publish drive-free `JournalBatch` and `DriveCaughtUp` through the selected handle. |
| `GitWizardTests/Watch/IndexVolumeChangeSourceReadinessTests.cs` | Use per-drive failures and drive-free `DriveCaughtUp`. |
| `GitWizardTests/TestSupport/WatchFailureAssertions.cs` | Use per-drive failure helpers and publish drive-free batches through their handles. |
| `GitWizardTests/Watch/IndexVolumeChangeSourceTests.cs` | Migrate failures, batches, and batched results; cover recovering and excluded states, stopped catch-up recovery and its journal warning, and concurrent usable-drive access. |
| `GitWizardTests/UI/MainViewModelPreparedSessionTests.cs` | Use `FailDrive` or `LoseChannel`. |
| `GitWizardTests/MftIndexPersistenceTests.cs` | Use `BrokerProcess`, drive-free batches, and batched start and stop results. |
| `GitWizardTests/Discovery/RepositoryDiscoveryCoordinatorWatchProgressTests.cs` | Use `BrokerProcess` and publish `new DriveCaughtUp()` through the drive handle. |
| `GitWizardTests/ElevatedBrokerEntryDispatchTests.cs`, `GitWizardTests/UI/DesktopStartupTests.cs` | Implement `RunBroker(string? controlPipeName)` and remove `--once` assertions. |
| `GitWizardTests/MftBrokerConnectionTests.cs` | Use the `BrokerProcess` type. |
| `GitWizardTests/MftIndexSessionTests.cs` | Assert batched start and stop results. |
| `GitWizardTests/Watch/JournalHintBuilderTests.cs`, `GitWizardTests/Watch/JournalWarningTests.cs` | Add trimmed, unavailable-size, and recreated `ScanCatchUp` hints and the scan-time journal warning. |
| `GitWizardTests/TestSupport/IndexOwnerFixture.cs`, `GitWizardTests/TestSupport/PackedIndexFixture.cs` | Compile against the process parameter type; no fixture behavior change. |

## Gates

All gate numbers and the deleted-identifier sweep refer to code head `66bdb6d`; branch head `7184c25` only retires the executed plan and specification.

`<<FILL>>` fields are reserved for the orchestrator's merged-head results and are not claims that a check has passed.

| Gate | Result and evidence |
| --- | --- |
| Windows whole suite, `scripts/run-coverage.ps1 -NonInteractive` | Total `<<FILL>>`; passed `<<FILL>>`; skipped `<<FILL>>`; failed `<<FILL>>`. |
| Line coverage: `MFTLib` | Covered lines `<<FILL>>` / executable lines `<<FILL>>`; `<<FILL>>` percent. |
| Line coverage: `MFTLib.Index` | Covered lines `<<FILL>>` / executable lines `<<FILL>>`; `<<FILL>>` percent. The nine Windows-uncovered lines are listed below; they are Linux-only code paths covered by the Linux measurement. |
| `scripts/test-coverage-status.ps1` | Passed `<<FILL>>`; failed `<<FILL>>`; exit code `<<FILL>>`. |
| Native Release build | `Release\|x64`: errors `<<FILL>>`; warnings `<<FILL>>`; exit code `<<FILL>>`. |
| Native Debug build | `Debug\|x64`: errors `<<FILL>>`; warnings `<<FILL>>`; exit code `<<FILL>>`. |
| Native coverage, `scripts/native-coverage.ps1` | Covered lines `<<FILL>>` / executable lines `<<FILL>>`; `<<FILL>>` percent; exit code `<<FILL>>`. |
| aislop | Score `<<FILL>>` / 100; errors `<<FILL>>`; warnings `<<FILL>>`; `aislop ci` exit code `<<FILL>>`. The five accepted warnings are listed below. |
| Linux `scripts/coverage-linux.sh` under load | Ledger result: 5 of 5 whole runs at `66bdb6d`, each with 1621 passed, 0 failed, 84 skipped, 1705 total, under 32 processor hogs at normal priority; tests also ran at normal priority. |
| Elevated administrator run, `scripts/run-coverage.ps1` | Attended result `<<FILL>>`; total `<<FILL>>`; passed `<<FILL>>`; skipped `<<FILL>>`; failed `<<FILL>>`; `MFTLib` line coverage `<<FILL>>` percent; `MFTLib.Index` line coverage `<<FILL>>` percent. This run verifies live `UsnWatchSession` cancellation and `CancelIoEx` timing. |
| Deleted-identifier grep | Recorded zero hits for the 24 identifiers below outside `docs/superpowers/**` and `CHANGELOG.md`, using independent fixed-string searches of tracked files. |

The nine Windows-uncovered executable lines are `MFTLib/Index/BlockFile.Flush.cs:71`, `MFTLib/Index/BlockFile.Flush.cs:72`, `MFTLib/Index/BlockFile.Flush.cs:93`, `MFTLib/Index/BlockFile.Flush.cs:94`, `MFTLib/Index/CacheDirectory.cs:335`, `MFTLib/Index/CacheDirectory.cs:337`, `MFTLib/Index/CacheDirectory.cs:340`, `MFTLib/Index/CacheDirectory.cs:342`, and `MFTLib/Index/CacheDirectory.cs:345`. These are Linux-only paths for native mapped-file flushing and cache-file ownership locking, so Windows cannot execute them. The cross-platform coverage audit recorded no line uncovered on both platforms.

The five accepted aislop warnings are:

- `MFTLib.Tests/NativeSeamIsolationFixtures.cs:73`: AsyncFixer01 on an intentional compiled-code isolation control.
- `MFTLib.Tests/NativeSeamIsolationFixtures.cs:79`: AsyncFixer01 on an intentional compiled-code isolation control.
- `MFTLib/Index/CachedBlockDeletionOutcome.cs:8`: redundant documentation.
- `MFTLib/Index/CachedBlockDeletionOutcome.cs:10`: redundant documentation.
- `MFTLib/Broker/Host/JournalBrokerHost.cs:46`: eight-parameter constructor, accepted under ruling W2-2.

The deleted-identifier sweep covers `JournalBrokerClient`, `JournalBrokerScanSession`, `ScanSessionTestHarness`, `WatchStreamNotRunningException`, `DriveWatchFailure`, `ReadyOnFirstMoveWatchStream`, `WatchSession`, `_swapGate`, `_rescanGate`, `ArmEpoch`, `EndWatchAck`, `SendStartWatchAsync`, `StopLiveWatchAsync`, `QueryVolumesAsync`, `ArmScanAndCatchUpAsync`, `BrokerScanResult`, `BrokerDied`, `WriteWarning`, `_cacheOnlyUnresumableCheckpointOrdinals`, `_unreportedWatchFaults`, `ResumeDriveAfterRescanAsync`, `IndexDriveOpened.Ordinal`, `ReplaceWatchCursors`, and `WatchCursors`.

## Follow-ups filed

- [#293](https://gitea.fleet.sticktoitive.net/schoen/MFTLib/issues/293): A failed scan followed by disposal after the restart decision can throw `AggregateException` containing the scan failure and cancellation instead of cancellation alone.
- [#294](https://gitea.fleet.sticktoitive.net/schoen/MFTLib/issues/294): `BrokerDiagnostics.Log` can lose one line when `ReplaceWriterForTest` completes a writer after acquisition but before enqueue.
- [#295](https://gitea.fleet.sticktoitive.net/schoen/MFTLib/issues/295): The launch-timeout test still waits on a real 50 millisecond timer, leaving that timeout boundary nondeterministic.
- [#296](https://gitea.fleet.sticktoitive.net/schoen/MFTLib/issues/296): Specification gaps remain for stop after a failed restart, whether a fresh start discards a faulted instance's outstanding fault, the started-control-write bound, held-write semantics, and C5-Q1's heartbeat during processing within its limit.
- [#297](https://gitea.fleet.sticktoitive.net/schoen/MFTLib/issues/297): Deferred minor findings remain, including unbounded test cleanup waits and the four baseline aislop warnings.
- [#298](https://gitea.fleet.sticktoitive.net/schoen/MFTLib/issues/298): `MFTLibTestExtensions` line coverage is 80.37 percent and was outside the managed namespace gate's scope.

## Still owed

- M1, Concurrent versus sequential rescan: attended comparison of batched rescans and concurrent open against sequential operations across at least three real volumes, recording per-drive and total time, processor use, disk queue length, and native phase timings.
- M2, Liveness limits under concurrent scans: measure frame and heartbeat gaps, stalls, and lost catch-ups during M1 to validate the heartbeat, processing, and client stall limits.
- M3, Chunk size throughput: compare the 64 megabyte host chunk with the base commit's 262144-record chunk on the largest available volume and check throughput against measured frame gaps.
- F1: file-wizard pin-bump pull request carrying its migration table.
- G1 and G2: git-wizard pin-bump pull request carrying production and test migration together.
- [#264](https://gitea.fleet.sticktoitive.net/schoen/MFTLib/issues/264): ship the separate `MFTLib.TestExtensions` package needed by consumers using `BrokerTestHarness` through package references.

🤖 Generated with [Claude Code](https://claude.com/claude-code)
