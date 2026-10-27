# Broker lifetime

- Index contracts:
    - **VolumeBroker**: `BrokerProcess` owns one elevated process and its control pipe, with one
      UAC prompt per consumer session. Nonzero request ids route concurrent `OpenChannel`,
      `QueryVolume`, and `GrowUsnJournal` replies; an id remains reserved until its reply arrives
      or the process ends. Opening a drive operation creates a client-owned pipe, sends its name
      on the control pipe, waits for both the host connection and `ChannelOpened`, and writes one
      request. Each scan or watch then owns that drive channel, so closing or faulting it cancels
      only that operation. Control EOF ends the process and every channel;
      the `BrokerProcess.Ended` task completes with the reason, and pending work fails with
      `BrokerChannelLostException`. The host admits concurrent scans under one processor-sized
      parse-thread budget and rebalances each `ParseThreadAllowance` when scans enter or leave;
      native parsing reads the allowance at each chunk. See [scan protocol](#scan-protocol)
      for block production and catch-up frames.
      One dedicated background thread visits every pipe every five seconds. An idle control pipe,
      a watch waiting on its volume, a queued scan, and a processing operation that has reported
      progress less than 30 seconds ago receive `Heartbeat`. This includes a `Processing` step
      that reports progress without writing an operation frame, such as bounded catch-up after
      `ScanReady` or block flush. Heartbeats do not reset the processing progress clock: at or
      beyond `ProcessingLimit` (30 seconds) without progress, the pipe writes `Stalled` and its
      channel is cancelled. A pipe that wrote an operation frame since the previous visit skips
      that visit. A pipe with a frame write in flight is also skipped, so it cannot delay other
      pipes; the client's 30-second no-frame limit then closes that pipe. Any frame resets the
      client limit. EOF, write/read failure, silence, `Stalled`, and invalid frames surface as
      `IOException`, not drive-reported errors. A slow scan or watch reader holds back only
      its own channel. See [session ownership](#session-ownership) for the public lifetime.
      `BrokerMftBlockProducer` validates
      completed blocks and transfers them to the index, while `BrokerIndexWatchSource` opens one
      channel per drive watch. `ElevatedEntryPoint` and `BrokerLauncher` dispatch `--broker` mode.
      `BrokerDiagnostics` writes through a bounded background queue and filters the two diagnostic
      logs from journal batches unless `MFTLIB_BROKER_DIAG_INCLUDE_SELF=1` opts in.

## Timing policy

`BrokerLiveness` is the single home of broker timing defaults and diagnostic seconds
formatting (`BrokerLiveness.FormatSeconds`):

- `HeartbeatInterval` (5 seconds): host heartbeat and processing-progress visit interval.
- `ProcessingLimit` (30 seconds): maximum host processing time without progress before
  the pipe writes `Stalled` and cancels the operation.
- `StallLimit` (30 seconds): maximum client wait between incoming frames on the control
  pipe or a drive channel.
- `ControlReplyTimeout` (30 seconds): client wait for a control request write, a control
  reply, or the host's channel pipe connection after `ChannelOpened`.
- `ControlClosedGracePeriod` (5 seconds): host drain grace period for in-flight channel
  tasks after the control pipe closes.
- `ChannelConnectTimeout` (30 seconds): host wait for a newly opened channel pipe to
  connect.
- `FirstRequestTimeout` (30 seconds): host wait for a connected channel pipe's opening
  request frame.
- `DefaultConnectTimeout` (30 seconds): default client wait for an elevated broker
  process to connect back to the caller's control pipe during launch.
- `ElevatedConnectTimeout` (30 seconds): default wait in the elevated `--broker` child
  for the caller's control pipe and each drive pipe.
- `DiagnosticsFlushTimeout` (2 seconds): elevated `--broker` exit wait for diagnostic
  log flushing.
- `ProgressThrottleInterval` (250 milliseconds): default minimum interval between
  intermediate `ScanProgress` frames during a drive block scan.

## Session ownership

`BrokerSession` owns one `BrokerProcess` for a consumer session. Construction launches
nothing. The first scan, watch or `GrowUsnJournalAsync` launches the broker; concurrent
first uses share that launch. A caller's cancellation ends only its wait, never the
launch or another caller's wait. Only disposal cancels the launch.

A declined UAC prompt throws `InvalidOperationException`, and a connection timeout
throws `TimeoutException`, for callers waiting on that attempt. Failed launches are
not cached: the next use launches again and raises `Connecting` again, including a
rescan after a declined prompt.

The unelevated client creates every pipe server (the control pipe and each drive pipe) with a DACL that grants only its own user SID, single instance. The elevated broker connects as the client end with `TokenImpersonationLevel.Identification`, so a server cannot impersonate the elevated token, and after each connect refuses the pipe (`UnauthorizedAccessException`; the control pipe ends the broker with exit code 1) unless its DACL names only the broker's own user SID. `PipeOptions.CurrentUserOnly` is not used on the broker's ends: .NET compares the pipe owner with the token's owner SID, which is Administrators in an elevated token, so it would refuse the unelevated user's pipe. The broker waits 30 seconds, on its runner's clock, for the control pipe and for each drive pipe (`TimeoutException` for a drive); with no server the broker exits with code 1 instead of hanging elevated. The client does not compare the connecting process id with the launched process, which would change the public launch delegate.

The launch runs on the thread pool, never the caller's thread or synchronization
context, so the blocking UAC prompt does not block a UI caller. Disposal is checked
before and after `Connecting`: if it overtakes the launch before the launcher runs,
including when a `Connecting` subscriber disposes the session, the launcher never
runs and its callers receive `OperationCanceledException`.

`Connecting` and `Connected` run on the launching thread-pool thread outside every
session gate; subscribers may call any member. A throwing subscriber does not prevent
the others from running, and its exception reaches callers waiting on that attempt.
A throwing `Connecting` subscriber fails the attempt before anything launches. A
throwing `Connected` subscriber fails the attempt after the process exists: the
session retains and owns that process, and the next use returns it.

`Ended` is a `Task<string>` that completes once, shortly after the launched process
ends, with the reason (control pipe lost or broker exited). It never faults or
completes at disposal: a session that never launched, or is disposed while its
process is alive, never completes it. A process end completed before disposal is
still published. Read `Ended.IsCompleted` for a flag. Continuations run asynchronously,
outside session gates, and may call any member, including `DisposeAsync`.

A session whose process ended stays ended: later uses throw `InvalidOperationException`
naming the reason. It never relaunches behind indexes referencing the dead process.
Close those indexes, dispose the session, then create a new session and new indexes.
`DisposeAsync` is idempotent. It cancels and awaits an in-flight launch, reclaims and
disposes a process arriving after disposal began, then disposes the owned process.
Any use after disposal throws `ObjectDisposedException`.

## Scan protocol

The producer queries MFT sizing, creates the client-owned block section and opens
a scan channel. The host captures the journal cursor, opens the section, validates
a private copy of its header against the mapped length and fails the request with
an `Error` frame when the layout disagrees ([MFT blocks](index-format.md#mft-blocks)
lists the checks), scans MFT records into the shared block using only that copy's
geometry, completes and flushes it, then reads the journal from the armed cursor
to the tip in bounded chunks, retaining none of those entries. It reports successful
catch-up or a proven catch-up loss. [Checkpoint loss](checkpoint-loss.md)
defines proof and recovery; [scan sizing](broker-scan-tuning.md#sizing-the-block)
defines the volume query and capacity planning.

The broker-internal scan result carries the completed block, independently received armed cursor and optional `CatchUpLoss`. The producer validates that cursor against the completed header before transferring ownership. The index-facing result carries the block, skipped record count and optional loss; the header supplies the durable cursor used to start watching. The index validates returned block structure, producer kind and requested cache tag before adoption. Rejected completed output is closed and deleted under the caller's ownership, with cleanup diagnostics, so a later open cannot warm-start it.

`DriveStatus` exposes the block's row and skipped counts and, after proven loss, `DriveWatchStatus.CheckpointLoss`. Proven loss still returns the completed block. `BlockScanOutcome` carries the block and skipped record count; row count and name-pool size are in the header. The section writer returns row count, name-pool usage and skipped record count; capacity exhaustion is read from the block header. The watch replays the scan window and anything after it, provided those entries are still retained when watching starts. A loss after scanning is reported by the watch.

Frames are `Cursor`, zero or more `ScanProgress`, `ScanReady` (skipped record count), then one terminal frame: payload-free `ScanCompleted` for successful catch-up or `CatchUpLost` with the journal facts of proven loss. Catch-up entries stay on the host. `Error` replaces any of these frames, including an unproven catch-up failure. `BrokerMftBlockProducer` disposes a block that fails validation and fails its scan; a validated block transfers to the index.

`ScanProgress` carries a 32-bit phase (Parsing = 0, Transferring = 1), then four signed 64-bit values: records processed, name-pool bytes processed, total records and total name-pool bytes. Unknown totals are encoded as -1. The length field is 37 (kind plus payload), for 41 bytes including the four-byte prefix. The channel identifies the drive. Watchdog progress and progress throttling use their own clocks.

The frame length limit is 16 MiB, checked before sending and before allocating a
received frame. Every frame kind must consume its declared payload completely; any
unconsumed bytes inside a frame's declared payload fail decoding with
`InvalidDataException`, including for payload-free frames, while bytes after the
declared frame length belong to subsequent frames in a combined input. A native-source watch `JournalBatch` is one 64 KiB journal read;
`Error` and `Stalled` text is truncated to 32,768 UTF-16 units. A
`FileIndexOptions.RetainedFileNames` list making `ArmAndScan` exceed the limit
(32,640 maximum-length names with the fixed 47-unit section name) throws `ArgumentException`
from `FileIndex.OpenAsync` for an index with an MFT-producer drive, before any cache directory is created. An oversized journal batch fails that watch with `Error`, like any
source failure.

`ArmAndScan` encodes the section name, a 32-bit include-freed flag (0 or 1), then
a signed 32-bit name count: -1 for full retention, 0 for directories only, or the
number of following UTF-16 names. The section name must match `mftlib-block-<drive>-`
followed by 32 ASCII hex digits (`0-9`, `a-f`, `A-F`) for the scan channel's drive
letter (47 UTF-16 units); any other section name, counts below -1 or beyond the
remaining payload, truncated fields, invalid flags and trailing data are malformed
requests and write an `Error` frame on the scan channel before the host touches the
volume or opens the named section.

## Control requests

`OpenChannel`, `QueryVolume`, and `GrowUsnJournal` carry the request id followed by one uppercase ASCII drive character in two little-endian UTF-16 bytes, without a string-length prefix. `OpenChannel` then carries the length-prefixed pipe name, which must match `mftlib-broker-` followed by 32 ASCII hex digits (`0-9`, `a-f`, `A-F`, 46 UTF-16 units); `GrowUsnJournal` then carries the two signed 64-bit sizes. Invalid or noncanonical drive characters, truncated fields, and trailing request payload bytes are malformed control frames and end the session before a volume source or channel connector runs; an `OpenChannel` pipe name that fails the `mftlib-broker-<32hex>` check replies with a request-id-correlated `Error` frame on the control pipe without connecting to the named pipe while the control session continues. Application character inputs normalize through `VolumeKey` before encoding. Progress frames inherit identity from their channel.

`BrokerSession.GrowUsnJournalAsync` is a control request, like the internal volume
query. It grows a journal in place, never shrinks it, refuses a maximum at or below
the current size or above `min(8 GiB, 5% of the volume's total size)` queried at
request time through `GetDiskSpaceInformationW` (`ActualTotalAllocationUnits *
SectorsPerAllocationUnit * BytesPerSector`), and returns settings read back from the
volume. A cap refusal replies with a request-id-correlated `Error` frame before
querying or growing the journal and leaves the broker session alive.

Cancellation before a request starts writing sends nothing. Once writing begins,
the process completes the frame to avoid corrupting the control stream with a
partial request. A cancelled caller stops waiting and the eventual reply is
discarded. A control write failure ends the process. A reply timeout throws
`TimeoutException` without reusing the request identifier.

## Diagnostics configuration

After early child dispatch, clients call `BrokerDiagnostics.Enable(role, logDirectory)`
with an application-owned directory they create themselves. Environment-only opt-in
(`MFTLIB_BROKER_DIAG=1`) uses the OS temp directory and the `client` role. Each client role
appends to its own `broker-diagnostics.<role>.log`. Roles are nonempty ASCII letters,
digits, dash and underscore only; invalid roles throw `ArgumentException` at `Enable`.
Drop notices distinguish `queue full` from `append failed`, including the latest append
exception's type and message; failed notices retain the counts until recovery.
The launcher forwards the resolved
absolute log path as quoted `--diag-log` alongside `--diag`. Child dispatch derives its
directory from that path, enables the broker role, and supplies the client path to
journal self-filtering before invoking the runner. The path must already be normalized,
fully qualified, and named `broker-diagnostics.<role>.log` with the same role validation.
The dispatched broker exclusively creates `broker-diagnostics.broker.log` in that directory on its first queued write and retains the handle through the diagnostic drain. It refuses any pre-existing output file, hardlink, symbolic link or directory, without changing that object. A reparse-point check precedes atomic exclusive creation; exclusivity is what prevents a check/open race. Creation and write failures drop diagnostics through the existing append-failure accounting and do not stop the broker session. The elevated process never deletes or rotates an occupied log. To capture another session, stop the previous broker and archive or remove its log from the unelevated application or shell before launching again. Concurrent sessions need separate capture directories if both broker logs are required. The forwarded client log may already exist. Missing or valueless paths,
directory-only arguments (including an existing directory named `broker-diagnostics.ui.log`),
different file names, `.` or `..` components, invalid characters,
and reserved device directory names in the ordinary Windows namespace are handled broker launches that exit with
code 1 without serving a session or falling through to application startup. Without `--diag`, dispatch ignores
the diagnostics arguments and does not configure diagnostics. Extended-length Windows paths
allow literal reserved device directory names while retaining the component checks.

Diagnostics are best effort and disabled by default. Queued disk writes do not
block channels. Each line carries `control` or the complete client-created drive pipe name,
shared by both endpoints. Applications
with an opt-in switch honor either it or `MFTLIB_BROKER_DIAG=1` by enabling diagnostics
after child dispatch. The broker relies on the forwarded command line, not inherited
environment or application configuration before dispatch. Set
`MFTLIB_BROKER_DIAG_INCLUDE_SELF=1` before launch to retain the diagnostic logs' own
journal entries while debugging diagnostics.

The background diagnostics channel holds up to 8192 queued lines. Admission does
not wait for disk I/O: a full queue refuses the line and counts a queue drop. A
completed writer also refuses new records while draining accepted records.
Flush snapshots accepted lines and waits until each has been appended or handled
as an append failure; refused lines do not extend that target. Drop notices are
best effort, emitted before a later append, and retain queue and append-failure
counts if the notice itself fails. Broker session exit, including a faulted session, completes diagnostic admission and waits for accepted records and sink closure for at most two seconds on the runner's clock. Flush during a session is nonterminal and waits only for its accepted-record snapshot. Filesystem work, including sink closure, stays on the background drain.

Each watch filters the broker and client diagnostics paths by full file reference
(record number and sequence number), scoped to the watched drive. Paths are
resolved again on every batch so newly created or replaced logs become filtered;
prior references remain tracked for renamed logs. A filtered-empty batch emits
no JournalBatch, but its cursor still advances the CaughtUp decision.
