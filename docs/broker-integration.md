# Integrating the elevated broker

Master File Table (MFT) and USN journal access require an Administrator volume handle.
`JournalBrokerScanSession` lets a desktop or CLI application keep its main process
non-elevated while one elevated child performs all raw-volume work for the session.

Use the broker when your application:

- has a UI or other code that should not run as Administrator;
- needs a full MFT scan followed by live USN updates;
- scans or rescans multiple volumes without repeated UAC prompts; or
- needs to close the cold-start race between a full scan and journal watching.

For a short tool that already runs elevated, use `MftVolume` directly instead.

## How it works

The non-elevated process creates a named pipe and launches its own executable with
`runas`. `JournalBrokerClient` queries `NtfsVolumeInformation` through that connection,
plans capacities with `MftBlockCapacity`, and creates each block file and named section.
It sends the section name in the scan spec. The elevated child:

1. captures each volume's USN cursor;
2. opens the client-created section and scans the MFT without resolving paths;
3. writes packed rows and names directly as parse batches arrive;
4. reads journal entries produced during the scan; and
5. optionally continues streaming live journal batches over the pipe.

Capturing the cursor before scanning is important: applying the returned catch-up batch
to the scan result produces a current inventory without a scan-to-watch gap.

The broker stamps that armed cursor and writes the completed header last. It then sends
`ScanReady` with `RowCount`, `NamePoolUsedBytes`, and `SkippedRecordCount` (three int64
fields). `RowCount` includes empty slots below the highest written row. A crash before
completion leaves an incomplete block the client discards; rows alone are not evidence
of a successful scan. `BrokerMftBlockProducer` validates the header and armed cursor
before `FileIndex` adopts the block. Rows carry native sizes and modified times, and
their own NTFS sequence number (`RowColumns.SequenceNumber`, read back from a block as
`BlockFile.SequenceNumbers`); paths use parent rows. `FileEntry.Open` combines a row's
sequence number with its record number to open the file by NTFS file id, which detects a
record NTFS has reused for a different file since the row was written.

### The live watch bridge

`MFTLib.Index.FileIndex` owns live watching end to end; nothing in this guide's session
or client API is required to keep an index current. `BrokerMftBlockProducer.CreateWatchSource()`
returns a `BrokerIndexWatchSource` that borrows the connection factory supplied to the
producer, bridging the broker's per-drive live-watch enumerables onto the single merged
stream `FileIndex.StartWatchingAsync` reads. Wire it up next to the producer:

```csharp
var producer = new BrokerMftBlockProducer(connectAsync);
var options = new FileIndexOptions
{
    Drives = drives,
    MftProducer = producer.CreateProducer(),
    WatchSource = producer.CreateWatchSource()
};

await using var index = await FileIndex.OpenAsync(options, cancellationToken);
await index.StartWatchingAsync(cancellationToken);
```

Each drive's watch resumes from the cursor stamped in its own block header, which is the
cursor armed before that drive's cold scan, not the cursor advanced past the scan's own
catch-up entries. Starting the live watch from the armed cursor deliberately replays the
whole scan window, so `BrokerScanResult.CatchUpEntries` is never read by the index: the
live watch subsumes it. A drive whose watch fails is reported on `FileIndex.WatchFaulted`
and `DriveStatus.WatchFailureMessage` without ending any other drive's watch, and
`FileIndex.RescanAsync` disarms, rebuilds, and re-arms that one drive on the running
watch session, leaving every other drive undisturbed.

## 1. Dispatch broker mode before normal startup

The launched executable must recognize MFTLib's `--broker` mode before initializing the
normal application. Put this at the beginning of `Program.cs`:

```csharp
using MFTLib;

if (ElevatedEntryPoint.TryHandle(
        Environment.GetCommandLineArgs(),
        new DefaultElevatedEntryRunner()))
{
    return;
}

// Normal application startup follows.
```

Run the compiled app host (`MyApp.exe`), not `dotnet MyApp.dll`. `BrokerLauncher`
relaunches the current executable, so the current process must be the application
executable that contains this dispatch code.

## 2. Run one scan-to-watch session

`JournalBrokerScanSession` owns one elevated `JournalBrokerClient` for the whole
consumer session: it spawns the broker, scans, discovers, watches, rescans, and
disposes as a single unit. The session owns the completed blocks in
`LatestScan.BlockOutcomes`: a rescan disposes the blocks of the result it replaces, and
disposing the session disposes the blocks of the result it still holds. Take a block out
of the result first if it has to outlive either. Keep the session alive in one
`await using` scope that covers
discovery, watching, and teardown together. In the examples below, `blockTargets` is an
`IReadOnlyDictionary<string, BlockScanTarget>` with a destination path, volume serial,
and delete-on-close choice for each requested drive. For example, given an existing
`cacheDirectory` and the discovered volume serials:

