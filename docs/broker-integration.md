# Integrating the elevated broker

Master File Table (MFT) and USN journal access require an Administrator volume
handle. `BrokerProcess` lets a desktop or CLI application keep its main process
non-elevated while one elevated child performs raw-volume work for the consumer
session.

Use the broker when your application:

- has a UI or other code that should not run as Administrator;
- opens a `FileIndex` over MFT-backed drives;
- scans, watches, or rescans several volumes after one UAC prompt; or
- needs scan and watch failures isolated to the drive that produced them.

For a short tool that already runs elevated, use `MftVolume` directly instead.

## Process and channel model

One `BrokerProcess` owns the elevated child and its control pipe. Volume queries,
journal growth, and channel-open requests use that pipe. Each scan and each live
watch then runs on a new pipe dedicated to one drive and one operation.

The separation is important:

- a slow scan on one drive does not delay another drive's watch;
- a watch reader that stops consuming holds back only its own pipe;
- closing a watch pipe stops only that drive;
- a drive pipe failure fails only that drive's operations with `BrokerChannelLostException`; and
- losing the control pipe ends the process, completes the `BrokerProcess.Ended` task with the reason, and
  causes every open drive channel to fail.

The host sends heartbeats on an idle control pipe, a watch pipe waiting on its
volume, and a queued scan pipe. A processing operation that continues to report
progress also receives heartbeats while it remains within the processing limit.
A pipe with a host write already in flight is skipped by the heartbeat sender;
if it remains silent, the client's 30 second stall limit closes it. Any received
frame counts as activity. A processing step that reports no progress past its
limit receives a `Stalled` frame and its channel is cancelled. The client exposes
that as `BrokerChannelLostException`, not as a drive-reported error.

## 1. Dispatch broker mode before normal startup

The launched executable must recognize MFTLib's broker mode before initializing
the normal application. Put this at the beginning of `Program.cs`:

```csharp
using MFTLib;

if (ElevatedEntryPoint.TryHandle(Environment.GetCommandLineArgs()))
{
    return;
}

// Normal application startup follows.
```

Run the compiled app host (`MyApp.exe`), not `dotnet MyApp.dll`.
`BrokerLauncher` relaunches the current executable, so the current process must
be the application executable that contains this dispatch code.

## 2. Launch and own one BrokerProcess

Launch the process once for the lifetime of the indexes that use it:

```csharp
await using var broker = await BrokerProcess.LaunchAsync(
    BrokerLauncher.Launch,
    cancellationToken);

_ = broker.Ended.ContinueWith(
    ended => Console.Error.WriteLine($"MFT broker ended: {ended.Result}"),
    TaskScheduler.Default);
```

`LaunchAsync` creates the control pipe, invokes `BrokerLauncher.Launch`, and
waits up to 30 seconds for the elevated child. The
overload taking a `TimeSpan` lets a host choose a different connection timeout.
A declined UAC prompt throws `InvalidOperationException`; a child that does not
connect in time throws `TimeoutException`.

`Ended` is a `Task<string>` that completes when the control pipe is lost or the
process is disposed, with the reason. It never faults, and a caller that looks
after the end still receives the reason; `Ended.IsCompleted` tells whether the
process has ended. Pending control operations fail with
`BrokerChannelLostException`, and open drive pipes then fail independently as
they observe the process exit. `DisposeAsync` closes the control pipe and every
open drive channel. The reason the host ended is reported through `Ended` and
the affected operations, not thrown again by disposal.

The application owns the process returned by `LaunchAsync`. The connection
callback given to the producer and watch source borrows it; neither type disposes
it. After `Ended`, close the indexes using that process, dispose it, launch a new
process, and open new indexes against the replacement.

## 3. Build FileIndex over the broker

`BrokerMftBlockProducer` adapts broker scans to `FileIndex`. Its watch source
uses the same connection callback and opens a separate broker channel for every
drive watch.

Given an `IReadOnlyList<IndexedDrive>` named `drives`:

