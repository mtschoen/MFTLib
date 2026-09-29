# Per-drive watch channels: design

Status: draft for owner review, 2026-09-28, amended the same day for the independent
review on MFTLib pull request 266 and for owner rulings 5 to 7. Anchor issue:
https://gitea.fleet.sticktoitive.net/schoen/MFTLib/issues/265. Rulings: the
2026-09-28 comments on that issue. Read at MFTLib `692820f` (main). Consumers read
at file-wizard `f4f9940` and git-wizard `15be5f3`, both on `main` and equal to
`gitea/main` after a fetch on 2026-09-28.

This spec is scaffolding for plan-writing. It gets distilled into the plan header
and deleted at the next handoff. Every `file:line` below was read at the commits
above. It runs past 600 lines because sections 2.6, 3, 4, 8 and 9 are enumerations
(a state machine, signatures, mechanisms, consumer files, test files) that the plan
needs in full.

## 1. Problem and goal

Every watched drive shares one pipe, one host frame loop, one client pipe reader,
one `BrokerIndexWatchSource` stream, one `FileIndex` watch session and one pump.
The fences built on top of that sharing are:

| Shared thing | Where | Fence it forced |
|---|---|---|
| One pipe, one host frame loop | `JournalBrokerHost.Session.cs:46`, `:69-139` | host write lock; a scan blocks every other request, because `HandleArmAndScanAsync` is awaited inline in the loop (`:87-92`) |
| One client reader | `JournalBrokerClient.cs:42-48`, `JournalBrokerClient.ControlExchange.cs:126-164` | arm-ordering gate, control exchange routed through the demux, foreground-read refusal |
| One arm namespace on the wire | `BrokerFrame.cs:27-31`, `JournalBrokerClient.LiveWatchDemux.cs:12-16`, `:164-196` | per-drive arm epochs echoed by the host |
| One stream per source | `BrokerIndexWatchSource.cs:32-36`, `BrokerIndexWatchSource.AbandonedStart.cs` | stream claim, abandoned-start teardown, awaiting-reader bookkeeping |
| One session and pump per index | `FileIndex.cs:88`, `FileIndex.WatchPump.cs:5-68` | ended-session reclaim and restart, unreported-fault ledger, readiness waits (`FileIndex.Rescan.Watch.cs`, `FileIndex.WatchFaults.cs`) |
| Index-wide gates | `FileIndex.cs:48-49` | one rescan at a time; a journal batch on one drive blocks every other drive |

Goal: every drive owns its own pipe, its own watch, its own pump, its own rescan
gate and its own write gate. Nothing index-wide exists except the snapshot and the
short step that publishes it. Nothing connection-wide exists except the process and
its control pipe.

Failure policy that replaces the fences: a per-drive fault (journal wrapped, drive
removed, a batch that cannot be applied, a scan whose catch-up was lost) recovers
quietly by rescanning that drive, except that three consecutive lost catch-ups on one
drive stop the automatic rescans and the fault suggests a larger journal
(section 2.6.6). Connection-level uncertainty (a cancelled or timed-out operation, a
closed or stalled pipe) closes that drive's channel and fails loudly. A channel is
never repaired: it is closed, and a new one is opened by whoever decides to try again.

## 2. Design

### 2.1 Process model

One elevated process per consumer session, one elevation prompt, many pipes.

- The unelevated client creates the control pipe server
  (`mftlib-broker-{guid}`) and launches `--broker --pipe {name}` through
  `BrokerLauncher.Launch`, exactly as today (`JournalBrokerClient.Connection.cs:66-99`).
  The broker connects as the pipe client. That direction is kept for every pipe:
  high integrity connects to medium integrity (`DefaultElevatedEntryRunner.cs:26-28`).
- To open a drive channel the client creates a new pipe server
  (`mftlib-broker-{guid}-{drive}-{sequence}`), sends `OpenChannel` on the control pipe
  naming the drive and the pipe, and waits for both the broker's connection and the
  `ChannelOpened` reply. The pipe name crosses the process boundary in that frame, so
  the broker never needs a name on its command line beyond the control pipe.
- A drive channel carries exactly one operation: one scan, or one watch. It is opened
  for that operation and closed when the operation ends. Stopping a watch is closing
  its pipe. Re-arming a drive is opening a new channel.
- A drive pipe closing (either side) ends that channel's operation only. The host
  cancels the operation's token; for a scan that stops the native parse, and the
  volume handle, the section and the scan's parse threads are released only once the
  parse has returned (section 2.3). Other channels are untouched.
- The control pipe is the process's lifeline. When it closes, the host cancels every
  channel, waits a bounded grace period for their tasks, returns from `ServeAsync`, and
  `DefaultElevatedEntryRunner` exits the process. Every drive pipe then reads EOF on the
  client side, so every open channel fails with a drive-named
  `BrokerChannelLostException`.
- Process death (crash, kill) looks the same to the client: every pipe reads EOF.
  `BrokerProcess.Ended` fires once, from the control pipe's reader.

### 2.2 Wire protocol

Framing is unchanged: a 4-byte little-endian length, then a kind byte and payload.
The client and broker are always the same executable, so `BrokerFrameKind` is
renumbered densely.

Control pipe. Every request and reply carries a nonzero `RequestId` (uint32). One
client reader routes replies by id. Requests are handled concurrently by the host;
control writes on each side are serialized by a control-only write lock.

| Kind | Direction | Payload | Reply |
|---|---|---|---|
| `OpenChannel` | client to host | RequestId, Drive, PipeName | `ChannelOpened` or `Error` |
| `QueryVolume` | client to host | RequestId, Drive | `VolumeInfo` or `Error` |
| `GrowUsnJournal` | client to host | RequestId, Drive, MaximumSize, AllocationDelta | `UsnJournalSettings` or `Error` |
| `ChannelOpened`, `VolumeInfo`, `UsnJournalSettings`, `Error` | host to client | RequestId plus today's fields | |
| `Heartbeat` | host to client, any pipe | none | |
| `Stalled` | host to client, any pipe | Message | |

Request ids and cancellation, client side:

- Ids are allocated under the pending-request lock from a counter that skips 0 and every
  id still in the pending table, so a new id is never zero and never an id that is
  outstanding or whose caller stopped waiting. When every nonzero id is in the table,
  allocation fails with `InvalidOperationException`. An entry leaves the table when its
  reply arrives or when the process ends, never earlier.
- A caller's token is observed until its request frame starts writing. Cancellation
  before that sends nothing and releases the id.
- Once a request frame starts writing, it is written to completion under a token the
  process owns, bounded by the stall limit (below). A write that fails or exceeds that
  bound ends the control connection loudly: the client closes the control pipe,
  `BrokerProcess.Ended` fires, and every pending request fails with
  `BrokerChannelLostException`. A partly written frame is never followed by another
  frame, because the host's reader requires every advertised byte
  (`JournalBrokerHost.cs:246-260`); today's client aborts an interrupted exchange for the
  same reason (`JournalBrokerClient.ControlExchange.cs:67-73`).
- Only a fully written request whose caller stopped waiting (cancelled, or past its reply
  timeout) uses the late-reply rule: its entry stays in the table and its reply is
  dropped when it arrives.

Drive pipe. The drive is fixed by `OpenChannel`, so no drive pipe frame carries a
drive field or an arm tag. The client writes exactly one request frame; everything
after it flows host to client.

| Operation | Client frame | Host frames, in order |
|---|---|---|
| Scan | `ArmAndScan(SectionName, Profile, KeepFileNames)` | `Cursor`, `ScanProgress`\*, `ScanReady`, then one terminal frame: `JournalBatch` when catch-up succeeded, `CatchUpLost(Loss, Message)` when catch-up failed and the live journal proves the armed cursor lost; or `Error` at any point before a terminal frame, including a catch-up failure the journal does not prove |
| Watch | `StartWatch(JournalId, NextUsn)` | `CaughtUp` once, `JournalBatch`\* in any order around it, `Heartbeat` when idle; `Error` or `Stalled` ends it |

The scan order is the order the retained pipeline writes: the block is finished and
`ScanReady` is written before catch-up runs (`JournalBrokerHost.Scan.cs:204-217`). The
client's scan collector accepts exactly this order and treats any other as a lost
channel. `ArmAndScan` carries no thread count (section 2.3). There is no `Warning`
frame: its only use was a failed catch-up (`JournalBrokerHost.Scan.cs:228-235`), which
`CatchUpLost` replaces (sections 2.3 and 2.6.6).

The host closes its end of a drive pipe after a terminal frame (`JournalBatch` or
`CatchUpLost` after `ScanReady`, or `Error`). The client closes its end to cancel.

Liveness. Known failures are reported by an explicit signal; timers are the backstop.

| Failure | Signal |
|---|---|
| Journal wrapped, deleted or inactive | Native error code, host `Error` frame |
| Journal wrapped during a scan | Host `CatchUpLost` frame |
| Drive removed | The pending read fails, host `Error` frame |
| Broker process ended | Every pipe reads EOF |
| Operation loop wedged in host code | Host watchdog `Stalled` frame (below) |
| Process frozen | Heartbeats stop on all pipes; client stall limit |
| Pipe write on channel X blocked | Heartbeat write to X times out or X stalls; other channels heartbeat undisturbed |
| Read wedged inside the kernel | Not detectable: it looks the same as a quiet drive |

An idle watch is blocked in `FSCTL_READ_USN_JOURNAL` with no limit
(`MFTLibNative/usn/usn_journal.cpp:54`, `:339-340`), and that call's `Timeout` field
neither returns early nor applies to the asynchronous handle the watch uses, so the
operation loop cannot write heartbeats itself. The control pipe is similarly idle
between client requests. Each pipe (the control pipe and every drive channel) therefore has:

- An operation state the loop publishes: `Idle` (control loop waiting for a client
  request), `WaitingOnVolume` (watch channel awaiting journal changes), `Queued`
  (a scan waiting for admission, section 2.3), or `Processing` with the time it began.
  Sources publish it through the operation-state seam of section 2.9.
- A heartbeat sender, run on one dedicated thread per process (not the thread pool, so
  a scan that saturates the pool cannot starve it). Every heartbeat interval (5 s) it
  visits each pipe that has written nothing since the last visit. `Idle` (on the control
  pipe), `WaitingOnVolume` (on a watch pipe) and `Queued` (on a scan pipe) write
  `Heartbeat`. `Processing` for longer than the processing limit (30 s) writes `Stalled`
  naming the wedged step, then cancels that channel: a stall the host can see is reported
  as a named failure, not as silence. `Stalled` is its own frame kind, not `Error`,
  because a stall is channel uncertainty: the client fails the operation with
  `BrokerChannelLostException` carrying the host's message, which the index reports as a
  loud `Channel` fault with no recovery rescan.
- Bounded, independent heartbeat writes: heartbeat writes to each pipe are independent
  and bounded by a short write timeout (or non-blocking write attempt). A write that
  blocks on pipe X (for example when X's OS pipe buffer is full because X's client
  reader is wedged) never blocks the sender thread or delays heartbeats to pipe Y or
  the control pipe. If a heartbeat write to X exceeds its bound or fails, channel X is
  treated as stalled/lost and closed; all other channels continue to receive heartbeats
  on schedule.
- The processing clock restarts whenever the loop writes a frame or republishes its
  state, so it measures time without progress, not time spent working. A scan republishes
  at every progress step. Any scan phase that can run longer than the processing limit
  without a progress step needs one added; the plan audits the scan pipeline for this.
- A client stall limit (30 s): a pipe whose read sees no frame of any kind for that long
  is closed and its operation fails with `BrokerChannelLostException`. Any frame counts,
  so `ScanProgress` keeps a scan pipe alive, and periodic `Heartbeat` frames keep an idle
  control pipe, watch pipe or queued scan pipe alive. On the control pipe that is the
  process ending.

Control requests (`OpenChannel`, `QueryVolume`, `GrowUsnJournal`) also carry their own
reply timeout. All intervals come from an injected `TimeProvider` (section 2.9), so
tests advance a fake clock.

### 2.3 JournalBrokerHost

- `ServeAsync(Stream control, BrokerChannelConnector connectChannel,
  IBlockSectionWriter? blockSectionWriter, CancellationToken)` reads control frames and
  dispatches each request to its own task. `connectChannel` opens a
  `NamedPipeClientStream` to the named pipe in production and an in-memory stream in
  tests.
- Channel open, host half. On `OpenChannel` the host calls `connectChannel(pipeName,
  token)` bounded by the channel connect limit (30 s on the injected clock). On success
  it replies `ChannelOpened` and serves the channel on its own tracked task. On failure
  or timeout it replies `Error` with the request id and disposes whatever was connected.
  A served channel waits for its first request frame bounded by the first-request limit
  (30 s); if none arrives, or the first frame is neither `ArmAndScan` nor `StartWatch`,
  it writes `Error` where it can, disposes the pipe and ends. The client half is in
  section 2.4.
