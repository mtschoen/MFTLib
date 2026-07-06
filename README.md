# MFTLib

Fast NTFS MFT (Master File Table) enumeration and USN change-journal library for Windows. Parses MFT records via raw volume access with a native C++ core, multi-threaded parsing, and double-buffered I/O — then keeps up with filesystem changes incrementally via the USN journal.

## Features

- Direct MFT parsing via raw volume access (no Windows Search or `FindFirstFile`)
- Native C++ I/O with parallel fixup, parsing, and path resolution across all CPU cores
- Double-buffered reads to overlap I/O with compute
- Native-side filename filtering (exact match or contains, case-insensitive)
- Native-side full path resolution
- USN change-journal support: query a cursor, batch-read changes, or live-watch as an `IAsyncEnumerable`
- Configurable buffer sizes for tuning memory/performance tradeoffs
- Benchmarked at 2.6M+ records/sec on 8M record synthetic MFT (Ryzen 9 7950X3D, Samsung 990 PRO)

## Requirements

- Windows (NTFS volumes)
- .NET 8.0+
- Administrator privileges (raw volume access)

## Usage

```csharp
using MFTLib;

// Open a volume and search for .git directories
using var volume = MftVolume.Open("C");
var records = volume.FindByName(".git", MatchFlags.ExactMatch | MatchFlags.ResolvePaths, out var timings);

foreach (var record in records.Where(r => r.IsDirectory))
    Console.WriteLine(record.FullPath);

Console.WriteLine($"Found {records.Length} matches in {timings.NativeTotalMs:F0}ms");
```

### Read all records (unfiltered)

```csharp
using var volume = MftVolume.Open("C");
var records = volume.ReadAllRecords(out var timings);
```

### Configure buffer size

```csharp
using var volume = MftVolume.Open("C", bufferSizeRecords: 65536); // 64MB buffers (default is 262144 = 256MB)
var records = volume.ReadAllRecords();
```

## Tracking changes with the USN journal

After a full MFT scan, baseline a cursor and read incremental changes instead of re-scanning. The cursor is durable — persist it and resume later.

```csharp
using var volume = MftVolume.Open("C");

// Baseline after a full scan
var cursor = volume.QueryUsnJournal();

// ... later: catch up on everything that changed since the cursor
var (entries, updatedCursor) = volume.ReadUsnJournal(cursor);
foreach (var entry in entries)
    Console.WriteLine($"{entry.Reason}: {entry.FileName} (record {entry.RecordNumber})");
```

`ReadUsnJournal` throws `InvalidOperationException` if the journal was recreated or entries were overwritten — the signal to fall back to a full MFT rescan.

To watch changes live (blocks on the kernel at zero CPU until changes arrive; cancel the token to stop):

```csharp
using var volume = MftVolume.Open("C");
var cursor = volume.QueryUsnJournal();

await foreach (var (entries, latest) in volume.WatchUsnJournalWithCursor(cursor, cancellationToken))
{
    foreach (var entry in entries.Where(e => e.IsCreate || e.IsDelete))
        Console.WriteLine($"{entry.Reason}: {entry.FileName}");
    // `latest` is the post-batch cursor — persist it to resume after a restart
}
```

`WatchUsnJournal(cursor, token)` is the same stream without the per-batch cursor if you don't need to persist progress.

## Elevated broker (one UAC prompt per session)

MFT scans and USN journal access need Administrator privileges. Rather than elevate the whole
application, a consumer can run the raw-volume work in a single elevated **broker** child process
and keep its own UI/CLI process non-elevated:

- The non-elevated side builds a named pipe, launches the current executable in `--broker` mode
  via `BrokerLauncher.Launch` (a `runas`-elevated relaunch, not waited on), and wraps the connected
  pipe in a `JournalBrokerClient` — see `JournalBrokerClient.SpawnAndConnectAsync`.
- The elevated child dispatches `--broker` through `ElevatedEntryPoint.TryHandle`, which hands off
  to `DefaultElevatedEntryRunner.RunBroker`. That runs a `JournalBrokerHost` (`CreateDefault()` wires
  it to real `MftVolume` access) that serves the session until told to shut down, then exits.
- `JournalBrokerClient.ArmScanAndCatchUpAsync(drives)` requests a cold MFT scan per drive: the
  client pre-creates a page-file-backed `MemoryMappedFile`, the broker arms the journal cursor,
  scans, and writes the packed scan payload into that map; the client reads it back with no disk
  round-trip.
- `SendStartWatchAsync` / `CreateBatchSource` / `StopLiveWatchAsync` drive a live USN journal watch
  over the same pipe and broker process — no second UAC prompt. `BrokerDied` fires once if the pipe
  drops.

Because the broker process is launched once and reused for both the cold scan and the live watch,
a consumer app only ever prompts for elevation once per session. Set `MFTLIB_BROKER_DIAG=1` (or
call `BrokerDiagnostics.Enable(role)` from the elevated child, since `--diag` propagates it across
the `runas` launch) to get frame-level tracing written to `BrokerDiagnostics.LogDirectory`.

## Building from source

Requires Visual Studio 2022 with C++ workload. Always build with MSBuild (not `dotnet build`) since the native C++ DLL cannot be built by the .NET CLI:

```bash
MSBuild.exe MFTLib.sln -p:Configuration=Release -p:Platform=x64
```

## License

[MIT](LICENSE.txt)
