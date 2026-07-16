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

The non-elevated process creates a named pipe and page-file-backed memory map, then
launches its own executable with `runas`. The elevated child:

1. captures each volume's USN cursor;
2. scans the MFT;
3. writes scan records to shared memory;
4. reads journal entries produced during the scan; and
5. optionally continues streaming live journal batches over the pipe.

Capturing the cursor before scanning is important: applying the returned catch-up batch
to the scan result produces a current inventory without a scan-to-watch gap.

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
disposes as a single unit, so nothing can hold scan results after the client that
produced them has been disposed. Keep the session alive in one `await using` scope
that covers discovery, watching, and teardown together:

```csharp
await using var session = await JournalBrokerScanSession.StartAsync(
    BrokerLauncher.Launch,
    new[] { "C", "D" },
    cancellationToken);

session.Faulted += reason =>
    Console.Error.WriteLine($"MFT broker stopped: {reason}");

// Discovery: LatestScan is the parked scan-and-catch-up result.
foreach (var (drive, error) in session.LatestScan.Errors)
    Console.Error.WriteLine($"{drive}: {error}");

foreach (var record in session.LatestScan.Records)
    Console.WriteLine(record.Path);

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
there is nothing left to clean up. Run one consumer task per drive returned by
`WatchDriveAsync`; the session owns the pipe reader and routes batches to per-drive
channels, so consumers must not read the pipe directly.

Consumers that only need directory path resolution plus a handful of named files can
request a smaller snapshot on very large volumes while preserving arm/catch-up order.
`keepFileNames` is matched case-insensitively; pass whatever marker file names your
application cares about (for example `.git`):

```csharp
await using var indexSession = await JournalBrokerScanSession.StartAsync(
    BrokerLauncher.Launch,
    new[] { "C", "D" },
    BrokerScanProfile.DirectoryIndex,
    keepFileNames: new[] { ".git" },
    cancellationToken);
```

`LatestScan` (a `BrokerScanResult`) contains:

| Property | Meaning |
| --- | --- |
| `Records` | Full-scan records from all successful drives |
| `ArmedCursors` | Cursors captured before each scan |
| `CatchUpEntries` | Changes recorded while each scan was running |
| `AdvancedCursors` | Resume cursors after catch-up; also the drives `StartWatchAsync` will watch |
| `Errors` | Per-drive failures; one failed drive does not abort the others |

The overload without a `BrokerScanProfile` uses `BrokerScanProfile.Full`, which returns
the complete MFT inventory. The `DirectoryIndex` profile retains every directory plus any
non-directory records whose name matches `keepFileNames` (case-insensitive); use it for
journal path indexing plus a small set of caller-named marker files when a full packed
snapshot could exceed the single-MMF payload limit. `keepFileNames` is ignored under `Full`.

`ScanRecord.Size` and `ScanRecord.LastWriteTicks` are currently zero because those
fields are reserved for a future MFT surface. Use `Name`, `Path`, `Attributes`,
`IsDirectory`, `RecordNumber`, and `ParentRecordNumber` today.

## 3. Stop watching, rescan, and restart

`StopWatchAsync` and `RescanAsync` reuse the same elevated broker; neither triggers a
second UAC prompt.

```csharp
await session.StopWatchAsync();
await session.RescanAsync(cancellationToken);   // same drives, profile, keepFileNames
await session.StartWatchAsync(cancellationToken);
```

`RescanAsync` also has overloads that take a new drive list, or a new drive list plus
profile and `keepFileNames`; each replaces `LatestScan` in place. `RescanAsync` and
`StartWatchAsync` both require the session to be parked (call `StopWatchAsync` first if
currently watching) and throw `InvalidOperationException` otherwise.

## 4. Handle broker death

`IsFaulted` latches once and never reverts; `FaultReason` holds the reason once faulted.
The `Faulted` event fires exactly once, and fires immediately for a handler added after
the fault already happened, so a consumer that only starts watching after discovery
still learns about a death that occurred while parked:

```csharp
if (session.IsFaulted)
{
    Console.Error.WriteLine($"MFT broker stopped: {session.FaultReason}");
    await session.DisposeAsync();
    // Create a new session if the user chooses to reconnect.
}
```

Once faulted, every session operation except queries and `DisposeAsync` throws
`InvalidOperationException` carrying `FaultReason`. Recovery is `DisposeAsync` followed by
a fresh `StartAsync`.

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

- Target .NET 8 and Windows x64.
- Reference the `MFTLib` NuGet package; its transitive build target copies
  `MFTLibNative.dll` to the output directory.
- Publish an executable app host and launch that `.exe`.
- Dispatch `ElevatedEntryPoint.TryHandle` before normal app startup.
- Keep one `JournalBrokerScanSession` per active elevated consumer session.
- Call `StopWatchAsync` before rescanning on that session.
- Dispose the session (`await using` or an explicit `DisposeAsync`) during application shutdown.
- Handle UAC decline, per-drive scan errors, journal invalidation, and broker death (`IsFaulted`/`Faulted`).