```csharp
var blockTargets = new Dictionary<string, BlockScanTarget>
{
    ["C"] = new(Path.Combine(cacheDirectory, $"C-{volumeSerialC:X8}.mlix"), volumeSerialC, false),
    ["D"] = new(Path.Combine(cacheDirectory, $"D-{volumeSerialD:X8}.mlix"), volumeSerialD, false)
};
```

```csharp
await using var session = await JournalBrokerScanSession.StartAsync(
    BrokerLauncher.Launch,
    new[] { "C", "D" },
    new BrokerScanOptions { BlockTargets = blockTargets },
    cancellationToken);

session.Faulted += reason =>
    Console.Error.WriteLine($"MFT broker stopped: {reason}");

// Discovery: LatestScan is the parked scan-and-catch-up result.
foreach (var (drive, error) in session.LatestScan.Errors)
    Console.Error.WriteLine($"{drive}: {error}");

// Live watch: one StartWatchAsync, then one WatchDriveAsync consumer per drive.
await session.StartWatchAsync(cancellationToken);

async Task WatchDriveAsync(string drive)
{
    await foreach (var (entries, cursor) in session.WatchDriveAsync(drive, cancellationToken))
    {
        ApplyChanges(drive, entries);
        PersistCursor(drive, cursor);
    }
}

var watchTasks = session.LatestScan.AdvancedCursors.Keys.Select(WatchDriveAsync);
await Task.WhenAll(watchTasks);
```

`StartAsync` displays the UAC prompt via `launchBroker` (typically `BrokerLauncher.Launch`).
It throws `InvalidOperationException` if the broker declines to launch or dies before the
initial scan completes; the session is fully disposed before the exception is thrown, so
there is no session left to clean up. The broker writes bounded batches directly into
each packed block. Successful blocks are published on `LatestScan.BlockOutcomes` and are
disposed by the next rescan or by the session's own disposal, whichever comes first.

Run one consumer task per drive returned by `WatchDriveAsync`; the session owns the pipe
reader and routes batches to per-drive channels, so consumers must not read the pipe directly.

`LatestScan` (a `BrokerScanResult?`) is always populated after `StartAsync` and after any
`RescanAsync`; it is `null` only on a warm session that has not yet rescanned (see Warm
start below). It contains:

| Property | Meaning |
| --- | --- |
| `ArmedCursors` | Cursors captured before each scan |
| `CatchUpEntries` | Changes recorded while each scan was running |
| `AdvancedCursors` | Resume cursors after catch-up; also the drives `StartWatchAsync` will watch |
| `Errors` | Per-drive failures; one failed drive does not abort the others |
| `BlockOutcomes` | Per-drive completed blocks; owned by the session, or the caller for a bare result |

The default `BrokerScanOptions.Profile` is `BrokerScanProfile.Full`, which returns
the complete MFT inventory. The `DirectoryIndex` profile retains every directory plus any
non-directory records whose name matches `keepFileNames` (case-insensitive); use it for
journal path indexing plus a small set of caller-named marker files to reduce retained
file rows and names. `keepFileNames` is ignored under `Full`.

### Scan options and progress reporting

Cold-scan parameters and progress callbacks are bundled in `BrokerScanOptions`, which can
be passed to `JournalBrokerScanSession.StartAsync`, `RescanAsync`, or directly to
`JournalBrokerClient.ArmScanAndCatchUpAsync`:

```csharp
var progress = new Progress<BrokerScanProgress>(p =>
{
    Console.WriteLine(
        $"Scanning drive {p.DriveLetter}: {p.RecordsProcessed:N0} records " +
        $"({p.BytesProcessed / (1024 * 1024):N1} MB) in {p.Elapsed.TotalSeconds:F1}s");
});

await using var session = await JournalBrokerScanSession.StartAsync(
    BrokerLauncher.Launch,
    new[] { "C", "D" },
    new BrokerScanOptions
    {
        Profile = BrokerScanProfile.DirectoryIndex,
        KeepFileNames = new[] { ".git" },
        Progress = progress,
        BlockTargets = blockTargets
    },
    cancellationToken);
```

