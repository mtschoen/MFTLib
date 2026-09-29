# Context common to the documentation tasks D1, D2, D3

The per-drive watch channels plan (MFTLib issue 265, closes 252) is implemented on your base: every
code task is merged. Your task rewrites documentation to describe the code as it is now. The code
is the source of truth; the governing specification is
`docs/superpowers/specs/2026-09-28-per-drive-watch-channels-design.md` (read the sections you need;
where the code and the spec differ, describe the code and list the difference in your report).

## Writing rules

- Describe the target state. No history notes: no "no longer", "previously", "used to", "was
  replaced", "PR 230 review finding" and similar (the CHANGELOG deletion entries in D1 are the one
  place that names removed things, as entries).
- No em-dashes or en-dashes anywhere. Use " - ", colons or parentheses. Plain ASCII punctuation.
- Keep the voice and structure of the file you edit; AGENTS.md bullets are dense prose, README is
  user-facing with code samples.
- Every identifier you name must exist in the code at your base (grep it before you write it), with
  the exact casing and signature. Every code sample must compile against the public API as it is
  (read the public signatures in `MFTLib/`); do not invent members.
- Deleted identifiers: after writing, grep your files for each of these, one search per name, and
  remove every hit except CHANGELOG deletion entries: `JournalBrokerClient`,
  `JournalBrokerScanSession`, `ScanSessionTestHarness`, `WatchStreamNotRunningException`,
  `DriveWatchFailure`, `ReadyOnFirstMoveWatchStream`, `WatchSession`, `_swapGate`, `_rescanGate`,
  `ArmEpoch`, `EndWatchAck`, `SendStartWatchAsync`, `StopLiveWatchAsync`, `QueryVolumesAsync`,
  `ArmScanAndCatchUpAsync`, `BrokerScanResult`, `BrokerDied`, `WriteWarning`,
  `_cacheOnlyUnresumableCheckpointOrdinals`, `_unreportedWatchFaults`, `ResumeDriveAfterRescanAsync`,
  `IndexDriveOpened.Ordinal`, `ReplaceWatchCursors`, `WatchCursors`. Report every search and its
  result count.

## Behavior decided during implementation that the docs must state (controller rulings)

Read `orchestrator-rulings.md` beside this file. The ones that change public behavior:
- C2-Q1 (owner): `BrokerTestHarness` has no fault surface of its own; an in-process host fault
  reaches tests only through `BrokerProcess.Ended`/`HasEnded`, `BrokerChannelLostException` on
  pending operations, and `Error` frames; disposal never throws a host fault.
- C5-Q1: the host heartbeats an idle control pipe unconditionally, a watch pipe waiting on its
  volume, a queued scan, and a processing operation that made progress within the processing
  limit; a processing operation with no progress past the limit gets `Stalled` and its channel is
  cancelled. C5-Q2: a pipe with a write in flight is skipped by the heartbeat sender; the client's
  stall limit (30 s, any frame counts) ends it.
- B5-Q1 and B9: `OpenAsync` settles every drive concurrently; a drive whose catch-up is lost at
  open rescans itself up to `FileIndex.LostCatchUpRecoveryLimit` (3) times and then settles `Ready`
  with its last block, unresumable, and its watch refused until `RescanAsync`.
- B9-Q1: `OpenProgress` is reported once for each drive that settles, from its settling thread,
  with no lock held; reports may overlap and arrive out of `SettledCount` order; `SettledCount`
  gives the settle order (keep the report with the largest `SettledCount`). B9-Q2: a drive whose
  settle is cancelled reports nothing; a cancelled or failed open may have reported only some
  drives.
- B4 note: after rescanning an unresumable drive the consumer calls `StartWatchingAsync` again
  (a refused start leaves no watch request).
- W4fix: a bounded catch-up read that returns entries without advancing its cursor fails the
  catch-up (loud), rather than delivering duplicates.
- Recovery (B6): a `Drive` or `Apply` fault publishes `Recovering`, raises `WatchFaulted`, and
  rescans the drive automatically; a second fault before `CaughtUp`, or a failed recovery scan,
  raises `WatchFaulted(Recovery, X)` and leaves X `Faulted` until the consumer calls `RescanAsync(X)`
  or `StartWatchingAsync(X)`; `Channel` faults never recover. A stop that lands while a recovery
  or rescan is restarting the watch wins and rethrows the stopped instance's fault once.
- Reentrancy (B8): from inside a `Changed` or `WatchFaulted` handler of an index, every lifecycle
  call on that index (start, stop, rescan, unsettled catch-up wait, dispose, and their batched
  forms) fails at once with `InvalidOperationException`; queue it (for example `Task.Run`) to run
  after the handler returns. Queries, `Drives`, `DriveStatus` and settled waits are allowed.

## Owner decisions still open (state current behavior only; do not present them as settled design)

- What a stop does after a failed restart (it clears the request and the refused-start fault
  without rethrowing it).
- Whether a fresh start discards a faulted instance's outstanding fault (it does).
- The aislop baseline (four pre-existing warnings) and the ruled 8-parameter constructor warning.

## Lane rules

Work only in your worktree; absolute paths; never write in `C:\Users\mtsch\MFTLib`. Do not acquire
or release any lock. Do not run `git add`, `git commit` or anything that writes git metadata: the
controller commits your work from the commit message you leave (see your dispatch). No build or
test is needed for documentation, but run `aislop scan <worktree>` once at the end (it checks
markdown too) and report its score and every finding beyond the baseline four plus the ruled
constructor warning.
