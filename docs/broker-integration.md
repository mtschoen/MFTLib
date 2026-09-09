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
before `FileIndex` adopts the block. Rows carry native sizes and modified times; paths use parent rows.

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
| `BlockOutcomes` | Per-drive completed blocks; owned by the session while it holds the result, by the caller when the result came straight from `JournalBrokerClient` |

The default `BrokerScanOptions.Profile` is `BrokerScanProfile.Full`, which returns
the complete MFT inventory. The `DirectoryIndex` profile retains every directory plus any
non-directory records whose name matches `keepFileNames` (case-insensitive); use it for
journal path indexing plus a small set of caller-named marker files to reduce retained
file rows and names. `keepFileNames` is ignored under `Full`.

### Scan options and progress reporting

Cold-scan parameters and progress callbacks are bundled in `BrokerScanOptions`, which can be passed to `JournalBrokerScanSession.StartAsync`, `RescanAsync`, or directly to `JournalBrokerClient.ArmScanAndCatchUpAsync`:

```csharp
var progress = new Progress<BrokerScanProgress>(p =>
{
    Console.WriteLine($"Scanning drive {p.DriveLetter}: {p.RecordsProcessed:N0} records ({p.BytesProcessed / (1024 * 1024):N1} MB) in {p.Elapsed.TotalSeconds:F1}s");
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
`RecordsProcessed == TotalRecords`. Late progress during live watch is discarded.

`IProgress<BrokerScanProgress>.Report` runs synchronously on the pipe-reading task without
UI marshaling. Use `System.Progress<T>` to capture the constructing thread's context or
marshal in your callback. Cancellation stops reporting and surfaces as
`OperationCanceledException` from the scan call; do not wait for a final equality report
after cancellation.

### Sizing the block

Before a cold scan, `JournalBrokerClient` queries the elevated broker for each volume's
MFT geometry, then creates a file-backed named section using the corresponding
`BrokerScanOptions.BlockTargets` destination. Targets are required for every drive.
`MftBlockCapacity.Plan` estimates rows from `MftRecordCount`, with a minimum of 65,536
when information is absent or smaller. Slot capacity adds 25 percent or 65,536 rows,
whichever is larger. The default name estimate is 48 bytes per slot, then name-pool
headroom adds 25 percent or one mebibyte, whichever is larger. A failed volume query
currently uses the minimum estimate; capacity exhaustion marks the block for compaction
and reports skipped records. Treat that as a reason to rescan.

A volume's NTFS geometry and MFT sizing can also be queried directly, without arming a
scan, via `JournalBrokerClient.QueryVolumesAsync` or (already elevated, no broker involved)
`NtfsVolumeInformation.Query`. Note that `QueryVolumesAsync` populates only MFT sizing
(`MftValidDataLength`, `BytesPerFileRecordSegment`, and derived `MftRecordCount`) for block
capacity planning; cluster and sector geometry fields (`BytesPerSector`, `BytesPerCluster`,
`TotalClusters`, `FreeClusters`) are not sent over the broker protocol and are set to zero.

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
and keep-file names. `LatestScan` is then populated and `StartWatchAsync` resumes from the fresh
advanced cursors instead of the originally supplied ones. If starting a live watch with a
cached cursor fails because the journal wrapped or its ID changed, the broker re-queries the
volume's current cursor, fires `session.WarningReceived` (`client.WarningReceived`) with the
drive and warning message, and continues streaming live batches from the fresh cursor position.
Fault latching, terminal-state checks, single-flight operations, and idempotent disposal
are identical to a scanned session.

### Customizing watch cursors: ReplaceWatchCursors and WatchCursors

By default, `StartWatchAsync` watches every drive armed during the latest scan (or supplied at warm start) using its advanced cursor. `ReplaceWatchCursors` lets an application replace the complete watch set while parked - for example, when a user selects or deselects individual drives:

```csharp
// Read back what the session is currently configured to watch:
IReadOnlyDictionary<string, UsnJournalCursor> current = session.WatchCursors;

// Replace with a custom or narrowed set:
session.ReplaceWatchCursors(new Dictionary<string, UsnJournalCursor>
{
    ["C"] = cachedCursorC,
    ["D"] = advancedCursorD
});