Progress arrives as `BrokerFrameKind.ScanProgress` (frame kind 11), carrying `DriveLetter`,
`Phase` (`BrokerScanPhase.Parsing` or `Transferring`), `RecordsProcessed`, `BytesProcessed`,
`TotalRecords`, `TotalBytes`, and `Elapsed`. The host throttles reports to every 250ms per
drive and flushes the newest pending report at completion. Counts never decrease within
a phase. Immediately before `ScanReady`, the final `Transferring` report has
`RecordsProcessed == TotalRecords`. Late progress during live watch is discarded. A
`FileIndex` reads the same shape through `IndexScanProgress`: `BrokerProgressAdapter`
maps `BrokerScanPhase` onto `IndexScanPhase.ParsingMft`/`Transferring` and forwards each
sample to `FileIndexOptions.Progress`, the same progress type `EnumerationProducer`
reports on with `IndexScanPhase.Enumerating`.

`IProgress<BrokerScanProgress>.Report` runs synchronously on the pipe-reading task without
UI marshaling. Use `System.Progress<T>` to capture the constructing thread's context or
marshal in your callback. Cancellation stops reporting and surfaces as
`OperationCanceledException` from the scan call; do not wait for a final equality report
after cancellation.

See [sizing blocks and customizing watch cursors](broker-scan-tuning.md) for
`MftBlockCapacity` planning and direct volume-geometry queries.

### Warm start: resume watching from persisted cursors without a scan

A consumer that already holds its own cached inventory and a persisted resume cursor per
drive can skip the cold scan entirely and go straight to watching, letting the kernel
replay the gap since the persisted cursor. `StartFromCursorsAsync` spawns the same
elevated broker (one UAC prompt) but performs no arm-and-scan; the session parks on the
supplied cursors, and `StartWatchAsync` resumes each drive from them:

```csharp
IReadOnlyDictionary<string, UsnJournalCursor> persisted = LoadPersistedCursors();

await using var session = await JournalBrokerScanSession.StartFromCursorsAsync(
    BrokerLauncher.Launch,
    persisted,
    cancellationToken);

await session.StartWatchAsync(cancellationToken);
foreach (var drive in persisted.Keys)
    _ = ConsumeDriveAsync(drive); // one WatchDriveAsync consumer per drive, as in section 2
```

A cursor whose `JournalId` is 0 is the "watch from current position" sentinel: the broker
resolves the drive's current cursor and watches from now, losing only the pre-launch gap
for that drive. Use it for a drive you want to watch but have no persisted cursor for.

Because no scan ran, `LatestScan` is `null` until the first `RescanAsync`. A warm session
rescans on the same broker using explicit `BrokerScanOptions` with block targets, profile,
and keep-file names. `LatestScan` is then populated and `StartWatchAsync` resumes from the
fresh advanced cursors instead of the originally supplied ones.

If starting a live watch with a cached cursor fails because the journal wrapped or its ID
changed, the broker ends that drive's stream with an `Error` frame whose message names
the stale cursor and the required rescan. `WatchDriveAsync` throws for that drive while
the other drives keep streaming. Scan warnings arrive on `BrokerScanResult.Warnings`.
Rescan the failed drive before arming it again. Fault latching, terminal-state checks,
single-flight operations, and idempotent disposal are identical to a scanned session.

See [sizing blocks and customizing watch cursors](broker-scan-tuning.md) for
`ReplaceWatchCursors`/`WatchCursors`, which replace the drive set a parked session
watches, for example when a user selects or deselects individual drives.

## 3. Stop watching, rescan, and restart

`StopWatchAsync` and `RescanAsync` reuse the same elevated broker; neither triggers a
second UAC prompt.

```csharp
await session.StopWatchAsync();
await session.RescanAsync(new BrokerScanOptions
{
    BlockTargets = blockTargets,
    Profile = BrokerScanProfile.DirectoryIndex,
    KeepFileNames = new[] { ".git" }
}, cancellationToken);   // same drives, explicit scan options
await session.StartWatchAsync(cancellationToken);
```

`RescanAsync` also accepts a new drive list plus `BrokerScanOptions`; both overloads
replace `LatestScan` in place, disposing the blocks of the result they replace, so supply
destinations suitable for the new scan and take any block you still need out of the old
result before rescanning. `RescanAsync` and
`StartWatchAsync` both require the session to be parked (call `StopWatchAsync` first if
currently watching) and throw `InvalidOperationException` otherwise.

## 4. Handle broker death

`IsFaulted` latches once and never reverts; `FaultReason` holds the reason once faulted.
The `Faulted` event fires exactly once, and fires immediately for a handler added after
the fault already happened, so a consumer that only starts watching after discovery
still learns about a death that occurred while parked.

Once faulted, every session operation except queries and `DisposeAsync` throws
`InvalidOperationException` carrying `FaultReason`. Recovery is `DisposeAsync` followed by
a fresh `StartAsync`.