- Each channel runs `ServeChannelAsync(stream, drive)`: once the first request is read,
  one reader task watches for EOF (which cancels the channel's token) and one task runs
  the operation. The operation's completion cancels and awaits the reader task, so a
  channel leaves no task behind. Scan is today's `ProcessDriveScanAsync` pipeline
  (`JournalBrokerHost.Scan.cs:45-85`) for one drive. Watch is today's `StreamWatchAsync`
  (`JournalBrokerHost.cs:47-132`) without arm epochs. `DescribeWatchFailure`
  (`JournalBrokerHost.cs:169-183`) keeps its rescan wording.
- Lost catch-up, proven by the journal. When catch-up after `ScanReady` fails, the host
  does not classify the exception: `MftVolume.ReadUsnJournal` throws the same
  `InvalidOperationException` for a null native result and for any native error text,
  whether the journal trimmed past the cursor, the drive went away or the read failed
  (`MFTLib/Journal/MftVolume.Journal.cs:76-88`). The journal is the classifier, never
  exception wording, as it already is for a failed watch start (`DescribeWatchFailure`,
  `JournalBrokerHost.cs:169-183`). The host runs
  `JournalCheckpointCheck.Check(drive, armed.JournalId, armed.NextUsn,
  JournalCheckpointLossDetection.ScanCatchUp)` against the live journal. When it returns a
  loss (the armed cursor is below the journal's `FirstUsn`, or the journal id changed), the
  host writes `CatchUpLost` carrying that loss's fields and the failure message, and closes
  the pipe. Otherwise (the cursor is still retained, or the journal cannot answer) the
  failure is an ordinary drive error on this scan: the host writes `Error` with the failure
  message, and the scan fails as any scan does. In neither case does the host query a fresh
  cursor or name a position to watch from; today it writes a `Warning`, re-queries the
  cursor and ships an empty terminal batch at that position
  (`JournalBrokerHost.Scan.cs:213-238`). The check runs on the host side because the host
  already holds the volume and already makes the same check for watch failures; the index
  trusts the frame and does not query the journal again.
- A write that finds the pipe gone ends that channel quietly, as `TryWriteFrameAsync`
  does today (`JournalBrokerHost.cs:216-229`). The session-ending
  `ClientDisconnectedException` path applies to the control pipe only.

**Scan admission and parse threads (rulings 4 and 6).** Scans run concurrently, one per
scan channel (section 7b), under one process-wide budget of `P` parse threads, where `P`
is the host's `processorCount` (section 2.9).

- Admission. At most `P` scans run at once, so every running scan has at least one parse
  thread: no more than one scan per core. A scan channel past that waits in admission
  order; while waiting its state is `Queued`, which heartbeats, so a queued scan is not a
  stall. Closing a queued scan's pipe removes it from the queue, and its scan source is
  never invoked.
- Rebalancing. Every running scan holds a `ParseThreadAllowance` (section 3) that the host
  owns. Whenever a scan is admitted, finishes, or is cancelled, the host recomputes every
  allowance under the budget's lock: with `n` running scans each gets `P / n` rounded
  down, and the first `P mod n` in admission order get one more. The native parser reads
  its allowance at the start of every chunk and before path resolution. The parser already
  creates its workers per chunk and joins them when the chunk is done
  (`ParseChunkParallel`, `MFTLibNative/mft/mft.parse_core.cpp:110-139`, called per chunk
  at `:331-333`), and path resolution fans out over the same count (`:443`); today the
  count is read once per parse (`EffectiveThreadCount()`, `:405`).
- Accepted limit (ruling 6). A chunk already in progress finishes with the count it
  started with, so for up to one chunk after an admission the total can exceed `P`.
- No caller supplies a thread count. `ArmAndScan` carries none, `BrokerScanOptions` has no
  thread field, `MftBlockProduceRequest` has none, and `FileIndex` passes none for open,
  single-drive rescan, batched rescan or recovery.
- A cancelled scan keeps its place in the budget until its native work has stopped (below).
  The budget counts parse threads that exist, not scans that are still wanted.

**Native scan cancellation.** Today closing a scan pipe cancels only a managed token, and
nothing reads it until the parse is over: `ScanDriveRecordBatches` checks it after
`ReadRecordBatches` yields (`JournalBrokerHost.Sources.cs:9-17`), `ReadRecordBatches`
runs `StreamRecords` to completion before its first batch (`MFTLib/Mft/MftVolume.cs:65-72`),
and the native call takes no cancellation (`MftVolume.cs:129-130`). A closed scan pipe
therefore leaves the whole parse running, holding the volume handle and the section.

- The native export `ParseMFTRecordsWithProgress` takes a pointer to a caller-owned
  control block, `MftParseControl { int32 cancelRequested; int32 parseThreadAllowance; }`,
  which the managed caller keeps pinned for the call. The native code reads both fields
  atomically: `cancelRequested` before each chunk read, after each chunk's parse, in each
  parse worker between 4096-record sub-slices, and between path-resolution slices;
  `parseThreadAllowance` at the start of each chunk and before path resolution. An
  allowance of 0 means every processor (`EffectiveThreadCount()`); the `SetMaxThreads`
  test hook still caps it.
- A cancelled parse joins its I/O thread, frees every buffer it allocated, and returns a
  result whose new `cancelled` field is 1. `MftParseResult` gains that field, so
  `MFT_NATIVE_ABI_VERSION` changes.
- `MftVolume.StreamRecords` and `MftVolume.ReadRecordBatches` take a
  `ParseThreadAllowance?` and a `CancellationToken` (section 3). A token registration sets
  `cancelRequested`; the allowance writes through to the control block while the parse
  runs and is detached before the block is freed; a cancelled result throws
  `OperationCanceledException`. `ReadRecordBatches` also checks the token between batches.
- Ownership on teardown. A scan channel's operation task owns the volume handle, the
  section writer and the scan's place in the budget. It releases them, in that order, only
  after the native call has returned and the batch enumerator is disposed. Closing the pipe
  requests the stop; it never releases a resource native work is still using. The longest
  stop is one worker sub-slice or one in-flight chunk read.

### 2.4 Client: BrokerProcess and channels

`JournalBrokerClient` becomes `BrokerProcess`: a handle to the elevated process plus
its control pipe. It owns no watch state and no scan state.

- `LaunchAsync` creates the control pipe, launches, and waits for the connection
  (today's `SpawnAndConnectAsync` body).
- `QueryVolumeAsync` and `GrowUsnJournalAsync` are control requests under the request id
  and cancellation rules of section 2.2.
- Channel open, client half. Opening a drive channel is one owned operation: create the
  pipe server, register the request id, write `OpenChannel`, await the host's connection
  and the `ChannelOpened` reply together (in either order), then write the channel's one
  request frame. Every failed branch disposes the pipe server and any connected stream:
  caller cancellation, an `Error` reply, a connection with no acknowledgement or an
  acknowledgement with no connection within the control reply timeout, and a failed
  first write. The host then observes EOF on whatever it holds, and its bounded waits
  (section 2.3) end any side the client never reached. An `Error` reply throws
  `InvalidOperationException` with the host's message; control loss throws
  `BrokerChannelLostException` naming the drive.
- `ScanDriveAsync` sizes the block through `QueryVolumeAsync`, creates the section
  (today's `PrepareDriveBlock`, `JournalBrokerClient.BlockScan.cs:31-72`), opens a scan
  channel, reads that one drive's frames in the section 2.2 order (today's
  `ScanCollector` reduced to one drive), transfers the block to the caller, and closes the
  channel. A `CatchUpLost` terminal frame still transfers the block, because the scan
  itself is complete: the result carries `CatchUpLoss` (the host's proven
  `JournalCheckpointLoss`, detected during `ScanCatchUp`) and no advanced cursor. An
  `Error` frame after `ScanReady` fails the scan like any other `Error`: the section is
  disposed and no block is returned.
  Cancellation closes the channel and disposes the section; the caller sees
  `OperationCanceledException`.
- `OpenWatchChannelAsync` (internal) opens a watch channel with `StartWatch` as its first
  request and returns a `BrokerWatchChannel`. Its `ReadAsync` reads frames straight off
  the pipe: there is no intermediate queue, so a slow consumer slows only its own pipe.
- Every failure of a channel that is not a host `Error` frame is a
  `BrokerChannelLostException` naming the drive; a `Stalled` frame is one, carrying the
  host's message. A host `Error` frame on a watch channel is a `DriveWatchFaultException`;
  on a scan channel it is an `InvalidOperationException` carrying the host message, as
  today.

### 2.5 Watch source

`IIndexWatchSource` starts one drive and returns a handle. The handle is the drive's
watch: reading it yields that drive's items, disposing it stops that drive.

- `StartAsync` returns once the watch is ready: for the broker source, once the channel
  is connected and `StartWatch` is written. There is no readiness callback. It observes
  its token until it returns; a start cancelled after its channel opened closes that
  channel before throwing.
- `ReadAsync` yields `JournalBatch` and one `DriveCaughtUp`, then runs until its token is
  cancelled or the handle is disposed. Cancelling its token ends the enumeration promptly
  (the broker source closes its pipe); that is how the index stops a pump. Otherwise it
  ends only by throwing: `DriveWatchFaultException` when the drive's own watch failed,
  any other exception when the channel carrying it was lost. A normal end before
  cancellation or disposal is treated as a lost channel.
- `DisposeAsync` completes once nothing further for that drive can be read. The index
  calls it exactly once for every handle a source returns (section 2.6.4), so a source
  need not make it idempotent.

`BrokerIndexWatchSource` is a thin adapter: `StartAsync` gets the process from the
connect factory, calls `OpenWatchChannelAsync`, and wraps the channel. It keeps no
per-drive maps.

### 2.6 FileIndex: per-drive state machine

Every watch and scan operation on drive X acts on X's records only. `_stateLock` guards
every field named in this section unless stated otherwise. This section is the contract
for start, stop, rescan, recovery, batches, faults and disposal; section 5 adds the lock
order.

#### 2.6.1 Records

One `DriveRuntime` per configured drive, created by `OpenAsync` and kept until disposal:

| Field | Purpose |
|---|---|
| Lifecycle gate (`SemaphoreSlim`, async; not under `_stateLock`) | Serializes start, rescan, recovery and disposal for X |
| Write gate (`SemaphoreSlim`; not under `_stateLock`) | Serializes X's batch application against X's block commit |
| `Current` | X's current watch instance, or none |
| `Retiring` | The previous instance until its `Drained` completes |
| `WatchRequested` | Set by start; cleared by stop and disposal. A rescan or recovery restarts X's watch only while it is set |
| `Recovery` | X's queued or running recovery ticket, or none (section 2.6.5) |
| `ConsecutiveLostCatchUps` | Scans of X in a row whose catch-up was lost (section 2.6.6) |

Each start creates a `WatchInstance` with its own identity: the object reference, plus a
per-drive generation number for diagnostics. It owns the start's cancellation source
(linked to the disposal token), the `IIndexDriveWatch` handle once returned, the pump task
and its stop source, the instance's catch-up slot, its outstanding fault, whether its
subscriber fault was announced, the block it was armed from (`ArmedBlock`), its `State`,
and a `Drained` task that completes when teardown has finished: the source's `StartAsync`
has returned, any handle is disposed, and the pump has returned. `Drained`, the catch-up
slot's waiters, the recovery ticket's completion and every other completion source of the
state machine complete their continuations asynchronously (section 2.6.8).

Instance states: `Starting` (source invoked, handle not yet published), `Running` (handle
published, pump running), `Faulted` (pump ended with a fault), `Retiring` (stopped or
superseded, teardown in progress), `Drained` (terminal).

`DriveStatus.WatchCatchUp` reports X from these records: `NotStarted` with no current
instance; `CatchingUp` or `CaughtUp` from a `Running` instance's slot; `Recovering` while
a recovery ticket exists or a lost catch-up is being rescanned; `Faulted` for a `Faulted`
instance with no recovery, or for a drive whose watch is refused because its block is
unresumable.

#### 2.6.2 Scoping rule

Every effect a pump has is conditioned on its own instance. A batch takes X's write gate,
then checks under `_stateLock` that `Current` is still this instance, still `Running`, and
that `ArmedBlock` is still X's published block; otherwise the batch is dropped, not
applied. Catch-up completion, fault recording, checkpoint-loss recording, recovery
queuing and cleanup check `ReferenceEquals(Current, instance)` the same way. A retiring
pump therefore never touches its successor's block or state. The rule is needed because
`ApplyJournalEntriesCore` resolves the block from whichever snapshot is current when it
acquires the gate (`FileIndex.Watch.cs:279-295`), and closing a pipe cannot retract a
batch a pump has already read.

#### 2.6.3 Linearization points

Each operation takes effect at one step under `_stateLock`:

| Operation | Takes effect when | Before it | After it |
|---|---|---|---|
| `StartWatchingAsync(X)` | the returned handle is stored and the instance moves from `Starting` to `Running` | lifecycle gate taken; `Retiring.Drained` awaited; the `Starting` instance registered as `Current` with its cancellation source, `WatchRequested` set, X's failure message cleared, all before the source is invoked | pump started outside the lock |
| `StopWatchingAsync(X)` | `WatchRequested` and the recovery ticket cleared, and `Current` moved to `Retiring` | nothing; no gate | teardown outside the lock; returns after that instance's `Drained`, bounded by the caller's token |
| `RescanAsync(X)` | the commit of X's new block (X's write gate, then `_stateLock`) | lifecycle gate held from entry to exit; `Current` retired and its `Drained` awaited before production | restart through an internal helper that assumes the gate is held, only if `WatchRequested` is still set |
| Recovery of X | the same commit | ticket revalidated after the lifecycle gate is taken | as rescan |
| Batch on X | the instance check, with X's write gate held | the pump read the batch | mutation under the write gate; `Changed` raised with no gate or lock held |
| Fault on X | the fault recorded on the instance while it is `Current` | the pump ended with the exception | checkpoint-loss check, `Recovering` published when recovery applies, then `WatchFaulted` raised |
| `DisposeAsync` | `_disposed` set and the disposal token cancelled | nothing | the disposal sequence in section 5 |

#### 2.6.4 Start, stop, pump, wait, rescan

- `StartWatchingAsync(X)`: `ArgumentException` for a letter not in the index. Takes X's
  lifecycle gate. `InvalidOperationException` when X has no MFT-backed block, or when X's
  block is unresumable: a cache-only open adopted it with a lost checkpoint (today's
  message, `FileIndex.WatchTargets.cs:61-69`) or its scans lost their catch-up
  (section 2.6.6); the message says which and points at `RescanAsync`. Succeeds without
  restarting when `Current` is `Running`. Clears X's recovery ticket (the consumer's start
  supersedes it) and retires a `Faulted` current instance. Awaits `Retiring.Drained`.
  Registers the `Starting` instance (section 2.6.3). Calls `source.StartAsync(target,
  token)` outside every lock, with the instance's start token linked to the caller's.
  Then, under `_stateLock`, publishes the handle and starts the pump only if the instance
  is still `Current` and `Starting`. Otherwise a stop or disposal retired it while the
  source ran: the start disposes the returned handle (an unpublished handle has no pump,
  so the start path is its only disposer), cancels the instance's catch-up slot,
  completes its `Drained` and throws `OperationCanceledException`. A throw from
  `StartAsync` records X's failure message, faults the instance's slot, clears `Current`,
  completes `Drained` and propagates. Today a start's session and cancellation source
  exist before readiness, and an abandoned start cancels and awaits that session
  (`FileIndex.WatchStart.cs:63-79`, `:106-137`); the instance gives each drive the same
  guarantee.
- `StopWatchingAsync(X)`: takes no gate, so it never waits for a rescan. Under
  `_stateLock` it clears `WatchRequested` and the recovery ticket, moves `Current` to
  `Retiring` (cancelling a `Starting` instance's start token, or a running pump's stop
  source), and takes the instance's outstanding fault. Outside the lock it cancels the
  instance's catch-up slot and awaits `Drained` bounded by its own token; it never
  disposes the handle, which belongs to the pump's exit path; a stop whose token fires first throws
  `OperationCanceledException` while teardown continues, and a later start awaits it
  through `Retiring`. It rethrows the taken fault once. X counts as watching for stop when
  `WatchRequested` is set or an instance exists, so a stop during a rescan clears
  `WatchRequested` and succeeds.
- Pump: reads `ReadAsync`. A `JournalBatch` goes to `ApplyJournalEntriesCore` under the
  scoping rule (section 2.6.2), then `Changed` is raised with no write gate and no
  `_stateLock` held (section 5); a throwing
  handler raises `WatchFaulted(Subscriber, X)` once per instance and the drive keeps
  watching. `DriveCaughtUp` completes the instance's slot if the instance is still
  `Current`. Ends: the stop source is a stop; `DriveWatchFaultException` is `Drive`; an
  apply throw is `Apply`; any other exception, or a normal end before a stop, is
  `Channel`. A fault is recorded only if the instance is still `Current`: the failure
  message, the outstanding fault, the slot faulted, state `Faulted`, then
  `RecordCheckpointLossForFaultedDrive(X)` (`FileIndex.WatchCheckpointLoss.cs:34-72`, whose
  block-identity check at `:65-69` stays), then recovery (section 2.6.5) for `Drive` and
  `Apply`, then `WatchFaulted`. A `Channel` fault is loud: `WatchFailureMessage` set,
  catch-up `Faulted`, no recovery. The pump's exit path is the only disposer of a
  published handle: on every exit (stop, fault, lost channel, disposal) it disposes the
  handle exactly once, then completes `Drained`. Stop and disposal only cancel the pump's
  stop source and wait.
- `WaitForCatchUpAsync(X)` waits on the current instance's slot: it completes on
  catch-up, faults with the instance's fault, and is cancelled by stop, rescan (the
  instance retires) or disposal. A wait issued while X is `Faulted` or `Recovering` faults
  at once with X's fault; a consumer that wants the recovered watch waits again once X
  reads `CatchingUp`.
