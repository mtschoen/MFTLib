# Integrating the elevated broker

Master File Table (MFT) and USN journal access require an Administrator volume
handle. `BrokerSession` lets a desktop or CLI application keep its main process
non-elevated while one elevated child performs raw-volume work for the consumer
session.

Use the broker when your application:

- has a UI or other code that should not run as Administrator;
- opens a `FileIndex` over MFT-backed drives;
- scans, watches, or rescans several volumes after one UAC prompt; or
- needs scan and watch failures isolated to the drive that produced them.

For a short tool that already runs elevated, use `MftIndexSources.FromLocalVolumes`.

This walkthrough wires the consumer application. [Broker lifetime](broker-lifetime.md)
defines process/channel isolation, heartbeats and stall handling. Use
[Testing your integration](broker-testing.md) for the in-process test harness.

## 1. Dispatch broker mode before normal startup

The launched executable must recognize MFTLib's broker mode before initializing
the normal application. Put this at the beginning of `Program.cs`:

```csharp
using MFTLib;

if (ElevatedEntryPoint.RunBrokerIfRequested(Environment.GetCommandLineArgs()))
{
    return;
}

// Normal application startup follows.
```

Run the compiled app host (`MyApp.exe`), not `dotnet MyApp.dll`.
The session relaunches the current executable, so the current process must
be the application executable that contains this dispatch code.

## 2. Launch and own one broker with BrokerSession

Create one `BrokerSession` for the lifetime of the indexes that use it. It owns the elevated
process: it launches the broker on first use, reports its end and disposes it.

```csharp
await using var session = new BrokerSession();

session.Connecting += () => ShowStatus("Requesting elevation...");
session.Connected += () => ShowStatus("Elevated broker connected.");

_ = session.Ended.ContinueWith(
    ended => Console.Error.WriteLine($"MFT broker ended: {ended.Result}"),
    TaskScheduler.Default);
```

`new BrokerSession()` relaunches the executable and waits up to 30 seconds for the
elevated child to connect. `new BrokerSession(launchBroker, connectTimeout)` takes the launch
callback (it receives the broker command line and returns false when the UAC prompt was declined)
and optionally a different connection timeout. Nothing launches at construction: the first scan,
watch or `GrowUsnJournalAsync` starts the launch, so the UAC prompt appears when the broker is
first needed.

