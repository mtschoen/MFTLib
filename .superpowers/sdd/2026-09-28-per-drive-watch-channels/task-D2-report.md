# Task D2 report

Status: DONE

## Files changed

### docs/broker-integration.md

Rewritten entirely.

- BrokerProcess launch, ownership, HasEnded, Ended, disposal, and direct control operations.
- One control pipe plus one per-drive channel for each scan or watch.
- BrokerMftBlockProducer and BrokerIndexWatchSource wiring into FileIndex.
- Concurrent open progress and per-drive start, stop, catch-up wait, and rescan behavior.
- Consumer response to BrokerChannelLostException, DriveWatchFaultException, JournalCatchUpLostException, Recovering, and Faulted.
- Lost scan catch-up, explicit journal growth through BrokerProcess.GrowUsnJournalAsync, rescan, and restart.
- Callback reentrancy, liveness, diagnostics, and deployment.

### docs/broker-testing.md

Rewritten entirely because the prior text described deleted session and merged-stream test surfaces.

- BrokerTestHarness.StartInProcess and BrokerTestHarnessOptions.
- Production-surface fault observation through BrokerProcess.
- Per-drive fake IIndexWatchSource and IIndexDriveWatch shape.
- CacheDirectoryIsolation, JournalIsolation, and SyntheticJournalWindow.

### docs/broker-scan-tuning.md

Rewritten entirely.

- BrokerProcess block sizing and direct MFT sizing queries.
- Process-wide parse-thread admission and per-chunk rebalancing.
- Bounded catch-up reads, loud failure on entries without cursor advance, journal growth, rescan, and restart.
- Removed the session-only watch cursor section.

### docs/handoff-release-0.3.0.md

Updated the status section and downstream package lists.

- Records main at 3597586 and the implementation branch at 2889deb.
- Records issue 264 as a remaining release gate.
- Lists MFTLib 0.3.0 and MFTLib.TestExtensions 0.3.0 as the release artifact set.
- Adds the test package to both consumer migration package lists.

## Deleted identifier searches

Each name was searched separately across the four edited documentation files.

| Identifier | Result count |
| --- | ---: |
| JournalBrokerClient | 0 |
| JournalBrokerScanSession | 0 |
| ScanSessionTestHarness | 0 |
| WatchStreamNotRunningException | 0 |
| DriveWatchFailure | 0 |
| ReadyOnFirstMoveWatchStream | 0 |
| WatchSession | 0 |
| _swapGate | 0 |
| _rescanGate | 0 |
| ArmEpoch | 0 |
| EndWatchAck | 0 |
| SendStartWatchAsync | 0 |
| StopLiveWatchAsync | 0 |
| QueryVolumesAsync | 0 |
| ArmScanAndCatchUpAsync | 0 |
| BrokerScanResult | 0 |
| BrokerDied | 0 |
| WriteWarning | 0 |
| _cacheOnlyUnresumableCheckpointOrdinals | 0 |
| _unreportedWatchFaults | 0 |
| ResumeDriveAfterRescanAsync | 0 |
| IndexDriveOpened.Ordinal | 0 |
| ReplaceWatchCursors | 0 |
| WatchCursors | 0 |

## Code and specification differences

1. Specification section 2.2 describes Processing as producing Stalled after the processing limit and says state publication restarts the processing clock. The implementation and ruling C5-Q1 also send Heartbeat while a processing operation remains within the limit and has written no frame since the previous visit. The documentation describes the implementation.
2. Specification section 2.2 describes bounded independent heartbeat writes whose timeout closes that pipe. The implementation and ruling C5-Q2 skip a pipe while any write is already in flight; the client 30 second silence limit then ends the pipe. The documentation describes the implementation.
3. The release plan requires MFTLib.TestExtensions as a separate exact-version package for issue 264. At HEAD, MFTLibTestExtensions.csproj has IsPackable false and scripts/release.ps1 packs only MFTLib. The handoff documents issue 264 as required remaining work and does not claim the test package exists yet.

No other code and specification difference was found in the behavior documented by this lane.

## Verification