- `RescanAsync(X)` takes X's lifecycle gate and holds it to the end: through retirement,
  production, commit or rollback, and restart. Holding it across production is what
  serializes the canonical file's rename-aside, the scan into the canonical path and the
  restore on failure, which run outside any other gate (`FileIndex.Rescan.cs:183-189`,
  `:231-235`); today `_rescanGate` does this index-wide (`:89-135`). Production holds
  neither X's write gate nor `_stateLock`. Steps: clear X's recovery ticket (a manual
  rescan supersedes it); retire `Current` and await its `Drained`; produce, with the
  lost catch-up retries of section 2.6.6; commit under X's write gate, then `_stateLock`
  (section 2.6.7); then, if `WatchRequested` is still set, start X's watch through an
  internal helper that assumes the gate is held and never reacquires it. The rule for a
  failed scan is today's: a drive that was healthy resumes from its old cursor; a drive
  that was faulted or unresumable stays faulted (`FileIndex.Rescan.cs:145-158`). A rescan
  whose producer returns no block throws `InvalidOperationException` carrying
  `DriveStatus.MftProducerFailureMessage`. The rescan's token is linked to the disposal
  token.

#### 2.6.5 Recovery

When the pump of X's `Current` instance ends with a `Drive` or `Apply` fault, the index
records the fault and runs the checkpoint-loss check (section 2.6.4), then, under
`_stateLock`, stores a `RecoveryTicket(FailedInstance, FailedBlock)` on X's runtime with a
cancellation source linked to disposal and publishes `Recovering`. Only then does it raise
`WatchFaulted(Drive or Apply, X)`, so a handler that reads `Drives` sees `Recovering`.
The ticket's task then runs on the thread pool:

- It takes X's lifecycle gate, then revalidates under `_stateLock`: the ticket is still
  X's `Recovery`, `Current` is still `FailedInstance` and `Faulted`, `FailedBlock` is still
  X's published block, and `WatchRequested` is set. On any mismatch it releases the gate
  and ends without scanning. The existing checkpoint-loss path guards a delayed result the
  same way, by block identity (`FileIndex.WatchCheckpointLoss.cs:65-69`).
- A manual `RescanAsync(X)` or `StartWatchingAsync(X)` supersedes the ticket by clearing it;
  `StopWatchingAsync(X)` clears it and `WatchRequested`; disposal cancels it and awaits its
  task. A recovery already past revalidation when stop runs commits its block and does not
  restart the watch.
- A valid recovery runs the rescan body of section 2.6.4 with the gate already held: retire
  `FailedInstance`, produce, commit, restart. It clears the ticket when it ends. Every
  fault it records is checked against its ticket, so an obsolete recovery's failure is
  dropped rather than marking a newer watch faulted.
- If the recovery scan fails, or X faults again before its restarted instance first
  reaches `CaughtUp`, the index raises `WatchFaulted(Recovery, X)` and leaves X `Faulted`
  until the consumer calls `RescanAsync(X)` or `StartWatchingAsync(X)`. A lost catch-up
  inside a recovery is handled by section 2.6.6 and is not a recovery failure until the
  limit there is reached.
- A recovery rescan keeps the drive's checkpoint-loss report: a `LiveWatch` loss explains
  why the block in place came from a rescan, the same way a `DriveOpening` loss explains
  an open's cold scan. A consumer rescan whose first attempt succeeds clears the report, as
  today (`FileIndex.Rescan.cs:207`).
- Recovery requests no thread count; the broker's budget rebalances it with every other
  scan (section 2.3), so several drives faulting together recover concurrently.

#### 2.6.6 Lost catch-up (ruling 7)

A scan whose catch-up after `ScanReady` fails is a lost catch-up. It is a fault naming
that drive, it recovers by rescanning that drive, and it never resumes from the journal's
current position.

What it means for the index. `FileIndex` adopts a produced block with the cursor armed
before the scan, so its watch replays everything that changed during the scan
(`MFTLib/Index/MftBlockProducer.cs:38-50`); it does not use the catch-up entries, and the
broker producer ignores today's `Warning` (`BrokerMftBlockProducer.cs:67-89`). A lost
catch-up therefore means the block's own cursor was already outside the journal when the
scan finished: a watch started from it fails at once.

- Signal. The host proves the loss and writes `CatchUpLost` (section 2.3); `ScanDriveAsync`
  returns the block with `CatchUpLoss` set; `BrokerMftBlockProducer` copies it to
  `MftBlockProduceResult.CatchUpLoss`. A producer sets it only with a loss the journal
  proved. A catch-up failure the journal does not prove never reaches this section: it is
  a failed scan with no block, so it leaves the count unchanged, produces no `ScanCatchUp`
  report and suggests no journal size.
- Publication. A block whose catch-up was lost is a complete scan, so it is committed like
  any other (section 2.6.7), and it is marked unresumable: `StartWatchingAsync(X)` refuses
  it (section 2.6.4). The publish step records `CatchUpLoss` as X's `CheckpointLoss`. Its
  `SizeThatWouldHaveRetained` is the existing `JournalSizeArithmetic` result for the span
  from the armed cursor to the journal's tip, which is the size a journal needs to outlast
  a scan of this drive; it is null when the journal was recreated rather than trimmed.
- The count. `DriveRuntime.ConsecutiveLostCatchUps`, under `_stateLock`, is created at
  open and kept for the index's lifetime, so it spans every scan operation and every
  watch instance of X. The publish step sets it: plus one for a lost catch-up, zero for a
  scan whose catch-up succeeded. A scan that produced no block leaves it unchanged.
  `DriveStatus.ConsecutiveLostCatchUps` reports it.
- Recovery. Every scan operation of X (the open's settle, `RescanAsync(X)`, a batched
  rescan's per-drive operation, a recovery) that publishes a lost catch-up raises
  `WatchFaulted(CatchUpLost, X)` with a `JournalCatchUpLostException`, publishing
  `Recovering` first when `WatchRequested` is set. The event is raised with no write gate
  and no `_stateLock` held; the operation still holds X's lifecycle gate, which section 5
  allows. It then rescans X again at once, still holding that gate, while the count is below `FileIndex.LostCatchUpRecoveryLimit`
  (3). During `OpenAsync` no handler can be subscribed yet, so the drive's status is the
  record. A recovery re-checks `WatchRequested` before each further attempt and stops when
  it has been cleared.
- The limit. When a lost catch-up brings the count to 3 or more, the operation stops
  rescanning.
  The drive keeps its last block (queryable, unresumable), `WatchFailureMessage` and a
  faulted catch-up state carry the `JournalCatchUpLostException` message, and that
  exception's `RecoveryStopped` is true. Its message names the drive, the three losses,
  and, when the `ScanCatchUp` report carries a `SizeThatWouldHaveRetained` (a trimmed
  journal, not a recreated one), that size as the journal size to grow to (through `BrokerProcess.GrowUsnJournalAsync`); consumers read the same
  number from `DriveStatus.CheckpointLoss`. `RescanAsync(X)` throws the exception (the
  batched form reports it as `Failed`); `OpenAsync` does not throw for it, and X reads
  `Ready` with the report attached.
- Manual rescan. A consumer's `RescanAsync(X)` is always honored, whatever the count. With
  the count at or past the limit it makes exactly one attempt: success resets the count,
  clears the unresumable mark and restarts a requested watch; another loss raises the
  count and throws, with no automatic retry. Below the limit it retries as above,
  continuing from the stored count. Growing the journal is what makes the next manual
  rescan likely to succeed.
- Reports. A commit made after a loss (a retry, or a recovery) keeps the loss report that
  caused it, as recovery keeps a `LiveWatch` report (section 2.6.5); a `ScanCatchUp` report
  replaces an older report on the drive as the newer fact.

#### 2.6.7 Publication, blockless adoption and concurrent open

- Nothing unpublished is keyed by an ordinal. Today a blockless drive's tentative ordinal
  is computed before its scan (`FileIndex.Rescan.cs:252`) and keys the producer failure
  message that `ProduceDriveBlockAsync` writes (`FileIndex.Scanning.cs:173-185`) and
  `RecordBlocklessProducerFailure` consumes (`FileIndex.Rescan.cs:256-272`), and the open
  path writes discarded-block and checkpoint-loss state by ordinal before adoption
  (`FileIndex.Scanning.cs:31-37`, `:297-307`). Two concurrent blockless scans could then
  overwrite or erase each other's message even with distinct final ordinals. Instead each
  scan operation builds a local pending result: the produced block, its access-denied
  count, producer failure message, discarded-block reason, checkpoint loss, cache-slot
  state, unresumable mark, and whether its catch-up was lost.
- The publish step (X's write gate, then `_stateLock`) assigns the ordinal
  (`_driveBlocks.Count` for a blockless drive, the existing ordinal otherwise), builds the
  `DriveBlock` (today built before the scan, `FileIndex.Scanning.cs:233`), writes every
  field of the pending result into the ordinal-keyed state, updates
  `ConsecutiveLostCatchUps`, and publishes the snapshot, all in one step. A failed
  blockless scan writes its message onto the drive's blockless status, which is keyed by
  letter.
- Concurrent open (ruling 5). `OpenAsync` settles every configured drive concurrently
  instead of one after another (`FileIndex.cs:148-162`). Each drive's settle (warm start,
  or scan with the retries of section 2.6.6) produces a pending result and adopts it under
  `_stateLock` as it finishes. MFT scans are bounded by the broker's budget (section 2.3).
  Enumeration walks run one thread each (`FileIndex.Scanning.cs:157-163`) and are admitted
  through a process-wide limit of one walk per processor, so ruling 4's limit holds
  wherever drives are scanned. `OpenAsync` waits for every drive to settle before it
  publishes the first snapshot or throws: if any settle throws (cancellation included), it
  waits for the rest, releases every block they produced, and throws the first failure.
- `FileIndexOptions.OpenProgress` reports each drive as it settles, from the thread that
  settled it, with `IndexDriveOpened.SettledCount` (this drive was the n-th to settle) in
  place of the configured position. `FileIndexOptions.Progress` receives scan progress from
  several drives' scans at once. `Drives` and `DriveStatus` keep `FileIndexOptions.Drives`
  order; block ordinals follow settle order.

#### 2.6.8 Callback reentrancy

`Changed` and `WatchFaulted` handlers run on a drive's pump or on the scan operation that
raised the fault (section 6), as they run inline on today's pump
(`FileIndex.Watch.cs:321-327`, `FileIndex.WatchPump.cs:316-320`). A handler that
synchronously waits for an operation that drains a pump deadlocks whenever that pump is
blocked in a handler too. Its own drive's pump is the obvious case, but delivery is
concurrent across drives, so a cycle needs no self-call: X's `Changed` handler blocks on
`StopWatchingAsync(Y)` while Y's handler blocks on `StopWatchingAsync(X)`, each stop
awaits the other pump's `Drained`, and neither handler can return. The rule is therefore
per index, not per drive.

- Rejected calls. From inside any callback of this index, whichever drive it names:
  `StopWatchingAsync`, `RescanAsync`, `StartWatchingAsync` (a start takes a lifecycle gate,
  and may await a previous instance's `Drained`; a gate can be held by a rescan that is
  itself awaiting a pump), `DisposeAsync`, every batched form of these, and an unsettled
  `WaitForCatchUpAsync` for any drive (two handlers waiting on each other's catch-up form
  the same cycle). Each checks synchronously at entry, before its first await, and fails
  with `InvalidOperationException`: "FileIndex.{operation} was called from inside a Changed
  or WatchFaulted handler; it can wait for a watch pump that is blocked in a handler. Queue
  the call to run after the handler returns, for example with Task.Run." The task it
  returns is already faulted, so a handler that blocks on it gets the exception at once.
- Allowed calls. Queries (`Find`, `FindByName`, `Search`, `Enumerate`, `Largest`,
  `DuplicateNames`, `Root`, `FileEntry` members), `Drives` and `DriveStatus`,
  `QueryUsnJournalSettings`, a `WaitForCatchUpAsync` that is already settled, and calls
  outside this index such as `BrokerProcess.GrowUsnJournalAsync`. None of these takes a
  lifecycle gate or waits for a pump.
- Detection. Around every handler invocation the raiser sets an `AsyncLocal` delivery
  marker that names this index and holds an `Active` flag, and clears the flag when the
  invocation returns. The marker flows with the handler's execution context, so it holds
  across an await inside the handler and in any continuation or `Task.Run` the handler
  starts: a handler that blocks on an async helper which awaits and then calls
  `StopWatchingAsync` is still rejected, because the pump is still blocked on that
  handler. Because the flag is cleared when the invocation returns, work the handler queued
  that runs after it returned sees an inactive marker and is allowed: queuing is the
  remedy the exception names. Work that runs while the handler is still blocked sees the
  active marker and is rejected, since blocking on it would deadlock.
- Asynchronous completion. A handler can settle an index waiter directly (a fault it
  raises completes catch-up waiters and `Drained`) or indirectly (it cancels a token that
  a pending `WaitForCatchUpAsync`, stop or rescan observes). Every task completion source
  whose completion, fault or cancellation can be triggered that way is created with
  `TaskCreationOptions.RunContinuationsAsynchronously`: `Drained`, the catch-up slot's
  `Waiter` and `FaultWaiter`, the batched catch-up wait, the recovery ticket's
  completion, and the snapshot borrow drain. A caller's token and the disposal token
  reach a public wait by cancelling such a source from a token registration, not through
  `Task.WaitAsync`, which does not promise asynchronous continuations; today's
  `WaitForCatchUpAsync` delivers cancellation through `Task.WaitAsync`
  (`FileIndex.WatchCatchUp.cs:374`, `:388`). Without this, an inline continuation would
  run the awaiter's code on the handler's stack, which is the pump's thread. Two things
  then go wrong. A continuation that does not flow its own execution context runs under
  the handler's, sees the active marker, and an innocent awaiter elsewhere in the
  application gets the callback exception. An `await` continuation restores its own
  context, so it sees no marker; a lifecycle call it then blocks on is allowed, runs on
  the pump's own thread, and deadlocks undetected. Queued continuations avoid both. The
  option promises one thing only: a continuation never runs inline on the stack that
  settles the source. It runs as separate work in the awaiter's own context (or the
  default one), where the marker is not active. It promises nothing about ordering: a
  continuation may run on another thread while the handler is still running, before or
  after it returns, and on no particular thread. Nothing in the index relies on more.