await session.StartWatchAsync(cancellationToken);
```

Keys passed to `ReplaceWatchCursors` are normalized (bare letter, case-insensitive) and the call replaces rather than merges the previous set. An empty dictionary is accepted, in which case `StartWatchAsync` will throw `InvalidOperationException` ("No drives to watch"). Keys that do not represent a valid drive letter (e.g. malformed paths, null keys, delimiters, non-letter strings) throw `ArgumentException`.

Note the interaction with rescans: `RescanAsync` overwrites the watch set with its own scan result's `AdvancedCursors`. A consumer that maintains a narrowed or custom drive selection should call `ReplaceWatchCursors` again after each rescan to preserve the selection.

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

## Testing your integration

Reference **`MFTLib.TestExtensions`** to test drives, `BrokerScanProfile`, and keep-file names
without elevation or friend-listing your assembly. Build a fake `JournalBrokerClient`
constructed on an in-memory duplex stream. The client constructor requires a block
section factory immediately after the stream. A Windows test can supply it with
`NamedBlockSection`; here `clientSide` is your connected duplex stream endpoint:

```csharp
using MFTLibTestExtensions;
using MFTLib.Index;

var client = new JournalBrokerClient(clientSide, (drive, options) =>
{
    var sectionName = NamedBlockSection.BuildSectionName(drive[0]);
    var (block, lifetime) = NamedBlockSection.Create(options, sectionName);
    return (sectionName, block, lifetime);
});

await using var session = await ScanSessionTestHarness.StartScannedAsync(
    _ => Task.FromResult(client), drives, new BrokerScanOptions
    {
        BlockTargets = blockTargets,
        Profile = BrokerScanProfile.DirectoryIndex,
        KeepFileNames = keepFileNames
    });
```

`ScanSessionTestHarness.StartScannedAsync` and `StartFromCursorsAsync` mirror the shipping
`JournalBrokerScanSession.StartAsync` / `StartFromCursorsAsync` with an injected client
factory. The harness warm start takes an initial profile but no keep-file names;
rescans supply those in explicit options. The factory must yield a **fresh** client per call - the
session takes exclusive ownership and disposes it. From there, assert on the frames your
test reads off `serverSide` (arm-and-scan drives spec, keep-file names, watch cursors) and
on the session's `LatestScan` and `WatchCursors`. Scanned tests must answer the volume
query and fill the client-created blocks before sending scan-completion frames.

For a portable in-process example, see `MFTLib.Tests/TestSupport/RecordingBlockSectionWriter.cs`
and `InProcessBlockBrokerHarness.cs` in this repository. `RecordingBlockSectionWriter`
implements `IBlockSectionWriter`, resolves section names to test blocks, and stamps an
injected completion timestamp after writing batches. These are repository test helpers,
not types shipped in `MFTLib.TestExtensions`; implement the same seam in your test project.
Warm tests need no block writer until a rescan. Your test assembly needs no
`InternalsVisibleTo` from MFTLib. Disposing the session disposes the blocks still held in
`LatestScan.BlockOutcomes`.

## Low-level primitive: JournalBrokerClient

`JournalBrokerScanSession` is built on `JournalBrokerClient` and is the recommended entry
point for non-elevated consumers. Use `JournalBrokerClient` directly only when your
process is already elevated and you want the pipe/transport primitive without session
ownership; see `JournalBrokerClient.SpawnAndConnectAsync`, `ArmScanAndCatchUpAsync`,
`SendStartWatchAsync`, `CreateBatchSource`, and `StopLiveWatchAsync`. Callers that hold a
`JournalBrokerClient` directly are responsible for the same ordering the session enforces:
arm-before-scan, one pipe reader at a time, and disposing the client exactly once.

## Persisting state and recovery

Persist the post-batch cursor, not the armed cursor. On restart, a direct
`MftVolume.ReadUsnJournal` call can resume from a persisted cursor. If MFTLib reports
that the journal was recreated or overwritten, discard the cursor and perform another
full arm/scan/catch-up cycle (`RescanAsync` on an existing session, or a fresh
`JournalBrokerScanSession.StartAsync`).

Treat `IsFaulted`/`Faulted` or a fault from `WatchDriveAsync` as loss of the elevated
session. Stop consuming batches, mark the affected watches inactive, and start a new
session if the user chooses to reconnect.

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
- Handle UAC decline, per-drive scan errors, journal invalidation, and broker death (`IsFaulted`/`Faulted`).