See [session ownership](broker-lifetime.md#session-ownership) for shared launch,
cancellation, subscriber exceptions, `Ended` and disposal rules.

`BrokerSession.GrowUsnJournalAsync(drive, target, cancellationToken)`
launches the broker if no use has yet, grows the journal, and returns the settings read back
from the volume. `target` is a `UsnJournalSettings` carrying the requested maximum size and allocation delta.

Consumers create index sources only through `BrokerSession.CreateIndexSource(scanOptions)`.
Application code owns a `BrokerSession`; test sessions use
`BrokerTestHarness.CreateSession` with an `InProcessBrokerHandle` launch callback.

## 3. Build FileIndex over the broker

`BrokerSession.CreateIndexSource()` adapts broker scans to `FileIndex`. Its watch source
uses the same session and opens a separate broker channel for every drive watch.

Given an `IReadOnlyList<IndexedDrive>` named `drives` and an application-owned
cache directory named `cacheDirectory`:

```csharp
using MFTLib;
using MFTLib.Index;

var options = new FileIndexOptions
{
    Drives = drives,
    CacheDirectory = cacheDirectory,
    MftSource = session.CreateIndexSource(new MftScanOptions
    {
        RetainedFileNames = null // Full scan; [] keeps directories only.
    })
};

await using var index = await FileIndex.OpenAsync(options, cancellationToken);
```

Choose and prepare the directory using [block ownership](block-ownership.md#preparing-the-cache-directory).
Tests use [owned temporary cache and isolated journal state](broker-testing.md#isolate-cache-and-journal-state).

`BrokerSession.CreateIndexSource()` returns the `MftIndexSource` that
`FileIndexOptions.MftSource` takes. The source carries both halves of the
connection: the block producer for cold opens and rescans, and the watch
source, implemented by an internal MFTLib type. Each drive watch start
connects through the session, opens the drive's pipe, writes its watch
request, and returns the drive's running watch. The watch source, the drive
watch handle and the block producer are internal to MFTLib; a consumer holds
only the opaque `MftIndexSource`.

A process that cannot scan assigns `MftIndexSources.Unavailable(reason)`
instead. Every scan of an MFT-backed drive then fails with
`Drive {letter}: {reason}.`, reported as `DriveFailureKind.ProducerFailed`,
and a watch start reports `NotApplicable` without recording a watch
request. Cached blocks still open.

The optional `MftScanOptions.RetainedFileNames` selects retention: null keeps
all eligible records, an empty collection keeps directories only, and a nonempty
collection keeps directories plus files whose names match ordinally, ignoring case.
`IncludeFreed` controls freed-record inclusion independently. Scan progress reaches the application as `IndexScanProgress`
through `FileIndexOptions.ScanProgress`. A block that fails validation is disposed
and its scan fails; a block that passes transfers to the index.

### Mixed producers and drive failures

`IndexedDrive.Producer` chooses each drive's producer. `ProducerKind.Mft`, the
default and what `IndexedDrive.FromWindowsVolume` produces, scans through `MftSource`.
`ProducerKind.Enumeration` walks the directory tree from the drive's `RootDirectory`, so one
index can mix both:

```csharp
var drives = new[]
{
    IndexedDrive.FromWindowsVolume('C'),
    new IndexedDrive('E', enumerationRoot, enumerationVolumeSerial, ProducerKind.Enumeration)
};
```

A failed MFT scan never falls back to a directory walk. Enumeration drives accept a null
`MftSource` and report `Watch.IsSupported == false`; a watch start reports `NotApplicable` for
them, and `RescanAsync` refreshes them.

A drive that settles `DriveState.Failed` has no block, and `DriveStatus.Failure.Kind` says why:

| Kind | Meaning |
| --- | --- |
| `CacheDeclined` | `CacheMode.ExistingOnly` found no usable cached block, or one from another producer, and forbade a scan. |
| `CacheTagMismatch` | `CacheMode.ExistingOnly` found a block whose cache tag differs from `FileIndexOptions.CacheTag`. |
| `InUse` | `CacheMode.ExistingOnly` found the block owned by another live `FileIndex`. |
| `ProducerFailed` | The scan failed, or an MFT drive needed a scan and had no `MftSource`. |

Every kind accepts a per-drive `RescanAsync`, which clears `Failure` on success. A failed
rescan, or a failed catch-up retry scan during `OpenAsync`, can instead leave a drive `Ready`
with its previous block and a `ProducerFailed` failure until a later scan succeeds.

For implementation details, see [scan protocol](broker-lifetime.md#scan-protocol)
and [block sizing and parse threads](broker-scan-tuning.md). The concurrent-open
and terminal scan-progress contracts are in [watch lifetime](watch-lifetime.md).

## 4. Start and stop per-drive watches

Each lifecycle verb accepts `IEnumerable<DriveKey>`, fans out concurrently and returns one
`DriveOperationResult` per requested drive. Use the current status keys for all drives:

```csharp
var drives = index.Drives.Select(status => status.Drive).ToArray();
var starts = await index.StartWatchingAsync(drives, cancellationToken);
foreach (var result in starts)
{
    if (result.Outcome == DriveOperationOutcome.Failed)
    {
        Console.Error.WriteLine(
            $"Drive {result.Drive} did not start: {result.Failure}");
    }
}

var catchUps = await index.WaitForCatchUpAsync(drives, cancellationToken);
```

Use the same drive selection for `StopWatchingAsync` and `RescanAsync`. See
[watch lifetime](watch-lifetime.md) for admission, start readiness, catch-up waits,
fault consumption and block replacement.

## 5. Handle faults and recovery

Subscribe before starting watches:

```csharp
index.WatchFaulted += fault =>
{
    var status = index.Drives.Single(
        drive => drive.Drive == fault.Drive);

    Console.Error.WriteLine(
        $"Drive {fault.Drive}: {fault.Kind}, {status.Watch.State}, " +
        $"automatic recovery: {fault.IsRecovering}");
};
```

Use [watch lifetime](watch-lifetime.md) to interpret fault kinds and recovery state,
[session ownership](broker-lifetime.md#session-ownership) for a completed `Ended`,
and [checkpoint loss](checkpoint-loss.md) for `JournalCatchUpLostException`.

To follow state without polling, subscribe to `WatchStateChanged`, then seed from
`Drives` through the same version check:

```csharp
var gate = new Lock();
var applied = new Dictionary<DriveKey, long>();
void ApplyWatchState(DriveKey driveLetter, DriveWatchStatus watch)
{
    // The check and the publication share one lock, so an older event
    // delivered concurrently can never publish after a newer one.
    lock (gate)
    {
        if (applied.TryGetValue(driveLetter, out var version) && version >= watch.StateVersion)
        {
            return;
        }

        applied[driveLetter] = watch.StateVersion;
        Publish(driveLetter, watch.State);
    }
}
index.WatchStateChanged += change => ApplyWatchState(change.Drive, change.Watch);
foreach (var status in index.Drives)
{
    ApplyWatchState(status.Drive, status.Watch);
}
```

The snapshot, state-version and callback-reentrancy rules are in
[watch lifetime](watch-lifetime.md). Queue lifecycle work to run after the handler returns.

## 6. Recover from a lost scan catch-up

Read the drive's `DriveWatchStatus.CheckpointLoss` using
[checkpoint loss](checkpoint-loss.md) and its [growth-target rules](checkpoint-loss.md#journal-growth-target).
[Lost scan catch-up](broker-scan-tuning.md#lost-scan-catch-up) describes the tuning
workflow. After the user chooses to grow the journal, use the session and rescan:

```csharp
if (loss.TryGetGrowthTarget(out var target))
{
    var current = index.QueryUsnJournalSettings(driveLetter);
    if (target.MaximumSize > current.MaximumSize)
    {
        await broker.GrowUsnJournalAsync(driveLetter, target, cancellationToken);
        var results = await index.RescanAsync([driveLetter], cancellationToken);
        foreach (var result in results.Where(result => result.Failure is not null))
        {
            Console.Error.WriteLine($"Rescan {result.Drive}: {result.Failure}");
        }
    }
}
```

If watching has not been requested, follow the successful rescan with
`StartWatchingAsync([driveLetter], cancellationToken)`.

## 7. Growing a journal

See [control requests](broker-lifetime.md#control-requests) for growth cancellation,
write completion and reply timeouts, and [sizing the block](broker-scan-tuning.md#sizing-the-block)
for the producer's internal volume query.

## Diagnostics

Configure diagnostics after the early `ElevatedEntryPoint.RunBrokerIfRequested` dispatch returns
false. Create the application-owned directory, then enable diagnostics in one call:

```csharp
Directory.CreateDirectory(appDataDirectory);
BrokerDiagnostics.Enable("client", appDataDirectory);
```

See [diagnostics configuration](broker-lifetime.md#diagnostics-configuration) for
environment opt-in, role and child-path validation, drop notices and journal self-filtering.

## Deployment checklist

- Target .NET 10 and Windows x64.
- Reference the `MFTLib` NuGet package; SDK-style consumers resolve
  `MFTLibNative.dll` through the package's runtime assets.
- Publish an executable app host and launch that `.exe`.
- Dispatch `ElevatedEntryPoint.RunBrokerIfRequested` before normal app startup.
- Create one `BrokerSession` for the consumer session and retain ownership of it.
- Assign `session.CreateIndexSource()` to every index of the session, so all drives share
  the one process.
- Observe `Ended`, per-drive start results, watch faults, and lost catch-up.
- Dispose indexes before disposing the session during application shutdown.