### 2.7 Status and fault reporting

- Every `WatchFault` names a drive. The kinds are `Subscriber` (a `Changed` handler
  threw; the drive keeps watching; announced once per drive per watch instance), `Drive`
  and `Apply` (recovery started), `CatchUpLost` (a scan of the drive lost its catch-up;
  the exception is a `JournalCatchUpLostException`, and its `RecoveryStopped` says whether
  the limit was reached), `Channel` (loud, no recovery), `Recovery` (recovery did not
  restore the drive).
- `DriveStatus` keeps its fields and gains `ConsecutiveLostCatchUps`. `WatchCatchUp` gains
  `Recovering`. `JournalCheckpointLossDetection` gains `ScanCatchUp` beside
  `DriveOpening` and `LiveWatch`: found when a scan's catch-up failed and the live
  journal proved it could not read from the
  cursor armed before the scan; `CheckpointUsn` is that armed cursor.
- `WaitForCatchUpAsync(X)` keeps today's contract per drive (section 2.6.4). The batched
  forms return one result per drive.
- Faults are reported through `WatchFaulted` and `DriveStatus` when they happen. The
  single-drive `StopWatchingAsync(X)` also rethrows X's outstanding fault once; the
  batched stop returns it as that drive's result. `DisposeAsync` never throws a fault.

### 2.8 Diagnostics

One log per process, as today (`BrokerDiagnostics.cs:47-67`). Every line carries
`[role:pid:channel]`, where channel is `control` or the drive letter plus the channel's
sequence number. `LogFrame` takes the channel tag. The log filter stays per drive
(`BrokerDiagnostics.CreateLogFilter`, used at `JournalBrokerHost.cs:84`).

No caller waits for the disk. Today `LogFrame` runs inside the frame-write path
(`JournalBrokerHost.cs:190-193`) and `Log` appends synchronously with
`File.AppendAllText` (`BrokerDiagnostics.cs:133-137`), so a slow log write stalls the
frame path of the channel that logged, and serializing appends from several channels
behind one lock would stall every channel. Instead `Log` and `LogFrame` format the line (timestamp
taken at the call) and enqueue it without blocking into one process-wide bounded buffer
(8192 records). One background writer drains it and appends. When the buffer is full the
newest record is dropped and counted; the writer's next append is first preceded by
`[role:pid:diagnostics]  {count} records dropped: buffer full`. A failing append is counted
the same way and never throws.

### 2.9 Test seams

- `JournalBrokerHost`'s constructor gains `int? processorCount` and
  `TimeProvider? timeProvider` (defaults `Environment.ProcessorCount` and
  `TimeProvider.System`); today it takes neither (`JournalBrokerHost.cs:26-39`). The budget,
  heartbeat, processing limit, connect and first-request limits and grace period read them.
- `BrokerProcess` takes a `TimeProvider` through an internal constructor that
  `BrokerTestHarness` reaches (stall limit, reply and write timeouts). The client takes no
  processor count, because no client code computes a thread share (ruling 6).
- Operation state. A pending `MoveNextAsync` cannot tell a quiet volume from a wedged
  step, and today's source delegates cannot report either: `JournalBatchSource` only
  yields batches and `MftRecordBatchSource` takes only progress and cancellation. Every
  host source therefore receives an `IBrokerOperationReporter` (`WaitingOnVolume()`,
  `Processing(string step)`). Production sources report `WaitingOnVolume` around the
  volume open and before each native journal read, and `Processing` per batch and per
  native progress callback; fakes call it directly. The scan source also receives the
  scan's `ParseThreadAllowance`, so a fake observes the value each chunk would read.
- `BrokerTestHarness.StartInProcess` has an overload taking `BrokerTestHarnessOptions`: the
  client's `TimeProvider`, a per-pipe connection failure, and per-pipe held writes.
- `JournalIsolation.OverrideJournalWindow` (the existing consumer seam) drives the
  `ScanCatchUp` check as it drives the other two, and the enumeration walk limit has an
  internal size seam.

## 3. Public API for 0.3.0

```csharp
namespace MFTLib;

public sealed class BrokerProcess : IAsyncDisposable
{
    public static readonly TimeSpan DefaultConnectTimeout;
    [SupportedOSPlatform("windows")]
    public static Task<BrokerProcess> LaunchAsync(Func<string, bool> launchBroker, CancellationToken cancellationToken);
    [SupportedOSPlatform("windows")]
    public static Task<BrokerProcess> LaunchAsync(Func<string, bool> launchBroker, TimeSpan connectTimeout, CancellationToken cancellationToken);
    public bool HasEnded { get; }
    public event Action<string>? Ended;
    public Task<NtfsVolumeInformation> QueryVolumeAsync(char driveLetter, CancellationToken cancellationToken);
    public Task<UsnJournalSettings> GrowUsnJournalAsync(char driveLetter, long maximumSize, long allocationDelta, CancellationToken cancellationToken);
    public Task<BrokerDriveScanResult> ScanDriveAsync(char driveLetter, BlockScanTarget target, BrokerScanOptions options, CancellationToken cancellationToken);
    public ValueTask DisposeAsync();
}

// AdvancedCursor is null and CatchUpEntries empty when CatchUpLoss is set. CatchUpLoss is
// the loss the host proved against the live journal (section 2.3).
public sealed record BrokerDriveScanResult(char DriveLetter, UsnJournalCursor ArmedCursor,
    UsnJournalCursor? AdvancedCursor, IReadOnlyList<UsnJournalEntry> CatchUpEntries,
    JournalCheckpointLoss? CatchUpLoss, BlockScanOutcome Block);

public sealed record BrokerScanOptions
{
    public BrokerScanProfile Profile { get; init; } = BrokerScanProfile.Full;
    public IReadOnlyCollection<string>? KeepFileNames { get; init; }
    public IProgress<BrokerScanProgress>? Progress { get; init; }
}

public sealed class BrokerChannelLostException : IOException
{
    public BrokerChannelLostException(char? driveLetter, string message, Exception? innerException = null);
    public char? DriveLetter { get; }
}

public delegate Task<Stream> BrokerChannelConnector(string pipeName, CancellationToken cancellationToken);
public delegate (string SectionName, BlockFile Block, IDisposable Lifetime) BrokerBlockSectionFactory(
    char driveLetter, BlockFileCreateOptions options);

// How a host source tells the watchdog what it is doing (section 2.9).
public interface IBrokerOperationReporter
{
    void WaitingOnVolume();
    void Processing(string step);
}

public delegate IEnumerable<IReadOnlyList<MftRecord>> MftRecordBatchSource(string driveLetter,
    ParseThreadAllowance parseThreads, IBrokerOperationReporter operation,
    IProgress<BlockWriteProgress>? progress, CancellationToken cancellationToken);
public delegate IAsyncEnumerable<(UsnJournalEntry[] Entries, UsnJournalCursor Cursor)> JournalBatchSource(
    string driveLetter, UsnJournalCursor since, IBrokerOperationReporter operation, CancellationToken cancellationToken);

public sealed partial class JournalBrokerHost
{
    public JournalBrokerHost(UsnJournalCursorQuery queryCursor, MftRecordBatchSource scanDrive,
        UsnJournalCatchUpSource readJournal, JournalBatchSource? watchDrive = null,
        NtfsVolumeInformationQuery? queryVolumeInfo = null, GrowUsnJournalQuery? growUsnJournal = null,
        int? processorCount = null, TimeProvider? timeProvider = null);
    public Task ServeAsync(Stream control, BrokerChannelConnector connectChannel,
        IBlockSectionWriter? blockSectionWriter, CancellationToken cancellationToken);
}

// The parse-thread count a running parse reads at every chunk and before path resolution.
public sealed class ParseThreadAllowance
{
    public ParseThreadAllowance(int count);   // count >= 1
    public int Count { get; set; }            // below 1 throws ArgumentOutOfRangeException
}

public sealed partial class MftVolume
{
    // parseThreads null: every processor. A cancelled parse stops natively (section 2.3).
    public MftResult StreamRecords(string? filter, MatchFlags matchFlags, IProgress<MftScanProgress>? progress,
        ParseThreadAllowance? parseThreads, CancellationToken cancellationToken);
    public IEnumerable<MftRecord[]> ReadRecordBatches(bool resolvePaths, int batchSize,
        IProgress<MftScanProgress>? progress, ParseThreadAllowance? parseThreads, CancellationToken cancellationToken);
}

public interface IElevatedEntryRunner
{
    void RunBroker(string? controlPipeName);
}

public sealed class BrokerMftBlockProducer
{
    public BrokerMftBlockProducer(Func<CancellationToken, Task<BrokerProcess>> connectAsync,
        BrokerScanOptions? scanOptions = null, Action<BrokerDriveScanResult>? scanCompleted = null);
    public MftBlockProducer CreateProducer();
    public IIndexWatchSource CreateWatchSource();
}

public sealed class BrokerIndexWatchSource : IIndexWatchSource
{
    public BrokerIndexWatchSource(Func<CancellationToken, Task<BrokerProcess>> connectAsync);
    public Task<IIndexDriveWatch> StartAsync(IndexWatchTarget target, CancellationToken cancellationToken);
}
```

```csharp
namespace MFTLib.Index;

public interface IIndexWatchSource
{
    Task<IIndexDriveWatch> StartAsync(IndexWatchTarget target, CancellationToken cancellationToken);
}

public interface IIndexDriveWatch : IAsyncDisposable
{
    char DriveLetter { get; }
    IAsyncEnumerable<WatchStreamItem> ReadAsync(CancellationToken cancellationToken); // cancelling ends it promptly
    // DisposeAsync: called exactly once per handle by the index; need not be idempotent.
}

public sealed record JournalBatch(IReadOnlyList<UsnJournalEntry> Entries, ulong JournalId, long NextUsn) : WatchStreamItem;
public sealed record DriveCaughtUp : WatchStreamItem;

public sealed record MftBlockProduceResult(BlockFile Block, ulong JournalId, long NextUsn,
    int SkippedRecordCount, bool CompactionNeeded)
{
    public JournalCheckpointLoss? CatchUpLoss { get; init; }  // a proven loss only; section 2.6.6
}

public sealed class DriveWatchFaultException : Exception
{
    public DriveWatchFaultException(char driveLetter, string message, Exception? innerException = null);
    public char DriveLetter { get; }
}

public sealed class JournalCatchUpLostException : Exception
{
    public JournalCatchUpLostException(char driveLetter, int consecutiveLostCatchUps, bool recoveryStopped,
        JournalCheckpointLoss checkpointLoss, string message);
    public char DriveLetter { get; }
    public int ConsecutiveLostCatchUps { get; }
    public bool RecoveryStopped { get; }
    public JournalCheckpointLoss CheckpointLoss { get; }
}

public enum WatchFaultKind { Subscriber, Drive, Apply, CatchUpLost, Channel, Recovery }
public sealed record WatchFault(WatchFaultKind Kind, char DriveLetter, Exception Exception);

public enum WatchCatchUpState { NotStarted, CatchingUp, CaughtUp, Recovering, Faulted }
public enum JournalCheckpointLossDetection { DriveOpening, LiveWatch, ScanCatchUp }

public sealed record DriveStatus
{
    // Existing members unchanged, plus:
    public int ConsecutiveLostCatchUps { get; init; }
}

public sealed record IndexDriveOpened
{
    public required char DriveLetter { get; init; }
    public required int SettledCount { get; init; }   // this drive was the n-th to settle, 1-based
    public required int Total { get; init; }
    public required BlockSource BlockSource { get; init; }
    public required DriveState State { get; init; }
}

public enum DriveOperationOutcome { Succeeded, Failed, NotApplicable }
public sealed record DriveOperationResult(char DriveLetter, DriveOperationOutcome Outcome, Exception? Failure);

public sealed partial class FileIndex
{
    public const int LostCatchUpRecoveryLimit = 3;

    public Task StartWatchingAsync(char driveLetter, CancellationToken cancellationToken);
    public Task<IReadOnlyList<DriveOperationResult>> StartWatchingAsync(IReadOnlyList<char> driveLetters, CancellationToken cancellationToken);
    public Task<IReadOnlyList<DriveOperationResult>> StartWatchingAsync(CancellationToken cancellationToken);

    public Task StopWatchingAsync(char driveLetter, CancellationToken cancellationToken);
    public Task<IReadOnlyList<DriveOperationResult>> StopWatchingAsync(IReadOnlyList<char> driveLetters, CancellationToken cancellationToken);
    public Task<IReadOnlyList<DriveOperationResult>> StopWatchingAsync(CancellationToken cancellationToken);

    public Task RescanAsync(char driveLetter, CancellationToken cancellationToken);
    public Task<IReadOnlyList<DriveOperationResult>> RescanAsync(IReadOnlyList<char> driveLetters, CancellationToken cancellationToken);
    public Task<IReadOnlyList<DriveOperationResult>> RescanAsync(CancellationToken cancellationToken);

    public Task WaitForCatchUpAsync(char driveLetter, CancellationToken cancellationToken);
    public Task<IReadOnlyList<DriveOperationResult>> WaitForCatchUpAsync(IReadOnlyList<char> driveLetters, CancellationToken cancellationToken);
    public Task<IReadOnlyList<DriveOperationResult>> WaitForCatchUpAsync(CancellationToken cancellationToken);

    public event Action<FileChange>? Changed;      // delivery contract: section 6
    public event Action<WatchFault>? WatchFaulted; // same delivery contract
}
```