- Required files: all present.
- Initial worktree: clean.
- git diff --check: clean after edits.
- Edited documentation line endings: CRLF.
- En-dash and em-dash search: 0 results.
- Public identifiers and signatures: checked against MFTLib, MFTLib/Index, MFTLib/Broker, and MFTLibTestExtensions at HEAD.
- Code samples: checked against public signatures but not compiled, as this documentation lane requires no build or test.
- Build and test: not run, per docs-common.md for documentation lanes.
- Aislop command: aislop scan C:\Users\mtsch\MFTLib-worktrees\265-D2
- Aislop score: 99 / 100 Healthy, 0 errors, 5 warnings, 0 fixable.
- Aislop finding: AsyncFixer01 at MFTLib.Tests/NativeSeamIsolationFixtures.cs:73:5.
- Aislop finding: AsyncFixer01 at MFTLib.Tests/NativeSeamIsolationFixtures.cs:79:5.
- Aislop finding: ruled 8-parameter constructor at MFTLib/Broker/Host/JournalBrokerHost.cs:46.
- Aislop finding: redundant XML-doc summary at MFTLib/Index/CachedBlockDeletionOutcome.cs:8:1.
- Aislop finding: redundant XML-doc summary at MFTLib/Index/CachedBlockDeletionOutcome.cs:10:1.
- Findings beyond the baseline four plus the ruled constructor warning: none.

## Not verified

- The release script was not run. Issue 264 package work is not implemented at HEAD.
- Consumer ports and the attended Windows release run were not executed.
- Existing validation measurements in docs/handoff-release-0.3.0.md were retained and were not re-measured.
- README.md and AGENTS.md still name deleted broker surfaces at this lane's HEAD. D3 and D1 own those files, respectively, so this lane did not edit them.

## Fix round 1

- Open-time lost catch-up: `docs/broker-scan-tuning.md:70-74` now states that
  the drive settles `DriveState.Ready` with a queryable block while
  `WatchCatchUpState` is already `Faulted`, a later `StartWatchingAsync` is
  refused until `RescanAsync` succeeds, and the consumer then starts watching
  again.
- Lifecycle fault disposition: `docs/broker-integration.md:212-215` now states
  the current behavior that stopping after a failed source start clears the
  watch request and refused-start fault without rethrowing that failure, and a
  fresh start discards a faulted instance's outstanding fault.
- Stop race: `docs/broker-integration.md:257-259` was checked against the
  implementation and already states that a stop racing recovery or rescan
  restart wins and reports the stopped instance's outstanding fault once.
- Target-state wording: `docs/broker-integration.md:246` now describes the
  armed cursor as having become unreadable.
- Harness fault surface: `docs/broker-testing.md:24-34` already states owner
  ruling C2-Q1 completely. `BrokerTestHarness` has no fault surface of its own;
  host faults reach tests only through `BrokerProcess.Ended` or `HasEnded`,
  `BrokerChannelLostException`, and `Error` frames, and disposal does not throw
  a host fault. No edit was needed.

Each deleted identifier was searched separately across the four documentation
files after the fixes. Result counts: `JournalBrokerClient` 0,
`JournalBrokerScanSession` 0, `ScanSessionTestHarness` 0,
`WatchStreamNotRunningException` 0, `DriveWatchFailure` 0,
`ReadyOnFirstMoveWatchStream` 0, `WatchSession` 0, `_swapGate` 0,
`_rescanGate` 0, `ArmEpoch` 0, `EndWatchAck` 0, `SendStartWatchAsync` 0,
`StopLiveWatchAsync` 0, `QueryVolumesAsync` 0, `ArmScanAndCatchUpAsync` 0,
`BrokerScanResult` 0, `BrokerDied` 0, `WriteWarning` 0,
`_cacheOnlyUnresumableCheckpointOrdinals` 0, `_unreportedWatchFaults` 0,
`ResumeDriveAfterRescanAsync` 0, `IndexDriveOpened.Ordinal` 0,
`ReplaceWatchCursors` 0, and `WatchCursors` 0. Target-state history phrases,
en-dashes, and em-dashes also returned 0 results.

- Aislop command: `aislop scan C:\Users\mtsch\MFTLib-worktrees\265-D2`
- Aislop score: 99 / 100 Healthy, 0 errors, 5 warnings, 0 fixable.
- Findings: the four baseline warnings and the ruled 8-parameter constructor
  warning only. Findings beyond that set: none.