```csharp
using MFTLib;
using MFTLib.Index;

Task<BrokerProcess> ConnectAsync(CancellationToken token)
{
    token.ThrowIfCancellationRequested();
    return Task.FromResult(broker);
}

var producer = new BrokerMftBlockProducer(
    ConnectAsync,
    new BrokerScanOptions
    {
        Profile = BrokerScanProfile.Full
    });

var options = new FileIndexOptions
{
    Drives = drives,
    MftSource = producer.CreateIndexSource()
};

await using var index = await FileIndex.OpenAsync(options, cancellationToken);
```

`BrokerMftBlockProducer.CreateIndexSource()` returns the `MftIndexSource` that
`FileIndexOptions.MftSource` takes. The source carries both halves of the
connection: the block producer for cold opens and rescans, and the watch
source, implemented by `BrokerIndexWatchSource`. Each drive watch start
connects through the callback, opens the drive's pipe, writes its watch
request, and returns the drive's running watch. The watch source, the drive
watch handle and the block producer are internal to MFTLib; a consumer holds
only the opaque `MftIndexSource`.

A process that cannot scan assigns `MftIndexSource.Unavailable(reason)`
instead. Every scan of an MFT-backed drive then fails with
`Drive {letter}: {reason}.`, reported as `DriveFailureKind.ProducerFailed`,
and starting a watch throws `InvalidOperationException` with the same
message. Cached blocks still open.

The producer's optional `BrokerScanOptions` supplies `Profile` and
`KeepFileNames`; scan progress reaches the application as `IndexScanProgress`
through `FileIndexOptions.Progress`. A block that fails validation is disposed
and its scan fails; a block that passes transfers to the index.

### What a broker scan does

For one drive, the producer's internal scan first queries MFT sizing from the
broker, creates the client-owned block section, and opens a scan
channel. The elevated host:

1. captures the drive's journal cursor;
2. scans MFT records into the shared block;
3. completes and flushes the block;
4. reads the journal entries produced during the scan, from the armed cursor
   to the journal's tip, and keeps none of them; and
5. returns either the cursor that read advanced to or a proven catch-up loss.

The scan's internal result carries the block, the armed cursor and the advanced
cursor; `DriveStatus` exposes neither cursor, only the block's row and skipped
counts and, after a proven loss, `CheckpointLoss`. A successful result
contains the armed cursor and the advanced cursor. The catch-up entries do not
cross the pipe: a caller that wants them watches from the armed cursor. A proven
loss still returns the completed block, with `CatchUpLoss` set and
`AdvancedCursor` null. `BlockScanOutcome` carries the block and its skipped
record count; the row count and name pool size are in the block's header.

The `FileIndex` adapter deliberately watches from the cursor armed before the
scan, so the live watch replays the scan window and anything after it, provided
the journal still retains those entries when the watch starts. The advanced
cursor is where the host's catch-up read finished; the adapter keeps the armed
cursor and propagates a proven `CatchUpLoss`, and a loss after the scan is
reported by the watch.

The scan channel's frames are `Cursor`, `ScanProgress`*, `ScanReady` (the
skipped record count), then one terminal frame: `ScanCompleted` with the
advanced cursor, or `CatchUpLost` with the journal facts of the proven loss.
`Error` replaces any of them. The frame length limit is 16 MiB, enforced by the
writer before a frame is sent and by the reader before it is allocated. The
library's own frames stay far below it: a watch `JournalBatch` from the native
source is one 64 KiB journal read, and `Error` and `Stalled` text is cut to
32,768 UTF-16 units. A `BrokerScanOptions.KeepFileNames` list that makes the
`ArmAndScan` request exceed the limit (32,639 maximum-length names with the
default section name) is refused with an
`ArgumentException` before anything is sent. A custom `JournalBatchSource`
batch over the limit fails that watch with an `Error` frame, like any source
failure.

### Concurrent open progress