```csharp
namespace MFTLibTestExtensions;

public static class BrokerTestHarness
{
    // Runs host.ServeAsync in process over in-memory control and channel streams and
    // returns a BrokerProcess connected to it. Disposing the process ends the host. The
    // host's own clock and processor count go through the JournalBrokerHost constructor.
    public static BrokerProcess StartInProcess(JournalBrokerHost host,
        IBlockSectionWriter blockSectionWriter, BrokerBlockSectionFactory createBlockSection);
    public static BrokerProcess StartInProcess(JournalBrokerHost host,
        IBlockSectionWriter blockSectionWriter, BrokerBlockSectionFactory createBlockSection,
        BrokerTestHarnessOptions options);
}

public sealed record BrokerTestHarnessOptions
{
    public TimeProvider TimeProvider { get; init; } = TimeProvider.System; // client side
    public Func<string, Exception?>? FailConnection { get; init; }         // by pipe name: the host's connect throws this
    public Func<string, Task>? HoldWrites { get; init; }                   // by pipe name: host writes wait on the returned task
}
```

Batched-call contract, shared by every list and no-list form:

- One result per requested drive, in the order given; the no-list form covers every
  drive in `FileIndexOptions.Drives` order.
- The per-drive operations run concurrently. The call returns after every one of them
  has settled, so nothing it started outlives it.
- It throws only for caller errors: a null list, a duplicate letter, a letter not in the
  index (`ArgumentException`, before anything starts), a disposed index
  (`ObjectDisposedException`), or cancellation (`OperationCanceledException`, after every
  per-drive operation has settled).
- `Failed` carries the exception the single-drive form would have thrown.
  `NotApplicable` means the operation does not apply to that drive: no MFT-backed block
  for start, not watching for stop and catch-up. For stop, `Failed` carries the fault
  that had ended the drive's watch.

Single-drive forms throw what the batched form would report as `Failed`, and throw
`InvalidOperationException` where the batched form reports `NotApplicable`.

## 4. Deleted, shrunk, kept

| Mechanism | Files | Verdict | Reason |
|---|---|---|---|
| Shared pipe and `JournalBrokerClient` | `Broker/Client/JournalBrokerClient*.cs` (12 files) | Deleted | Replaced by `BrokerProcess` and per-operation channels |
| Arm-ordering gate | `JournalBrokerClient.cs:43-48` and every `WaitAsync` on it | Deleted | Nothing shares a reader |
| Control exchange routed through the demux | `JournalBrokerClient.ControlExchange.cs` | Deleted | Control replies have their own pipe and request ids |
| Live demux and per-drive live channels | `JournalBrokerClient.LiveWatchDemux.cs` | Deleted | Each watch pipe is read by its own consumer |
| Arm epochs | `BrokerFrame.cs:27-31`; host echo `JournalBrokerHost.cs:58,93,103,128`; `WatchDriveRequest.ArmEpoch` `JournalBrokerHost.Scan.cs:297-301` | Deleted | An arm is a pipe; a superseded arm's frames sit on a closed pipe nobody reads |
| Source arm generations, stop and awaiting-reader sets | `BrokerIndexWatchSource.cs:17-30` | Deleted | One handle per drive watch |
| Stream claim and abandoned-start teardown | `BrokerIndexWatchSource.cs:32-36`, `BrokerIndexWatchSource.AbandonedStart.cs` | Deleted | Nothing to claim; a cancelled start closes its own pipe |
| `BrokerIndexWatchSource.PerDrive.cs` | whole file | Deleted | Arm and disarm are start and dispose |
| `StartWatch` drive lists, `DisarmDrive`, `EndWatch`, `EndWatchAck`, `Shutdown`, plural `QueryVolumes` | `BrokerFrame.cs`, `BrokerProtocol*.cs`, `JournalBrokerHost.Session.cs:116-136` | Deleted | Closing a pipe is the stop; control EOF is the shutdown |
| `oneShot` / `--once` | `JournalBrokerHost.Session.cs:94-97`, `ElevatedEntryPoint.cs:41`, `IElevatedEntryRunner.cs:17` | Deleted | No caller passes `--once` |
| `CreateBatchSource`, `LiveWatchItem`, `LiveWatchItemSource` | `JournalBrokerClient.LiveWatch.cs:358-397`, `LiveWatchItem.cs` | Deleted | The watch channel yields index items directly |
| `JournalBrokerScanSession`, `JournalBrokerSessionState` | `Broker/Client/JournalBrokerScanSession*.cs`, `JournalBrokerSessionState.cs` | Deleted | Lumped by design; no production caller |
| `ScanSessionTestHarness` | `MFTLibTestExtensions/ScanSessionTestHarness.cs` | Deleted | Replaced by `BrokerTestHarness` |
| `BrokerScanResult`, `NtfsVolumeQueryResult`, `BrokerScanOptions.BlockTargets` | `Broker/Client/` | Deleted | Scan and query are per drive |
| `IIndexWatchSource` merged stream, arm and disarm | `Index/IIndexWatchSource.cs` | Deleted | Replaced by `StartAsync` and `IIndexDriveWatch` |
| `ReadyOnFirstMoveWatchStream` | `Index/ReadyOnFirstMoveWatchStream.cs` | Deleted | `StartAsync` returns when ready |
| `WatchStreamNotRunningException` | `Index/WatchStreamNotRunningException.cs` | Deleted | No shared stream to be absent |
| `DriveWatchFailure` item | `Index/WatchStreamItem.cs:23` | Deleted | A drive failure is its own stream throwing |
| Drive letter on `JournalBatch`, `DriveCaughtUp` | `Index/JournalBatch.cs`, `Index/WatchStreamItem.cs:31` | Deleted | The stream is one drive's |
| `WatchSession`, `_watchSession`, session readiness | `Index/FileIndex.WatchSession.cs`, `FileIndex.WatchStart.cs` | Deleted | No session |
| Merged pump, source-ended-without-stop, whole-stream failure | `Index/FileIndex.WatchPump.cs:5-172` | Shrunk | Becomes the per-drive pump of section 2.6.4 |
| `WatchSessionFaults`, `_unreportedWatchFaults` | `Index/FileIndex.WatchFaults.cs` | Deleted | Faults are per drive; one outstanding slot per drive |
| Suspend, resume, reclaim, restart for rescan | `Index/FileIndex.Rescan.Watch.cs` | Deleted | Replaced by the rescan and recovery rows of the per-drive state machine (section 2.6) |
| Session-wide catch-up bookkeeping and `CatchUpCoordinator` | `Index/FileIndex.WatchCatchUp.cs:74-150`, `:278-351` | Shrunk | Per-drive slots; batched wait is a fan-out |
| `_rescanGate` | `Index/FileIndex.cs:48` | Shrunk | Per-drive lifecycle gate |
| `_swapGate` | `Index/FileIndex.cs:49` | Shrunk | Per-drive write gate; publishing moves into `_stateLock` |
| Null-drive `WatchFault`, `WatchFaultKind.Source` | `Index/WatchFault.cs` | Deleted | Every fault names a drive; kinds per section 2.7 |
| `JournalBrokerHost` scan, catch-up, progress, volume and grow handlers | `Broker/Host/*.cs` | Kept | Run per channel or per control request |
| `DescribeWatchFailure` | `JournalBrokerHost.cs:169-183` | Kept | Host-side wording for a lost cursor |
| Checkpoint-loss detection | `Index/FileIndex.WatchCheckpointLoss.cs`, `Index/JournalCheckpointCheck.cs` | Kept | Per-drive already |
| Block swap and rename-aside | `Index/FileIndex.Rescan.cs:160-240`, `FileIndex.ScanCleanup.cs` | Kept | Commit moves under the drive's write gate |
| `NamedBlockSection`, `IBlockSectionWriter`, section naming | `Index/NamedBlockSection.cs:113-116` | Kept | Section names already carry a GUID |
| `BrokerDiagnostics`, `BrokerDiagnosticsLogFilter` | `Broker/` | Kept | Lines gain a channel tag and go through a bounded background writer (section 2.8) |
| `Heartbeat` frame | `BrokerFrame.cs:12,115-118` | Kept | Defined today, never written; now written when idle |
| `Warning` frame, `BrokerScanResult.Warnings` | `BrokerFrame.cs:16`, `JournalBrokerHost.Scan.cs:228-238`, `JournalBrokerClient.ScanCollector.cs:80-82` | Deleted | A failed catch-up is a terminal `CatchUpLost` frame and a per-drive fault (section 2.6.6) |
| Parse-thread count read once per parse | `MFTLibNative/mft/mft.parse_core.cpp:405`, `core/test_hooks.cpp:36-43` | Shrunk | Read per chunk and before path resolution from the parse control block; `EffectiveThreadCount()` stays as the ceiling and the meaning of 0 |
| `StreamRecords` and `ReadRecordBatches` optional-parameter shapes | `Mft/MftVolume.cs:65-74`, `:102-105` | Replaced | Take a `ParseThreadAllowance?` and a `CancellationToken` (section 3) |
| `_cacheOnlyUnresumableCheckpointOrdinals` | `Index/FileIndex.cs:44` | Shrunk | An unresumable mark carried in the pending result, with its reason: a cache-only adoption or lost catch-ups |
| Sequential open loop, `AddDriveWithProgressAsync` | `Index/FileIndex.cs:148-162` | Deleted | Drives settle concurrently (section 2.6.7) |
| `IndexDriveOpened.Ordinal` | `Index/IndexDriveOpened.cs` | Deleted | `SettledCount` |
| Ordinal-keyed state written before publication | `Index/FileIndex.Scanning.cs:31-37`, `:173-185`, `:297-307`; `FileIndex.Rescan.cs:252-272` | Shrunk | Local pending result, published with its ordinal (section 2.6.7) |
| Synchronous diagnostics append | `Broker/BrokerDiagnostics.cs:133-137` | Shrunk | Bounded queue and one background writer (section 2.8) |

AGENTS.md paragraphs that become untrue and are rewritten from this spec:

- Architecture, MFTLib, "Checkpoint loss": everything from "The same check also runs
  mid-session, from `FileIndex.WatchPump`" onward that describes sessions, reclaim,
  restart, `_unreportedWatchFaults`, `ResumeDriveAfterRescanAsync` and PR 230/241 session
  handling. The journal-read and `DetectedDuring` rules stay, and `DetectedDuring` gains
  `ScanCatchUp` (section 2.6.6).
- "Watch start readiness": whole paragraph.
- "Watch and catch-up lifetime": whole paragraph.
- "VolumeBroker": whole bullet.
- "MFTLibTestExtensions" bullet in the project list.
- Test coverage, journal isolation: "may run on the watch-pump thread" becomes "may run
  on any drive's pump thread, concurrently for different drives".

Documentation rewritten: `docs/broker-integration.md` (entirely), `docs/broker-testing.md`,
`docs/broker-scan-tuning.md` (client and session sections), `docs/index-format.md`
around line 308, README sections "Keep the application non-elevated", "Build a live index
with FileIndex" and "Errors and recovery", and `CHANGELOG.md`.

## 5. Concurrency rules

Same-drive ordering between start, stop, rescan, recovery, batches, faults and disposal is
the state machine of section 2.6. This section adds the lock order and the disposal
sequence.

Lock order, outermost first:

1. Drive X's lifecycle gate (`SemaphoreSlim`, async; start, rescan, recovery, disposal).
2. Drive X's write gate (`SemaphoreSlim`; synchronous `Wait` in `ApplyJournalEntriesCore`,
   `WaitAsync` in a commit).
3. `_stateLock` (`Lock`, never held across an await).

- No code holds two drives' lifecycle gates or two drives' write gates, except disposal,
  which takes them in ascending drive-letter order.
- No code acquires a gate while holding `_stateLock`.
- A rescan or recovery holds X's lifecycle gate through production, which holds neither
  X's write gate nor `_stateLock`. Internal helpers a rescan calls to restart X's watch
  assume the lifecycle gate is held and never reacquire it.
- The publish step runs entirely under `_stateLock`: the commit of X's block and pending
  result, the new `Snapshot.Create(_driveBlocks)`, and the retirement of the previous
  snapshot. Today the retired-snapshot list is mutated outside `_stateLock`
  (`FileIndex.ScanCleanup.cs:276-277`) and relies on `_swapGate` for exclusion; that
  exclusion moves into `_stateLock`.
- A batch on X reads the current snapshot under X's write gate and mutates only X's
  block (section 7a). A publish for Y may retire that snapshot mid-batch. That is safe:
  X's `DriveBlock` is the same instance in both snapshots, the handles in the batch's
  `FileChange`s keep the retired snapshot and its blocks alive (`Snapshot.cs:5-11`), and
  X's own commit cannot run because it needs X's write gate.
