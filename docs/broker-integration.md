# Integrating the elevated broker

Master File Table (MFT) and USN journal access require an Administrator volume handle. `JournalBrokerClient`
lets a desktop or CLI application keep its main process non-elevated while one elevated
child performs all raw-volume work for the session.

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

## 2. Spawn one broker client

Keep one client alive for the consumer session:

```csharp
await using var broker = await JournalBrokerClient.SpawnAndConnectAsync(
    BrokerLauncher.Launch,
    cancellationToken);

broker.BrokerDied += reason =>
    Console.Error.WriteLine($"MFT broker stopped: {reason}");
```

`SpawnAndConnectAsync` displays the UAC prompt. It throws `InvalidOperationException`
when launch fails, including when the user declines elevation.

## 3. Scan and catch up

```csharp
var result = await broker.ArmScanAndCatchUpAsync(
    new[] { "C", "D" },
    cancellationToken);

foreach (var (drive, error) in result.Errors)
    Console.Error.WriteLine($"{drive}: {error}");

foreach (var record in result.Records)
    Console.WriteLine(record.Path);

foreach (var (drive, entries) in result.CatchUpEntries)
{
    ApplyChanges(drive, entries);
    PersistCursor(drive, result.AdvancedCursors[drive]);
}
```

The result contains:

| Property | Meaning |
| --- | --- |
| `Records` | Full-scan records from all successful drives |
| `ArmedCursors` | Cursors captured before each scan |
| `CatchUpEntries` | Changes recorded while each scan was running |
| `AdvancedCursors` | Resume cursors after catch-up |
| `Errors` | Per-drive failures; one failed drive does not abort the others |

`ScanRecord.Size` and `ScanRecord.LastWriteTicks` are currently zero because those
fields are reserved for a future MFT surface. Use `Name`, `Path`, `Attributes`,
`IsDirectory`, `RecordNumber`, and `ParentRecordNumber` today.

## 4. Start live watching

Start the shared pipe demultiplexer once, then create one batch source and consume it
for each drive. Each drive stream should have one consumer.

```csharp
await broker.SendStartWatchAsync(result.AdvancedCursors, cancellationToken);
var batches = broker.CreateBatchSource();

await foreach (var (entries, cursor) in batches(
    "C",
    result.AdvancedCursors["C"],
    cancellationToken))
{
    ApplyChanges("C", entries);
    PersistCursor("C", cursor);
}
```

For multiple drives, run one consumer task per drive. The client owns the pipe reader
and routes batches to per-drive channels, so consumers must not read the pipe directly.

## Rescanning without another UAC prompt

Stop the live watch before starting another arm-and-scan operation on the same client:

```csharp
await broker.StopLiveWatchAsync();
var refreshed = await broker.ArmScanAndCatchUpAsync(drives, cancellationToken);
await broker.SendStartWatchAsync(refreshed.AdvancedCursors, cancellationToken);
```

`StopLiveWatchAsync` returns the pipe to scan mode but keeps the elevated process alive.
Disposing the client sends `Shutdown`, closes the pipe, and releases its memory maps.

## Persisting state and recovery

Persist the post-batch cursor, not the armed cursor. On restart, a direct
`MftVolume.ReadUsnJournal` call can resume from a persisted cursor. If MFTLib reports
that the journal was recreated or overwritten, discard the cursor and perform another
full arm/scan/catch-up cycle.

Treat `BrokerDied` or a fault from a drive's batch source as loss of the elevated
session. Stop consuming batches, mark the affected watches inactive, and create a new
client if the user chooses to reconnect.

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
- Keep one `JournalBrokerClient` per active elevated session.
- Call `StopLiveWatchAsync` before rescanning on that client.
- Dispose the client during application shutdown.
- Handle UAC decline, per-drive scan errors, journal invalidation, and broker death.