Broker death surfaces on two channels at once: the `Faulted` event (with the
`IsFaulted`/`FaultReason` latch) fires, and every in-flight `WatchDriveAsync` enumerable
throws `InvalidOperationException`. These are redundant by design - either alone is enough
to detect the death - but a consumer running one watch task per drive under a
`Task.WhenAll` will still see that `WhenAll` throw, so handle both surfaces in one place
rather than letting the per-drive exception escape as an unobserved fault. Catch a
broker-death `InvalidOperationException` (and the normal `OperationCanceledException` on a
cancelled shutdown) around the aggregate await and route both through the same teardown
the `Faulted` handler runs:

```csharp
try
{
    await Task.WhenAll(watchTasks);
}
catch (Exception exception) when (exception is OperationCanceledException || session.IsFaulted)
{
    // Broker died (or the watch was cancelled): the Faulted latch already holds the
    // reason. Stop consuming, mark watches inactive, and reconnect if the user chooses.
}
```

For testing a consumer of this session and the `FileIndex` watch bridge without
elevation, see [testing your integration](broker-testing.md).

## Low-level primitive: JournalBrokerClient

`JournalBrokerScanSession` is built on `JournalBrokerClient` and is the recommended entry
point for non-elevated consumers. Use `JournalBrokerClient` directly only when your
process is already elevated and you want the pipe/transport primitive without session
ownership; see `JournalBrokerClient.SpawnAndConnectAsync`, `ArmScanAndCatchUpAsync`,
`SendStartWatchAsync`, `CreateBatchSource`, and `StopLiveWatchAsync`. Callers that hold a
`JournalBrokerClient` directly are responsible for the same ordering the session enforces:
arm-before-scan, one pipe reader at a time, and disposing the client exactly once.

The first `SendStartWatchAsync` call starts the live generation and arms every drive it
names. Later calls add or re-arm only their named drives and leave the others running.
`SendDisarmDriveAsync` retires one drive and completes its current batch source normally;
`StopLiveWatchAsync` ends the complete generation. The host handles control frames in
order and awaits an existing drive task before starting its replacement, so one drive
never has two host tasks writing frames at once.

Each `SendStartWatchAsync` arms every named drive under a fresh per-drive arm epoch
carried in its `StartWatch` token (`letter:journalId:nextUsn:armEpoch`). The broker tags
every live `JournalBatch` and per-drive `Error` with the epoch of the arm that produced
it. The client delivers a frame only while that is still the drive's current epoch,
discarding batches and failures produced before a disarm or re-arm instead of delivering
them into the replacement channel. `DisarmDrive` has no acknowledgement and needs none.

`BrokerIndexWatchSource` (see [the live watch bridge](#the-live-watch-bridge) above)
builds on this primitive rather than replacing it: it is a `JournalBrokerClient` consumer
like any other, merging every drive's `SendStartWatchAsync`/`CreateBatchSource` output
into the single stream `IIndexWatchSource` declares.

## Persisting state and recovery

Persist the post-batch cursor, not the armed cursor. On restart, a direct
`MftVolume.ReadUsnJournal` call can resume from a persisted cursor. If MFTLib reports
that the journal was recreated or overwritten, discard the cursor and perform another
full arm/scan/catch-up cycle (`RescanAsync` on an existing session, or a fresh
`JournalBrokerScanSession.StartAsync`).

Treat `IsFaulted`/`Faulted` as loss of the elevated session. A fault from `WatchDriveAsync`
ends only that drive's watch: mark it inactive, keep consuming the other drives, and rescan it
before arming it again.

## Diagnostics

Set the log directory before enabling diagnostics:

```csharp
BrokerDiagnostics.LogDirectory = appDataDirectory;
BrokerDiagnostics.Enable("client");
```

Alternatively, set `MFTLIB_BROKER_DIAG=1` before spawning the client. MFTLib propagates
`--diag` across the `runas` boundary because environment inheritance is not reliable
for elevated launches. Both processes append frame and event traces to
`broker-diagnostics.log` in `BrokerDiagnostics.LogDirectory`.

Diagnostics are best-effort and disabled by default.

## Deployment checklist

- Target .NET 10 and Windows x64.
- Reference the `MFTLib` NuGet package; its transitive build target copies
  `MFTLibNative.dll` to the output directory.
- Publish an executable app host and launch that `.exe`.
- Dispatch `ElevatedEntryPoint.TryHandle` before normal app startup.
- Keep one `JournalBrokerScanSession` per active elevated consumer session.
- Call `StopWatchAsync` before rescanning on that session.
- Dispose the session (`await using` or an explicit `DisposeAsync`) during application shutdown.
- Handle UAC decline, per-drive scan errors, journal invalidation, and broker death
  (`IsFaulted`/`Faulted`).