- `Changed` and `WatchFaulted` are raised with no write gate and no `_stateLock` held.
  A pump holds no gate at all when it raises. The one raiser that holds a gate is a scan
  operation raising `WatchFaulted(CatchUpLost, X)` between attempts, which holds X's
  lifecycle gate (section 2.6.6). That is safe because every call that takes a lifecycle
  gate or waits for a pump is rejected inside a handler (section 2.6.8), so no handler can
  wait for that gate.

Disposal:

1. Under `_stateLock`, set `_disposed`; then cancel the disposal token. Every pending
   start's cancellation source, every rescan and every recovery is linked to it, so they
   stop at their next checkpoint, and a scan's cancellation reaches the native parse
   (section 2.3). Today disposal waits out an in-flight rescan instead
   (`FileIndex.Disposal.cs:9`, with `RescanAsync`'s token not linked,
   `FileIndex.Rescan.cs:89`).
2. For every drive, under `_stateLock`: clear `WatchRequested`, clear the recovery ticket,
   and move `Current` to `Retiring`, cancelling each instance's start token or pump stop
   source. Outside the lock: await every instance's `Drained` (each pump disposes its own
   handle, section 2.6.4) and every recovery task. No fault is thrown.
3. Take every lifecycle gate, then every write gate, in ascending drive-letter order.
4. Release retired and current snapshots, as `ReleaseSnapshotsForDisposalAsync` does.

## 6. `Changed` event delivery

Decision: concurrent delivery across drives, serialized within a drive. Each drive's pump
raises `Changed` for its own batches in journal order and never overlaps itself; two
drives' handlers may run at the same time. `WatchFaulted` follows the same rule. A
`WatchFaulted(CatchUpLost, X)` is raised by the scan operation instead of a pump, but that
operation runs only while X has no running pump (section 2.6.4), so X's delivery stays
serialized.

Justification: serializing in MFTLib needs one index-wide delivery lock held across
consumer code, which puts back the coupling ruling 2 removes, since one slow or blocked
handler would stop every drive's pump. Both consumers already serialize inside their
handlers, so concurrent delivery costs them nothing.

Because delivery is concurrent across drives, a handler must not synchronously wait for
any operation that drains a pump or takes a lifecycle gate, on any drive: two handlers
blocking on each other's drive would deadlock. Such a call fails at once with an exception
telling the handler to queue it (section 2.6.8). Neither consumer's handlers make such a
call directly (section 8 lists the events they raise onward, to audit).

Consumer audit:

| Handler | Evidence | Safe |
|---|---|---|
| file-wizard `JournalWatcher.OnChanged` | takes `_stateLock` for the whole body (`FileWizard/JournalWatcher.cs:145-170`); `_status` is a `ConcurrentDictionary` (`:19`) | Yes |
| file-wizard `JournalWatcher.OnWatchFaulted` | same lock (`:175-215`) | Yes |
| file-wizard `MainPage.OnJournalEvent` | called under the watcher lock; takes `_journalEventLock` (`FileWizardMaui/MainPage.LiveUpdates.cs:335-341`) | Yes |
| git-wizard `IndexVolumeChangeSource.OnChanged` | reads `_driveRealRoots`, written only during arming before the watch starts (`GitWizard/Watch/IndexVolumeChangeSource.Journals.cs:28`); takes `_startupBufferLock`; writes an unbounded channel created with multi-writer defaults (`IndexVolumeChangeSource.cs:11`, `:235-260`) | Yes |
| git-wizard `IndexVolumeChangeSource.OnWatchFaulted` | mutates `_usableDrives` under `_faultLock` (`IndexVolumeChangeSource.cs:286-297`), while `RecordStartupDriveFailure` mutates it under `_lifecycleLock` (`IndexVolumeChangeSource.Startup.cs:64-67`) and `ArmCoreAsync` reads it with no lock (`IndexVolumeChangeSource.cs:122-126`) | Race exists today between the pump and the arming continuation; concurrent fault delivery widens it. Fixed in migration (section 8) |

## 7. The three unverified points

**(a) Does a journal batch read only its own drive's block?** Yes.
`ApplyJournalEntriesCore` takes the current snapshot and hands `JournalMutator.Apply` one
drive ordinal (`FileIndex.Watch.cs:285-295`). Every snapshot read in the mutator goes
through that ordinal: `snapshot.GetDriveBlock(driveOrdinal)` for reason cycles
(`JournalMutator.cs:75`), `IndexNavigation.BuildPath` (`JournalMutator.cs:147,167,211,216,224,254`;
it reads only `snapshot.GetDriveBlock(driveOrdinal)`, `IndexNavigation.cs:40-58`), and
`FileEntry.Create(snapshot, driveOrdinal, rowIndex)`. All writes go through the
`BlockWriter` over that drive's block. The snapshot object is the only cross-drive thing it
touches, and only as a container.

**(b) Can the host run several scans concurrently once each has its own channel?** Yes,
with no process-global state in the way. Every host volume operation opens and disposes
its own `MftVolume` (`JournalBrokerHost.cs:307,313,334,348`;
`JournalBrokerHost.Sources.cs:9`); each scan has its own progress state and channel
(`JournalBrokerHost.Scan.cs:53-63`, `JournalBrokerHost.Progress.cs:9-16`); section names
carry a GUID (`NamedBlockSection.cs:113-116`). The native library's only mutable globals
are test hooks (`MFTLibNative/core/test_hooks.cpp:10-32`). What is serial today is the
single frame loop, which awaits each `ArmAndScan` inline (`JournalBrokerHost.Session.cs:87-92`),
and the client's arm-ordering gate around every scan (`JournalBrokerClient.ControlExchange.cs:49-54`).
Two process-wide items remain and neither blocks correctness: `BrokerDiagnostics` is
static and best-effort (section 2.8), and each native scan sizes its thread pool to
`std::thread::hardware_concurrency()` (`test_hooks.cpp:37`), so unlimited concurrent
scans would oversubscribe the CPU. Section 2.3 admits at most one scan per processor and
divides the processors' parse threads among running scans, rebalanced at every chunk.