`FileIndex.OpenAsync` settles its drives concurrently. A drive whose scan loses
catch-up rescans itself up to `FileIndex.LostCatchUpRecoveryLimit` times. At the
limit, open still returns that drive as `Ready` with the last complete block,
but the block is unresumable and watching it is refused until a successful
`RescanAsync`.

`FileIndexOptions.OpenProgress` reports once for each drive that settles, on the
thread that settled it and with no index lock held. Callbacks can overlap and can
arrive out of order. `IndexDriveOpened.SettledCount` records settle order, so a
consumer tracking overall progress keeps the report with the largest count. A
drive whose settle is cancelled reports nothing; a cancelled or failed open may
therefore have reported only part of the configured drive set.

`FileIndexOptions.Progress` samples a scan while a producer runs and ends each
drive scan with one `IndexScanPhase.Finished` sample from `FileIndex`, after the
producer's last sample, for MFT-backed and enumeration-backed drives, in open and
in rescans. `IndexScanProgress.Outcome` is `Succeeded`, `Failed` or `Cancelled`
(non-null only on that sample). A rescan or lost-catch-up retry reports its own
`Finished`, so track finished drives as a set. A handler that throws on the
`Finished` sample is contained and logged to `FileIndexOptions.Diagnostics`; it
never replaces the scan's own exception or result.

## 4. Start and stop per-drive watches

The all-drive forms fan out concurrently and return one `DriveOperationResult`
per configured drive:

```csharp
var starts = await index.StartWatchingAsync(cancellationToken);
foreach (var result in starts)
{
    if (result.Outcome == DriveOperationOutcome.Failed)
    {
        Console.Error.WriteLine(
            $"Drive {result.DriveLetter} did not start: {result.Failure}");
    }
}

var catchUps = await index.WaitForCatchUpAsync(cancellationToken);
```

The single-drive overloads act on one letter. The list overloads act on the
letters supplied. Batched calls preserve that order in their result list, wait
for every drive they started to settle, and report per-drive failures without
hiding successful siblings.

`StartWatchingAsync` completes when each successful drive has a returned watch
handle and its pump is reading. It does not wait for the journal backlog.
`WaitForCatchUpAsync` waits for the drive's caught-up marker per drive.

`StopWatchingAsync` stops only the requested drive or drives. The single-drive
form rethrows that watch instance's outstanding fault once. The batched form
places it in the affected drive's failed result. `DisposeAsync` does not rethrow
watch faults.

If the watch source fails during a consumer start, the failed start leaves a watch
request and a refused-start fault. Stopping that drive clears both without
rethrowing the source-start failure. A fresh start after a faulted watch
instance supersedes that instance and discards its outstanding fault.

A rescan keeps the selected drive's healthy handle reading while its scan channel builds
another block. Failed or cancelled production leaves that handle and its catch-up waits
attached. At commit the new block is published and the old handle is retired together;
pending catch-up waits are cancelled then. After draining it outside the write gate and
state lock, the rescan starts a fresh handle from the new cursor when watching is still
requested. Other drives continue independently. `Changed` events can arrive from the old
block after publication and repeat during replacement catch-up; queries can lag until
the new watch reports `CaughtUp`. If a start was refused because
the block was unresumable, the refusal retains the watch request: a successful
`RescanAsync` clears the refusal and starts the watch for that drive.

## 5. Handle faults and recovery

Subscribe before starting watches:

```csharp
index.WatchFaulted += fault =>
{
    var status = index.Drives.Single(
        drive => drive.DriveLetter == fault.DriveLetter);

    Console.Error.WriteLine(
        $"Drive {fault.DriveLetter}: {fault.Kind}, {status.WatchCatchUp}");
};
```

`WatchFaulted` always names a drive. Faults from different drives may be raised
concurrently. The status has already been updated when the event runs.

