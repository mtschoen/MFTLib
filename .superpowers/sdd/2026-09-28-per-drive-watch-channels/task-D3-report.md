# Task D3 report

Status: DONE

## Files and sections rewritten

### README.md

- Choose an integration model: replaced the deleted broker client and scan session recommendation with BrokerProcess and its index adapters.
- When a rescan happened because the journal moved on: documented DriveOpening, LiveWatch, and ScanCatchUp reports; cache-only adoption; automatic recovery; and current non-null WatchFault.DriveLetter handling.
- Keep the application non-elevated: replaced deleted APIs with ElevatedEntryPoint, BrokerProcess.LaunchAsync, BrokerMftBlockProducer, BrokerIndexWatchSource, direct broker entry points, ownership, and process-end reporting.
- Build a live index with FileIndex: added exact single-drive and batched StartWatchingAsync, StopWatchingAsync, RescanAsync, and WaitForCatchUpAsync forms; DriveOperationResult outcomes; and the no-list batched forms. Preserved and clarified the B9 OpenProgress and SettledCount rules.
- Errors and recovery: documented all WatchFaultKind values, WatchCatchUpState.Recovering, DriveStatus recovery fields, lost catch-up retries, handler reentrancy, outstanding stop faults, process and drive channel failures, and current heartbeat and stall behavior.

### docs/index-format.md

- Watch catch-up: rewrote the section for one handle and catch-up slot per drive, the single-drive wait contract, automatic recovery states, and batched waits returning DriveOperationResult values in request order.

## Deleted identifier grep

Each name was searched separately with fixed-string rg over README.md and docs/index-format.md after the final documentation edit.

- JournalBrokerClient: 0
- JournalBrokerScanSession: 0
- ScanSessionTestHarness: 0
- WatchStreamNotRunningException: 0
- DriveWatchFailure: 0
- ReadyOnFirstMoveWatchStream: 0
- WatchSession: 0
- _swapGate: 0
- _rescanGate: 0
- ArmEpoch: 0
- EndWatchAck: 0
- SendStartWatchAsync: 0
- StopLiveWatchAsync: 0
- QueryVolumesAsync: 0
- ArmScanAndCatchUpAsync: 0
- BrokerScanResult: 0
- BrokerDied: 0
- WriteWarning: 0
- _cacheOnlyUnresumableCheckpointOrdinals: 0
- _unreportedWatchFaults: 0
- ResumeDriveAfterRescanAsync: 0
- IndexDriveOpened.Ordinal: 0
- ReplaceWatchCursors: 0
- WatchCursors: 0

The same final check found 0 em-dash or en-dash characters in the two edited documentation files.

## Code and specification differences

- Design section 2.6.6 says every scan operation, including the open settle, raises WatchFaulted(CatchUpLost). FileIndex.ScanOpenedDriveAsync explicitly raises no event during OpenAsync because no handler can be subscribed before the index is returned. README.md describes the code: OpenAsync records the loss on DriveStatus and raises no WatchFaulted event.
- Design section 2.2 says heartbeat writes are independently bounded and a blocked heartbeat write closes that pipe. HostPipeWriter.Visit implements ruling C5-Q2: when any write is already in flight, the heartbeat sender skips that pipe without waiting. README.md describes the code and the client-side 30-second no-frame limit.
- Design section 2.2 does not state the C5-Q1 processing-progress rule completely. HostPipeWriter sends Heartbeat while a Processing operation is within BrokerLiveness.ProcessingLimit and sends Stalled only after progress has stopped past the limit. README.md describes the code.
- Design section 2.6.7 gives only the base OpenProgress settle-order rule. The B9 rulings and FileIndex.Scanning add overlapping callbacks, out-of-order arrival, no report for a cancelled settle, and the requirement to keep the largest SettledCount. README.md describes the code and rulings.

No other code/specification difference was found in the behaviors described by these edits.

## Verification

- Public signatures and named identifiers were checked at worktree HEAD in MFTLib, MFTLib/Index, MFTLib/Broker, and MFTLibTestExtensions.
- git diff --check: clean.
- README.md line endings: CRLF only, 0 bare LF before the aislop run.
- docs/index-format.md line endings: CRLF only, 0 bare LF before the aislop run.
- Aislop command: aislop scan C:\Users\mtsch\MFTLib-worktrees\265-D3
- Aislop score: 99 / 100 Healthy, 0 errors, 5 warnings, 0 fixable.
- Pre-existing warnings: AsyncFixer01 at MFTLib.Tests/NativeSeamIsolationFixtures.cs lines 73 and 79; redundant XML-doc summaries at MFTLib/Index/CachedBlockDeletionOutcome.cs lines 8 and 10.
- Ruled warning: JournalBrokerHost constructor at MFTLib/Broker/Host/JournalBrokerHost.cs line 46 has 8 parameters.
- Findings beyond the baseline four plus the ruled constructor warning: none.

## Not verified or out of scope

- No build or test was run, as docs-common.md says none is required for this documentation lane. README code samples were checked against the public signatures at HEAD but were not compiled.
- The companion broker documents and AGENTS.md still contain deleted identifiers in this isolated worktree. They are owned by documentation lanes D1 and D2 and were outside D3's edit permission.
- The D1 and D2 documentation changes were not present to cross-check as a merged set.

## Fix round 1

- Finding 1: README.md lines 336-342 and 574-581 now tell the consumer to call
  `StartWatchingAsync` again after a successful manual `RescanAsync`, because a refused
  start retains no watch request for the rescan to restart.
- Finding 2: README.md lines 694-701 now label the edge dispositions as current behavior
  and state the refused-start stop behavior, fresh-start fault disposition, and the rule
  for a stop racing a recovery or rescan restart.
- Finding 3: README.md lines 652-655 now state that a bounded catch-up read which returns
  entries without advancing its cursor fails without delivering those entries, after which
  the live journal check reports `CatchUpLost` or `Error`.
- Finding 4 ruling: README.md has no `BrokerTestHarness` reference, so no harness fault
  surface sentence was added.
- Aislop command: `aislop scan C:\Users\mtsch\MFTLib-worktrees\265-D3`.
- Aislop score: 99 / 100 Healthy, 0 errors, 5 warnings, 0 fixable. The findings are the
  baseline four warnings and the ruled 8-parameter `JournalBrokerHost` constructor warning;
  there are no additional findings.