**(c) Why is `_rescanGate` index-wide?** Commit `9db6dee` (2026-09-15, "Rescan: serialize
RescanAsync on _rescanGate and narrow _swapGate to block swap") split it out of
`_swapGate`, which until then was held across the whole rescan, scan included. Its message
states the goal as unblocking journal applies on other drives during a scan; serializing
rescans against each other was carried over from the old gate, not chosen. Code written
since then depends on it: the suspend, resume, reclaim and restart paths in
`FileIndex.Rescan.Watch.cs` assume one rescan at a time touches `_watchSession`, and
blockless adoption computes its ordinal before the scan (`FileIndex.Rescan.cs:252`). The
orchestrator's inference (the shared session a rescan must suspend and resume) is right
about what now depends on the gate, not about why it was created.

## 8. Consumer migration

Neither consumer handles today's scan `Warning`: both reach scans only through
`FileIndex` and `BrokerMftBlockProducer`, which ignores it (`BrokerMftBlockProducer.cs:67-89`).
A lost catch-up reaches them as a `WatchFault` of kind `CatchUpLost`, a
`JournalCheckpointLoss` detected during `ScanCatchUp`, and `DriveStatus.ConsecutiveLostCatchUps`;
the rows below that name these are that migration.

file-wizard, read at `f4f9940` (`main`, equal to `gitea/main`):

| File | Change |
|---|---|
| `FileWizard/FileWizardAPI.cs:10` | `BrokerProcess.LaunchAsync(BrokerLauncher.Launch, ct)` |
| `FileWizard/BrokerSessionHost.cs` | Hold `BrokerProcess`; `BrokerDied` becomes `Ended`; `ConnectAsync` returns `Task<BrokerProcess>` |
| `FileWizard/FileIndexHost.cs:107-108` | Producer takes the `BrokerProcess` factory; `CreateWatchSource` unchanged |
| `FileWizard/JournalWatcher.cs:178-187` | Drop the null-drive branch; `endsTheWatch` is true for `Channel`, `Recovery`, and `CatchUpLost` whose `JournalCatchUpLostException.RecoveryStopped` is true; `Drive`, `Apply`, and `CatchUpLost` without it mark the drive recovering, not failed |
| `FileWizard/JournalWatcher.cs:199-202` | Keep a `ScanCatchUp` loss as well as a `LiveWatch` loss: both explain the fault being handled |
| `FileWizardMaui.Logic/JournalHintLogic.cs:17-23` | A `ScanCatchUp` branch: a scan of this drive outlasted the journal N times in a row (`DriveStatus.ConsecutiveLostCatchUps`), with the size from `SizeThatWouldHaveRetained` and `CanGrow` unchanged |
| `FileWizardMaui.Logic/OpenProgressPresenter.cs:15`, `:19` | `Ordinal` becomes `SettledCount` |
| `FileWizardMaui/MainPage.xaml.cs:234`, `MainPage.Scanning.cs:181` | Open progress arrives from several threads in settle order; keep the report with the largest `SettledCount` rather than the last written |
| `FileWizardMaui/MainPage.Scanning.cs:170` | Scan progress arrives from several drives' scans at once; confirm `ScanDriveProgressTracker` (`:24`) accepts interleaved drives |
| `FileWizardMaui/MainPage.LiveUpdates.cs:79` | `StartWatchingAsync(ct)` returns results; `Failed` drives go to `_watchStartFailure` when every drive failed, otherwise to per-drive health via `ReconcileDriveHealth` |
| `FileWizardMaui/MainPage.LiveUpdates.cs:150` | Batched stop; results ignored (faults already reached `JournalWatcher`) |
| `FileWizardMaui/MainPage.Scanning.cs:56-57`, `:288-289` | Sequential loops become one `host.Index.RescanAsync(drives, ct)`; `Failed` results feed the scan error state, including a `JournalCatchUpLostException` whose message carries the journal size |
| `FileWizardMaui/SettingsPage.xaml.cs:127-129`, `file-wizard/JournalCommand.cs:143-146` | `client` is a `BrokerProcess`; call unchanged |
| `file-wizard/BrokerSmoke.cs:62,100,114,126,145` | Launch and results; `:129-130` loop becomes batched rescan |
| `file-wizard/CliRunner.Database.cs:105,124` | Results |
| `FileWizardTests/BrokerDeathTests.cs`, `BrokerSessionHostTests.cs:79`, `CliServicesTests.cs:126` | Build the process with `BrokerTestHarness.StartInProcess` |
| `FileWizardTests/JournalCommandTests.cs:166-185` | `BrokerProcess` on the real path |
| `FileWizardTests/JournalWatcherTests.cs` | Delete `OnWatchFaulted_MergedSourceFailureDeactivatesEveryDrive` (`:296-303`); map `WatchFaultKind.Source` to `Drive` or `Channel` (`:105,226-256,321,354,387,421-441`); add a recovering case and `CatchUpLost` cases before and at the limit |
| `FileWizardTests/Maui/JournalHintLogicTests.cs` | `ScanCatchUp` hint cases (trimmed with and without a size, recreated) |
| `FileWizard/JournalWatcher.cs:175-215`, `FileWizardMaui/MainPage.LiveUpdates.cs:335-341` | Audit that nothing reached from `OnWatchFaulted` or `OnJournalEvent` blocks on a `FileIndex` lifecycle call; queue any that does (section 2.6.8) |

Unchanged: `App.xaml.cs:43`, `file-wizard/Program.cs:8` (`ElevatedEntryPoint.TryHandle`
keeps its signature), `MainPage.DriveRescan.cs:66`, `LargestSummaryCommand.cs:98`,
`CliRunner.Database.cs:51`.

git-wizard, read at `15be5f3` (`main`, equal to `gitea/main`):

| File | Change |
|---|---|
| `GitWizard/MftBrokerConnection.cs` | `JournalBrokerClient` becomes `BrokerProcess`; `BrokerDied` becomes `Ended` (`:64,108`) |
| `GitWizard/MftIndexSession.cs:41,53,185` | Factory type; `GetBrokerClient` becomes `GetBrokerProcess` |
| `GitWizard/MftIndexSession.cs:117` | Scan `Progress` arrives from several drives' scans at once during a concurrent open; the progress handler passed in must accept interleaved drives |
| `GitWizard/MftIndexSession.Windows.cs:43-44` | `BrokerProcess.LaunchAsync` |
| `GitWizard/Watch/IndexVolumeChangeSource.cs:59-64` | Grow through `BrokerProcess` |
| `GitWizard/Watch/IndexVolumeChangeSource.cs:229-233` | `StartWatchingAsync(ct)` results; each `Failed` drive goes through `RecordStartupDriveFailure` |
| `GitWizard/Watch/IndexVolumeChangeSource.cs:283-318` | Drop the null-drive branch; `terminal = _usableDrives.Count == 0`; `Drive`, `Apply`, and `CatchUpLost` without `RecoveryStopped` keep the drive usable and publish a recovering state; `Channel`, `Recovery`, and `CatchUpLost` with `RecoveryStopped` remove it |
| `GitWizard/Watch/IndexVolumeChangeSource.cs:299-317` | Audit that the `DriveFailed` subscriber (`RepositoryWatchService.cs:80`) and the `Stopped` subscribers reached through `ReportDeath` never block on a `FileIndex` lifecycle call; queue any that does (section 2.6.8) |
| `GitWizard/Watch/IndexVolumeChangeSource.cs:316-317`, `RepositoryWatchService.cs:225`, `IndexVolumeChangeSource.Startup.cs:47`, `IndexVolumeChangeSource.cs:153-158` | Verified safe, no change. A terminal fault's `ReportDeath` raises `SourceDied` inside the `WatchFaulted` handler; `SourceDeathSignal.OnSourceDied` starts `_cts.CancelAsync()` without waiting. That token reaches the pending `WaitForCatchUpAsync` (through `RepositoryWatchService.cs:128`, `:244` and `IndexVolumeChangeSource.cs:77`, `:127`). The wait's cancellation completes asynchronously (section 2.6.8), so the arming code's catch block runs `DisposeAsync` off the handler's stack, in its own context, with no active marker |
| `GitWizard/Watch/IndexVolumeChangeSource.Journals.cs:51-57` | `ProcessFaultedDrive` builds a journal warning for a `ScanCatchUp` loss as well as a `LiveWatch` one |
| `GitWizard/Watch/JournalHintBuilder.cs:19-60` | `ScanCatchUp` wording in `BuildRecreatedHint` and `BuildRescanClause`: a scan of the drive outlasted the journal, automatic rescans stopped after three, grow the journal |
| `GitWizard/Watch/IndexVolumeChangeSource.cs:353-360` | Batched stop; the `WasReported` filter goes, since stop returns faults as results |
| `GitWizard/Watch/IndexVolumeChangeSource*.cs` | Guard `_usableDrives` with one lock everywhere (`IndexVolumeChangeSource.cs:122,126,199,296`, `Startup.cs:66`, `Readiness.cs:60`, `Journals.cs:24`) |
| `GitWizard/Discovery/RepositoryDiscoveryCoordinator.cs:170-187` | Loop becomes batched `RescanAsync`; `Failed` results go to `accumulator.Errors` |
| `GitWizardTests/TestSupport/BlockBrokerFixture.cs` | Rewrite on `BrokerTestHarness.StartInProcess` with a real `JournalBrokerHost`, a fixture `IBlockSectionWriter` that writes its rows, and fake cursor and watch sources; the hand-written frames (`:126-292`) and `JournalBrokerScanSession` (`:22,78-80,109-115`) go |
| `GitWizardTests/BlockBrokerFixtureTests.cs:20,62` | `ArmScanAndCatchUpAsync` becomes `ScanDriveAsync` per drive |
| `GitWizardTests/TestSupport/ScriptedIndexWatchSource.cs` | Rewrite per drive: `StartAsync` returns a scripted handle; `Publish(letter, item)`, `FailDrive(letter, exception)`, `LoseChannel(letter, exception)` |
| `GitWizardTests` callers of `DriveWatchFailure` (`Discovery/WindowsWatchStartupTests.cs:245`, `Watch/IndexVolumeChangeSourceReadinessTests.cs:47,123`, `TestSupport/WatchFailureAssertions.cs:147,166`, `Watch/IndexVolumeChangeSourceTests.cs:357,370`, `UI/MainViewModelPreparedSessionTests.cs:160`) | `FailDrive` or `LoseChannel` on the scripted source |
| `GitWizardTests/Watch/JournalHintBuilderTests.cs`, `Watch/JournalWarningTests.cs`, `Watch/IndexVolumeChangeSourceTests.cs` | `ScanCatchUp` hint and warning cases; `CatchUpLost` fault before and at the limit |
| `GitWizardTests/ElevatedBrokerEntryDispatchTests.cs:14-20`, `UI/DesktopStartupTests.cs:10-15` | `RunBroker(string? controlPipeName)` |
| `GitWizardTests/MftBrokerConnectionTests.cs:29,79,128`, `MftIndexPersistenceTests.cs:44`, `Discovery/RepositoryDiscoveryCoordinatorWatchProgressTests.cs:81` | `BrokerProcess` type |

Unchanged: `GitWizardUI/Program.cs:23`, `git-wizard/Program.Watch.cs:36`,
`IndexVolumeChangeSource.Startup.cs:47`, `GitWizardUI/ViewModels/MainViewModel.Journals.cs:45`.

## 9. Test plan

Rules: tests that reference the native delegate seams in `MFTLibNative` or `FileUtilities`,
or a native test-hook global (`SetMaxThreads`, the per-chunk thread-count hook), keep
class-level `[DoNotParallelize]`. Time-based behavior (heartbeat, stall, connect
timeout) runs on a fake `TimeProvider`; ordering is proven with `TestGate` signals, never
elapsed time.

Deleted outright (paths under `MFTLib.Tests/`; `FileIndex*` files are under `Index/`):
`BrokerArmEpochDemuxTests.cs`, `BrokerArmOrderingTests.cs`,
`BrokerProtocolTests.ArmEpochFrames.cs`, `BrokerProtocolTests.DisarmDriveFrame.cs`,
`BrokerWatchSourceAbandonedStartTeardownTests.cs`,
`BrokerWatchStartSendCancellationTests.cs`, `BrokerIndexWatchSourceArmingTests.cs`,
`BrokerIndexWatchSourceArmingTests.LastDrive.cs`, `BrokerPerDriveArmTests.cs`,
`BrokerPerDriveArmTests.Recovery.cs`, `JournalBrokerClientTests.ControlExchange.cs`,
`JournalBrokerClientTests.ControlExchangeLifetime.cs`,
`JournalBrokerClientTests.LiveWatchChannels.cs`, `JournalBrokerHostTests.WatchArmEpoch.cs`,
`JournalBrokerHostTests.WatchArming.cs`, all nine `JournalBrokerScanSessionTests*.cs`,
`VolumeQueryScanSessionTests.cs`, `FileIndexWatchStartReadinessTests.cs`,
`FileIndexWatchRescanEndedSessionTests.cs`; support files `WatchSpecArmEpochs.cs`,
`ReadinessScriptedWatchSource.cs`, `SingleReaderGuardStream.cs`, and
`GateFrameWriteStream.cs` / `CancellableGateFrameWriteStream.cs` if no rewritten test uses them.

Rewritten against the per-drive shape: `BrokerProtocolTests.cs`, `.Frames.cs`, `.Scan.cs`,
`.GrowUsnJournalFrames.cs`; `JournalBrokerHostTests.cs` and its `.Watch`, `.WatchCatchUp`,
`.WatchRecovery`, `.WatchFailureClassification`, `.WatchDiagnosticsFilter`,
`.RequestDisconnect`, `.Scan`, `.Progress` parts; `JournalBrokerHostRealSeamsTests.cs` and
`.Operations.cs`; `JournalBrokerHostBlockScanTests.cs`; the remaining
`JournalBrokerClientTests*.cs` become `BrokerProcessTests*.cs`; `BrokerDeathTests.cs`;
`BrokerLiveWatchErrorTests.cs`; `BrokerIndexWatchSourceTests.cs`, `...CaughtUpTests.cs`,
`...FaultTests.cs`; `BrokerMftBlockProducerTests.cs`, `...ProtocolTests.cs`;
`BrokerBlockContractTests.cs`; `BrokerFileIndexRescanTests.cs`;
`GrowUsnJournalClientTests.cs`, `GrowUsnJournalHostTests.cs`;
`DefaultElevatedEntryRunnerTests.cs`, `ElevatedEntryPointTests.cs`;
`BrokerDiagnosticsTests.cs`; every `FileIndexWatch*Tests.cs`,
`FileIndexCacheOnlyUnresumableWatchTests.cs`, `FileIndexMidSessionCheckpointLossTests.cs`,
`WatchFailureObservationTests.cs`, `ConsumerJournalIsolationTests.cs`; support files
`FakeIndexWatchSource.cs`, `WatchHarness.cs`, `InProcessBlockBrokerHarness.cs`,
`ScriptedWatchBrokerHarness.cs`.

New tests. Every row runs on a fake `TimeProvider` where time matters and proves order
with `TestGate` signals; none waits on real time.

Channels and liveness:

| Test | Shape |
|---|---|
| One drive stalls while others flow | In-process host; X's watch source never yields and its channel heartbeats stop; Y yields batches. Y applies and catches up while X is stuck. Advancing the fake clock past the stall timeout faults X with `Channel`; Y is untouched |
| Blocked write on X does not delay Y's heartbeat | X's pipe buffer fills so writes to X block; the heartbeat sender's bounded write to X times out and X is closed/stalled; Y's pipe continues receiving heartbeats and Y remains healthy past the stall limit |
| A blocked `Changed` handler on X does not delay Y | Handler for X waits on a `TestGate`; a Y batch is applied and observed before the gate opens |
| Process death faults every drive by name | Harness ends the host; each watched drive raises exactly one `WatchFaulted(Channel, letter)`; each `DriveStatus.WatchFailureMessage` is set; `BrokerProcess.Ended` fires once |
| Idle watch stays alive | X's watch source never yields and reports `WaitingOnVolume`; advancing the fake clock well past the stall limit produces heartbeats and no fault |
| Idle control pipe stays alive | An idle session with no control requests; advancing the fake clock well past the 30 s stall limit produces control-pipe heartbeats; the control pipe remains open and the process stays alive |
| Host watchdog names a wedged loop | X's loop is held in `Processing` on a `TestGate`; advancing the fake clock past the processing limit yields `Stalled` on X's pipe and `WatchFaulted(Channel, X)` carrying the host's message; no recovery rescan starts; Y is untouched |
| Timed-out stop, then a new watch (issue 252) | Host holds X's pipe open and silent; `StopWatchingAsync(X)` times out and closes the pipe; a new `StartWatchingAsync(X)` opens a fresh pipe and receives batches; releasing the held host task delivers nothing to the new watch |
| One channel lost | Host closes X's pipe; only X faults |
| Channel open: cancelled before the host connects | The client cancels after writing `OpenChannel`; the host's connect ends within the connect limit on the fake clock; no channel task remains |
| Channel open: connected, then `Error` | The host connects and replies `Error`; the client disposes its stream; the host's channel ends on EOF |
| Channel open: connection failure | `BrokerTestHarnessOptions.FailConnection` for X's pipe; the client throws `InvalidOperationException` with the host's message |
| Channel open: no first request | The host connects and no request follows; the channel ends after the first-request limit |
| Channel open: first write fails | The client's first-request write throws; both sides are disposed; the host's channel ends |
| Control request ids | A cancelled `QueryVolumeAsync` wait; its late reply is dropped; the next request succeeds |
| Request ids wrap and skip | Allocation starts at `uint.MaxValue - 1` through an internal seam with id 1 still pending; the next ids are `uint.MaxValue`, then 2; zero is never issued |
| Request ids released on process end | Pending entries are gone after the control pipe closes; their callers fail with `BrokerChannelLostException` |
| Control write cancelled mid-frame | `HoldWrites` holds the client's control write after the length prefix; the caller cancels; the frame completes when released; the next request is answered |
| Control write fails mid-frame | The write throws after the length prefix; `Ended` fires; every pending request fails with `BrokerChannelLostException` |
| Diagnostics under concurrency | Lines from eight channels each carry their tag and none is lost |
| Diagnostics sink blocked | The writer's append waits on a `TestGate`; another channel's `Log` calls all return while it is closed |
| Diagnostics buffer full | The buffer fills behind a closed gate; after release the log holds one "records dropped" line with the right count |

Scans, admission and parse threads:

| Test | Shape |
|---|---|
| Scan admission | Host built with a processor count of 2; three scan channels, each fake scan source signalling on entry and waiting on a gate; exactly two entry signals arrive, each source reading an allowance of 1; the third channel heartbeats while `Queued`; releasing one gate admits the third |
| Queued scan leaves on cancel | Closing a queued scan's pipe removes it from the queue; its scan source is never invoked; a later scan is admitted |
| Allowance rebalanced at each chunk | Processor count 4; scan A alone reads 4 at its first chunk seam; B is admitted; A's next chunk and B's first read 2; B finishes; A's next chunk reads 4 |
| Remainder in admission order | Processor count 5 with two running scans: the first admitted reads 3, the second 2; with five scans every one reads 1 |
| No caller supplies a thread count | `ArmAndScan` round-trips with no thread field; a batched rescan of four drives and a recovery both reach the fake source with the host-computed allowance only |
| Native parse reads the allowance per chunk | Synthetic NTFS file with a 64-record chunk; the progress callback changes the control block's allowance; a native test hook records each chunk's thread count, which follows the change from the next chunk |
| Native parse stops when cancelled | The flag is set from the first chunk's progress callback; exactly one chunk callback fires; the result's `cancelled` is 1 and every buffer is freed; set before the call, no chunk is read |
| `ReadRecordBatches` cancelled during parse | Through the managed seam, cancelling from the progress callback throws `OperationCanceledException` before any batch |
| Cancelled scan keeps its place until native work stops | Processor count 1; the fake source ignores cancellation until a gate opens; closing its pipe leaves a queued scan queued until the gate opens |
| Scan cancellation | Cancelling X's scan closes X's channel; the fake scan source observes cancellation; Y's scan completes |
| Scan frame order | `Cursor`, `ScanProgress`, `ScanReady`, `JournalBatch` is accepted; `JournalBatch` before `ScanReady` fails with `BrokerChannelLostException` |
| Concurrent open | Two cold drives' producers are both inside before either gate opens; `OpenProgress` reports `U` with `SettledCount` 1 then `T` with 2 when `U` settles first, each from the settling thread; `Drives` keeps configured order |
| Open cancelled while gated | Every drive settles, every produced block is released, `OperationCanceledException` is thrown |

State machine (section 2.6):

| Test | Shape |
|---|---|
| Restart awaits the old drain | X's old pump is held inside `ApplyJournalEntriesCore` on a seam gate; `StopWatchingAsync(X)` with an already-cancelled token returns; `StartWatchingAsync(X)` does not publish a new handle until the gate opens and the old instance drains |
| Retiring pump's batch is dropped | A batch the old pump read before its drain is not applied to the block the successor armed from |
| Old instance's fault after a restart | The old handle throws after a restart; no `WatchFaulted` for it; the new instance's slot stays `CatchingUp` |
| Stop during start | The source's `StartAsync` waits for its token; `StopWatchingAsync(X)` cancels it; the start throws `OperationCanceledException`; no pump runs |
| Dispose during start | Same source; `DisposeAsync` completes without the test releasing anything |
| Handle returned after stop | The source ignores its token and returns a handle after the stop; the start path disposes it once; no pump runs |
| Rescan holds the lifecycle gate | With X's producer gated, `StartWatchingAsync(X)` waits until the rescan finishes |
| Concurrent rescans | X's and Y's producers each signal on entry and wait on a gate; both entry signals arrive before either gate opens; both blocks commit; the snapshot holds both |
| Concurrent blockless adoption | Two blockless drives rescanned together get distinct ordinals and both resolve through `Snapshot.GetDriveBlock` |
| Blockless adoption, mixed outcome | X's producer fails and Y's succeeds, overlapping by gates, in both completion orders; X reads `Failed` with its own message, Y reads `Ready` with none |
| Blockless adoption, both fail | Both messages survive, each on its own drive |
| Rescan of X leaves Y's pump running | Y applies a batch while X's producer is gated |
| Stop during rescan | `StopWatchingAsync(X)` returns while X's producer is gated; the rescan commits and does not start a watch |
| Drive fault recovers | Host `Error` on X; `WatchFaulted(Drive, X)`, producer invoked once, X reaches `CaughtUp`, the `LiveWatch` loss survives |
| `Recovering` before the event | A `WatchFaulted` handler reading `Drives` sees X `Recovering` |
| Second fault before catch-up | X faults again while `CatchingUp` after recovery; `WatchFaulted(Recovery, X)`; producer not invoked again |
| Queued recovery superseded by a manual rescan | The recovery is held before its gate; a manual rescan completes; releasing the recovery invokes the producer no further time |
| Queued recovery after stop | Stop clears the ticket; the recovery ends without scanning |
| Obsolete recovery failure | A superseded recovery's scan fails; the newer watch is not marked faulted |
| Two drives fault together | Both recoveries' producers are inside before either gate opens |
| Disposal during rescans | Disposal cancels gated rescans, awaits every pump, and releases every gate |
| Handler calls stop on its own drive | A `Changed` handler blocks on `StopWatchingAsync(X)`; it gets `InvalidOperationException` at once (bounded by `WaitAsync`, so a regression fails instead of hanging); the pump continues |
| Handler calls stop on another drive | X's `Changed` handler blocks on `StopWatchingAsync(Y)`; it fails at once; Y keeps watching |
| Two-handler cycle | X's and Y's `Changed` handlers are each released by a `TestGate` and then block on `StopWatchingAsync` of the other drive; both calls fail at once; both pumps continue and apply a later batch (bounded `WaitAsync`, so a regression fails instead of hanging) |
| Handler calls rescan, start, dispose or an unsettled wait | A `WatchFaulted` handler calling `RescanAsync(Y)` or `StartWatchingAsync(Y)`, a `Changed` handler calling `DisposeAsync`, and one calling an unsettled `WaitForCatchUpAsync(Y)` all fail at once; a settled wait returns its result |
| Marker survives an await inside the handler | The handler blocks on an async helper that awaits a completed `TestGate` on the thread pool and then calls `StopWatchingAsync(Y)`; the call fails |
| Queued work after the handler returns | The handler queues `StopWatchingAsync(X)` with `Task.Run` gated to start after the handler returns; the stop succeeds and X drains |
| Handler may query and grow | A handler runs `Search`, reads `Drives`, and awaits `BrokerProcess.GrowUsnJournalAsync` through the harness; all succeed |
| Published handle disposed exactly once | For each pump exit (stop, `Drive` fault, lost channel, disposal, stop racing a fault) the fake handle's `DisposeCount` is 1 |
| Unpublished handle disposed exactly once | A start cancelled by stop before publication: the returned handle's `DisposeCount` is 1 and no pump ran |
| Handler cancels a waiter's token | A `WaitForCatchUpAsync(Y)` is pending with a token from a test-owned source. X's `WatchFaulted` handler sets a test `[ThreadStatic]` flag, calls that source's synchronous `Cancel()`, and clears the flag. The awaiter's continuation records whether the flag was set on its own thread, then calls `DisposeAsync` on the index. Assert the recorded value is false (an inline continuation on the handler's stack would read true) and that `DisposeAsync` completes. No thread ids, no ordering, no time |
| Pump's fault settles a waiter | A `WaitForCatchUpAsync(X)` and a batched wait naming X are pending. X faults; an internal test hook around the pump's fault-settlement step (the step of section 2.6.4 that faults X's slot, before `WatchFaulted` is raised) sets the same `[ThreadStatic]` flag for exactly that step and clears it after. Each awaiter's continuation records the flag on its own thread and then calls `StopWatchingAsync(Y)`. Assert every recorded value is false and every stop completes |
| Stop's cancellation settles off the handler's stack | A `StopWatchingAsync(Y)` is pending on Y's `Drained` with a token from a test-owned source. X's `Changed` handler sets the flag, cancels that source, and clears the flag. The stop's caller records the flag on its own thread in its `OperationCanceledException` handler, then calls `RescanAsync(Y)`. Assert the recorded value is false and the rescan completes |

Lost catch-up (section 2.6.6; journal windows come from `JournalIsolation.OverrideJournalWindow`, catch-up outcomes from the fake host catch-up source):

| Test | Shape |
|---|---|
| Host writes `CatchUpLost` for a proven loss | The catch-up source throws and the journal window puts the armed cursor below `FirstUsn`; frames are `Cursor`, `ScanProgress`\*, `ScanReady`, `CatchUpLost` carrying that loss, then EOF; the cursor source is not queried again |
| Recreated journal is a proven loss | The window's journal id differs from the armed one; `CatchUpLost` with cause `JournalRecreated` and no size; it counts |
| Read failure with the cursor retained is a drive error | The catch-up source throws while the window still holds the armed cursor; the host writes `Error` after `ScanReady`; the scan fails with no block, `ConsecutiveLostCatchUps` is unchanged, no `ScanCatchUp` report, no `CatchUpLost` fault |
| Journal query unavailable is a drive error | The catch-up source throws and the override returns null; same outcome as the row above |
| Lost catch-up recovers | X watching; a rescan's first catch-up is lost and the second succeeds; `WatchFaulted(CatchUpLost, X)` once with `ConsecutiveLostCatchUps` 1 and `RecoveryStopped` false; producer invoked twice; count back to 0; the watch starts from the second block's cursor; the `ScanCatchUp` loss is kept |
| Three in a row stop recovery | Every catch-up is lost; the producer is invoked exactly three times; the third fault has `RecoveryStopped`; X reads `Faulted` with a `ScanCatchUp` loss whose `SizeThatWouldHaveRetained` matches `JournalSizeArithmetic` for the synthetic window; `RescanAsync(X)` throws `JournalCatchUpLostException` naming that size; `StartWatchingAsync(X)` is refused |
| Success resets the count | Two losses, then a success: count 0; a later single loss retries rather than stopping |
| Manual rescan at the limit | Count 3; a manual rescan whose catch-up is lost invokes the producer once, raises the count to 4 and throws; a manual rescan that succeeds resets the count and a start is accepted |
| Count survives operations and watch instances | A recovery loses two catch-ups and then its producer fails without a block (count stays 2, `Recovery` fault); the next manual rescan loses once and stops at 3 with one producer call |
| Lost catch-up at open | The open's first scan of X loses its catch-up and the second succeeds; `OpenAsync` returns X `Ready`, count 0, `ScanCatchUp` loss kept; three losses at open leave X `Ready`, unresumable, count 3, and `OpenAsync` does not throw |
| Other drives unaffected | Y applies batches and completes a rescan while X retries lost catch-ups |

Batched calls and boundaries:

| Test | Shape |
|---|---|
| Batched start with partial failure | Source `StartAsync` throws for Y; an enumeration-backed Z; results are X `Succeeded`, Y `Failed` with that exception, Z `NotApplicable`; the call does not throw; X watches |
| Batched cancellation settles first | Token cancelled while Y's start is gated; the call throws `OperationCanceledException` only after Y's start settles; no handle is left published |
| Namespace boundary | `IIndexDriveWatch`, `DriveWatchFaultException`, `JournalCatchUpLostException`, `DriveOperationResult` reference nothing in the flat `MFTLib` namespace (existing `NamespaceBoundaryTests` covers this once built) |

## 10. MFTLib issue 252

MFTLib issue 252 (a timed-out stop releases the watch source so a late EndWatchAck ends
the next watch) is open and is closed by the implementation of this design. With one
pipe per operation a stop is closing that drive's pipe, so no acknowledgement from one
watch can reach another. The implementation's pull request carries `Closes #252`, and
its tests include the case from that issue: a stop that times out, followed by a new
watch on the same drive, which runs undisturbed.

The implementation branches from main. MFTLib pull request 257, which fixed issue 252
on the shared connection, is closed unmerged, so none of its additions are on main and
the plan has nothing of it to delete.

## 11. Open items

- The heartbeat interval (5 s), processing limit (30 s) and stall limit (30 s) are
  chosen, not measured. The heartbeat sender has its own thread, but it still competes
  for processor time with native scan threads; confirm on hardware with a multi-drive
  concurrent rescan before relying on the 30 s values. The channel connect and
  first-request limits (30 s each) and the control-closed grace period (5 s) are chosen
  the same way.
- Whether several scans at once are faster than one at a time is unmeasured, and the
  answer may differ for spinning or shared storage, where the disk and not the processor
  is the limit. Measure concurrent open and a multi-drive batched rescan against
  sequential ones on real hardware (owner, attended). The parse-thread budget
  (section 2.3) bounds processor use; it does not bound disk contention. The parser's
  per-scan read, fixup and parse times (`ioTimeMs`, `fixupTimeMs`, `parseTimeMs`) show
  which limit a scan hit.
- Rebalancing lets the total exceed the processor count for up to one chunk after an
  admission (ruling 6). The size of that overshoot is the chunk size, which the plan's
  scan pipeline audit sets.
- The two readings of the rulings this spec made were confirmed by the owner on
  2026-09-28: a scan runs on its own drive pipe, with only the channel-open handshake on
  the control pipe (a cancelled scan must not leave uncertainty on the pipe every drive
  depends on, the problem today's `JournalBrokerClient.ControlExchange.cs:67-73` solves by
  aborting the whole connection); and a recovery rescan keeps the drive's `LiveWatch`
  checkpoint-loss report, which both consumers turn into a journal-size hint
  (`FileWizard/JournalWatcher.cs:199-202`, git-wizard `IndexVolumeChangeSource.Journals.cs:51-57`)
  and which a successful rescan otherwise clears (`FileIndex.Rescan.cs:207`).

## Appendix A: corrections to the map in the issue body

The map was read at `2caac78` (pull request 257's head). At `692820f` (main):

| Map claim | Finding at main |
|---|---|
| `JournalBrokerClient.RetiredReader.cs` and the "whole-frame read rule" (`Transport.cs:103-133`) | Do not exist. Both are pull request 257 additions. Main's `ReadFrameAsync` passes the caller's token to both reads (`JournalBrokerClient.Transport.cs:103-126`) |
| Host `WatchGeneration` holds `Generation` and `LastEndedGeneration` (`Session.cs:238-246`); `EndWatchGenerationAsync` (`:186-216`); a conflicting generation is refused (`:152-156`) | Main's `WatchGeneration` has only `DriveWatches` and `Cancellation` (`JournalBrokerHost.Session.cs:200-204`). `EndWatch` carries no generation (`:129-133`); the refusal comment at `:148-153` says the refusal was replaced |
| Client `_lastWatchGeneration` and `_activeWatchGeneration` (`LiveWatchDemux.cs:18-22`) | Main has a `bool _liveWatchGenerationStarted` (`JournalBrokerClient.LiveWatchDemux.cs:19`) |
| `StartWatch` factory rejects a zero generation (`BrokerFrame.cs:86-101`) | Main's `StartWatch` takes only a drives spec (`BrokerFrame.cs:84-93`) |
| `IIndexWatchSource.StartWatching` has three overloads (`:47-108`) | Two (`IIndexWatchSource.cs:45-46`, `:77-82`); the teardown-token overload is pull request 257's |
| Tests `BrokerIndexWatchSourceGenerationFenceTests.cs`, `BrokerIndexWatchSourceStalledFrameTests.cs`, `BrokerStrayEndWatchTests.cs`, `JournalBrokerHostTests.WatchGeneration.cs`, `JournalBrokerClientTests.StopMidFrame.cs`, `Index/FileIndexWatchTeardownTokenTests.cs` | None exist at main; all are pull request 257 additions |
| `_unreportedWatchFaults` at `FileIndex.Watch.cs:13` | Declared at `FileIndex.WatchFaults.cs:13` |
| "accepted for v1" `_swapGate` comment at `FileIndex.Watch.cs:255-256` | `FileIndex.Watch.cs:239-241` |
| Arm epochs "shrink, not delete": a re-arm always races frames in flight for the old arm | With one pipe per arm the old arm's frames are on a closed pipe nobody reads, so no tag is needed; deleted |
| `DisarmDrive` "still needed" | Closing the drive's pipe is the disarm; the frame is deleted |
| Unreported-faults ledger and session reclaim "still needed" | Correct only under one session per index; ruling 2 removes the session and both go |
| `ReadyOnFirstMoveWatchStream` "still needed, unchanged" and `WatchStreamNotRunningException` "shrink" | Both deleted: `StartAsync` returns when ready, and there is no shared stream to be absent |
| Section 3: `_rescanGate`/`_swapGate` stay index-wide under per-drive channels | Overtaken by ruling 5 |
| Section 6: file-wizard reaches the broker only through `FileIndex` | It also calls `GrowUsnJournalAsync` on the client directly (`FileWizardMaui/SettingsPage.xaml.cs:129`, `file-wizard/JournalCommand.cs:146`), and its tests drive the client's wire API (`FileWizardTests/BrokerDeathTests.cs:23`, `CliServicesTests.cs:126`) |
| Section 6: git-wizard reaches the broker only through `FileIndex` | It also calls `GrowUsnJournalAsync` directly (`GitWizard/Watch/IndexVolumeChangeSource.cs:59-64`); its tests implement `IIndexWatchSource` (`GitWizardTests/TestSupport/ScriptedIndexWatchSource.cs`) and `IElevatedEntryRunner` (`ElevatedBrokerEntryDispatchTests.cs:14`, `UI/DesktopStartupTests.cs:10`), and `BlockBrokerFixture.cs` hand-writes wire frames |
| Section 4c: no process-global native state found (not read in full) | Confirmed: the native library's mutable globals are test hooks only (`MFTLibNative/core/test_hooks.cpp:10-32`) |
| Section 2, "Control exchange": the host's single `ServeFramesAsync` loop | Also serializes scans inside the host, because `HandleArmAndScanAsync` is awaited inline (`JournalBrokerHost.Session.cs:87-92`); the map does not mention that the host cannot read a disarm or arm while a scan runs |
| Section 2: "stalled broker" handling | `Heartbeat` is defined but never written by the host (`BrokerFrame.cs:12,115-118`, `BrokerProtocol.Write.cs:60`); the only stall detection is the 5-second EndWatchAck timeout |

Line numbers in the map are otherwise off by 2 to 30 lines against main in
`JournalBrokerHost.Session.cs`, `JournalBrokerClient.LiveWatch.cs`,
`JournalBrokerClient.LiveWatchDemux.cs`, `BrokerIndexWatchSource.cs` and
`FileIndex.WatchSession.cs`, because pull request 257 edits those files.