| Signal | Meaning and consumer action |
| --- | --- |
| Completed `BrokerProcess.Ended` task | The control connection and elevated process are gone. Stop using the process, close its indexes, and create a new process and new indexes. |
| `BrokerChannelLostException` | A pipe reached EOF, failed, stalled, or carried an invalid frame. A watch reports this through `WatchFaultKind.Channel`. A channel fault never starts automatic recovery. Reconnect the process when needed, then rescan or reopen the affected state. |
| Drive watch fault (internal `DriveWatchFaultException`) | The host reported an `Error` on that drive's watch. `FileIndex` publishes `WatchFaultKind.Drive`, changes the drive to `Recovering`, and rescans it automatically. Observe the recovery rather than starting a competing lifecycle operation. |
| `JournalCatchUpLostException` | A scan completed, but the journal proved that the cursor armed before it had become unreadable. `WatchFaultKind.CatchUpLost` reports every attempt. Automatic retries stop when `RecoveryStopped` is true. Grow the journal when appropriate, then rescan; a successful rescan starts the watch if it is requested. |
| `WatchFaultKind.RescanRestart` | A rescan replaced the block but could not start its watch. The scan returns success. The exception and `WatchFailureMessage` identify the rescan; the inner exception is the start failure. No automatic recovery starts. A consumer start or rescan retries it, and stop rethrows the fault once. |
| `WatchCatchUpState.Recovering` | A drive or apply fault is being recovered, or a lost catch-up is being retried. Queries still use the current complete block, which may be behind the volume. |
| `WatchCatchUpState.Faulted` | Recovery did not restore the watch, a channel was lost, a start was refused, or the catch-up loss limit was reached. Inspect `WatchFailureMessage` and the fault exception. Call `RescanAsync` or `StartWatchingAsync` after the triggering condition is fixed. An unresumable block must be rescanned first. |

`WatchFaultKind.Apply` follows the same automatic recovery path as
`WatchFaultKind.Drive`. If the replacement watch faults before it first reaches
`CaughtUp`, or if the recovery scan or restart fails, the index publishes
`WatchFaultKind.Recovery` and leaves the drive `Faulted`. A second automatic
recovery is not started. `WatchFaultKind.Channel` also leaves the drive
`Faulted` without an automatic rescan.

A stop that races a recovery or rescan wins. It prevents that operation from
restarting the watch and reports the stopped watch instance's outstanding fault
once.

To follow a drive's state without polling `Drives`, subscribe to
`WatchStateChanged`. It delivers every change of a drive's `WatchCatchUp` once,
with that drive's next `WatchStateVersion`, before the `WatchFaulted` of the
fault that caused it (`DriveWatchState.Fault`). A finished automatic recovery is
`Recovering`, then `CatchingUp` when the restarted watch registers, then
`CaughtUp`. One drive's changes arrive in version order, but a read of `Drives`
can be newer than an event still being delivered, so keep the last version
applied per drive, seed it from `Drives` (which reads the state and the version
together), and drop older events:

```csharp
var gate = new Lock();
var applied = new Dictionary<char, long>();
index.WatchStateChanged += state =>
{
    // The check and the publication share one lock, so an older event
    // delivered concurrently can never publish after a newer one.
    lock (gate)
    {
        if (applied.GetValueOrDefault(state.DriveLetter) >= state.Version)
        {
            return;
        }

        applied[state.DriveLetter] = state.Version;
        Publish(state.DriveLetter, state.State);
    }
};
```

A decision a consumer tags with the version it read, such as "this drive is
ready", is superseded by any later event of that drive, including a fault that
lands just after the read.

### Callback reentrancy

Do not call an index lifecycle method synchronously from that index's `Changed`,
`WatchFaulted` or `WatchStateChanged` handler. Start, stop, rescan, dispose, their batched forms, and
an unsettled catch-up wait fail immediately with `InvalidOperationException`.
Queue the work so it begins after the handler returns. Queries, `Drives`,
`QueryUsnJournalSettings`, and already settled catch-up waits are allowed.
`BrokerProcess.GrowUsnJournalAsync` is outside the index and is also allowed.

## 6. Recover from a lost scan catch-up

A scan reads journal catch-up in bounded chunks. A chunk that returns entries
without advancing its cursor is an error because accepting it would deliver
duplicates. When a catch-up read fails, the host asks the live journal whether
the armed cursor is actually gone. Only a proven trimmed or recreated journal
becomes `JournalCatchUpLostException`; an unavailable journal query or a cursor
that is still retained remains an ordinary scan failure.

Every proven loss increments `DriveStatus.ConsecutiveLostCatchUps`. A successful
scan resets the count. A manual or recovery rescan retries until success or
`FileIndex.LostCatchUpRecoveryLimit`; at the limit it throws the last
`JournalCatchUpLostException`, keeps the complete block queryable, and refuses a
watch from that block. At open, the same retries happen before the drive settles;
the open returns the unresumable block instead of throwing at the limit.

The exception's `CheckpointLoss` has
`DetectedDuring == JournalCheckpointLossDetection.ScanCatchUp`. If its `Cause`
is `JournalCheckpointLossCause.CheckpointTrimmed` and
`SizeThatWouldHaveRetained` has a value, offer that value as the minimum journal
size that would have retained the cursor. If the cause is `JournalRecreated`, no
size would have preserved the old journal.

Journal growth is an explicit, persistent system change. After user consent:

1. Read current sizing with `index.QueryUsnJournalSettings(driveLetter)`.
2. Choose a new maximum greater than the current `MaximumSize` and at least the
   loss report's `SizeThatWouldHaveRetained`.
3. Call `broker.GrowUsnJournalAsync(driveLetter, maximumSize,
   allocationDelta, cancellationToken)`. The broker refuses a maximum at or
   below the current size and returns the settings read back after success.
4. Call `index.RescanAsync(driveLetter, cancellationToken)`.
5. The successful rescan clears the refusal and starts the watch if it is requested.
   Call `index.StartWatchingAsync(driveLetter, cancellationToken)` only if watching
   has not been requested.

Growing the journal does not make the already lost records reappear. The rescan
is what rebuilds current state; the larger journal reduces the chance that the
next scan window is lost.

## 7. Growing a journal

The broker also answers a volume query for the MFT sizing that block planning
uses; only `MftValidDataLength` and `BytesPerFileRecordSegment` cross the
protocol. The query is internal to the producer.

`BrokerProcess.GrowUsnJournalAsync` grows a journal in place. It never shrinks
one. It is a control request, like the volume query. Cancellation before its
request starts writing sends nothing. Once writing begins, the process completes the frame so a
partial request cannot corrupt the control stream; a cancelled caller stops
waiting and the eventual reply is discarded. A control write failure ends the
process. A reply timeout throws `TimeoutException` without reusing that request
identifier.

## Diagnostics

Set the log directory before enabling diagnostics:

```csharp
BrokerDiagnostics.LogDirectory = appDataDirectory;
BrokerDiagnostics.Enable("client");
```

Alternatively, set `MFTLIB_BROKER_DIAG=1` before launching. MFTLib propagates
diagnostics arguments across the `runas` boundary. Both processes append to
`broker-diagnostics.log` in `BrokerDiagnostics.LogDirectory`.

Diagnostics are best effort and disabled by default. Writes are queued so disk
logging does not block a channel. Each line carries its control or drive-channel
tag. While diagnostics are enabled, the broker filters the diagnostics logs' own
journal entries from the watch stream. Set
`MFTLIB_BROKER_DIAG_INCLUDE_SELF=1` before launching to retain those entries
while debugging diagnostics themselves.

## Deployment checklist

- Target .NET 10 and Windows x64.
- Reference the `MFTLib` NuGet package; its transitive build target copies
  `MFTLibNative.dll` to the output directory.
- Publish an executable app host and launch that `.exe`.
- Dispatch `ElevatedEntryPoint.TryHandle` before normal app startup.
- Launch one `BrokerProcess` for the consumer session and retain ownership of it.
- Give `BrokerMftBlockProducer` and `BrokerIndexWatchSource` access to that same
  process.
- Observe `Ended`, per-drive start results, watch faults, and lost catch-up.
- Dispose indexes before disposing the process during application shutdown.
