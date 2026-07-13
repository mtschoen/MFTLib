# MFTLib

MFTLib is a .NET library for building fast NTFS file indexes, search tools, backup
catalogs, and filesystem monitors. It reads the Master File Table (MFT) directly for a
high-throughput snapshot, then uses the USN change journal to keep that snapshot current
without repeatedly walking the filesystem.

The public API is managed C#; performance-sensitive volume I/O, record parsing, and path
resolution run in a native C++ core.

## Why MFTLib?

Normal directory enumeration visits the filesystem tree one directory at a time. MFTLib
instead reads NTFS's central record table, which is useful when an application needs to:

- discover files or directories across an entire volume quickly;
- build and maintain a searchable local file index;
- find records by filename without Windows Search;
- correlate full-scan records with later filesystem changes; or
- keep its main process non-elevated while raw-volume work runs in one elevated child.

MFTLib is not a cross-platform filesystem abstraction and does not read file contents.
It is specialized for NTFS metadata on Windows.

## Highlights

- Direct MFT parsing through raw NTFS volume access
- Native C++ I/O with parallel fixup, parsing, and path resolution
- Double-buffered reads that overlap I/O and compute
- Case-insensitive exact and substring filename filtering in native code
- Optional native full-path resolution
- Materialized arrays or lower-allocation streaming enumeration
- USN journal query, catch-up, and cancellable live-watch APIs
- Race-free scan/catch-up workflow through an elevated broker
- One reusable UAC-elevated child per broker session
- Per-drive error isolation and broker-death notification

A synthetic 8-million-record benchmark has exceeded 2.6 million records/second on a
Ryzen 9 7950X3D with a Samsung 990 PRO. Real-volume performance depends on storage,
volume size, filtering, path resolution, and hardware.

## Requirements

- Windows on an NTFS volume
- .NET 8.0 or later
- x64 process architecture
- Administrator access for direct raw-volume and USN operations

The NuGet package includes `MFTLibNative.dll` under `runtimes/win-x64/native` and a
transitive build target that copies it to the consumer's output directory.

## Install

After 0.3.0 is published:

```bash
dotnet add package MFTLib --version 0.3.0
```

Or add a package reference:

```xml
<PackageReference Include="MFTLib" Version="0.3.0" />
```

## Choose an integration model

| Scenario | Recommended API |
| --- | --- |
| Elevated CLI or service; simplest integration | `MftVolume` directly |
| Non-elevated desktop/CLI app; one UAC prompt | `JournalBrokerClient` |
| One-time filename lookup | `MftVolume.FindByName` |
| Full in-memory index | `MftVolume.ReadAllRecords` |
| Process records while native memory is alive | `MftVolume.StreamRecords` |
| Resume from a persisted journal cursor | `MftVolume.ReadUsnJournal` |
| Continuously receive changes | `WatchUsnJournalWithCursor` or broker batches |

## Quick start: find records by name

Run the application as Administrator when using `MftVolume` directly.

```csharp
using MFTLib;

using var volume = MftVolume.Open("C");
var records = volume.FindByName(
    ".git",
    MatchFlags.ExactMatch | MatchFlags.ResolvePaths,
    out var timings);

foreach (var record in records.Where(record => record.IsDirectory))
    Console.WriteLine(record.FullPath);

Console.WriteLine($"Matched {records.Length:N0} records; {timings}");
```

`MftVolume.Open` accepts `"C"`, `"C:"`, or `"C:\\"`.

## Core MFT workflows

### Read a complete volume index

```csharp
using var volume = MftVolume.Open("C");
var records = volume.ReadAllRecords(resolvePaths: true, out var timings);

var byRecordNumber = records.ToDictionary(record => record.RecordNumber);
```

`RecordNumber` and `ParentRecordNumber` are 48-bit MFT segment indexes with the NTFS
sequence number removed. They match the corresponding identifiers on
`UsnJournalEntry`, making them suitable for joining a scan with journal updates on the
same volume.

### Filter in native code

```csharp
var exact = volume.FindByName("report.pdf", MatchFlags.ExactMatch);
var containing = volume.FindByName(
    "report",
    MatchFlags.Contains | MatchFlags.ResolvePaths);
```

`ExactMatch` and `Contains` are case-insensitive. Add `ResolvePaths` only when full paths
are needed; path resolution has additional CPU and memory cost.

Convenience methods are available for exact-name path searches:

```csharp
IEnumerable<string> directories = volume.FindDirectories("node_modules");
IEnumerable<string> files = volume.FindFiles("desktop.ini");
```

### Stream to reduce managed allocations

```csharp
using var result = volume.StreamRecords(
    filter: ".git",
    MatchFlags.ExactMatch | MatchFlags.ResolvePaths);

foreach (var record in result)
{
    // Use the record while result is alive.
    Console.WriteLine(record.FullPath);
}
```

Records yielded directly by `MftResult` can reference native memory owned by the result.
Do not retain them after disposing it unless each record is materialized:

```csharp
var retained = record.Materialize();
```

`ReadAllRecords`, `FindByName`, and `MftResult.ToArray()` return records whose strings
are already materialized into managed memory.

### Tune scan buffers

```csharp
// Number of MFT records per native buffer. Default: 262,144.
using var volume = MftVolume.Open("C", bufferSizeRecords: 65_536);
```

Smaller buffers reduce peak memory use; larger buffers can improve throughput. The
native implementation double-buffers, so budget for more than one record buffer.

## Keep an index current with the USN journal

A durable `UsnJournalCursor` contains the journal instance ID and next USN to read.
Persist both fields together.

For a gap-free direct workflow, capture the cursor before the full scan, then apply the
catch-up entries produced while the scan was running:

```csharp
using var volume = MftVolume.Open("C");

var armedCursor = volume.QueryUsnJournal();
var records = volume.ReadAllRecords(resolvePaths: true);
var (catchUpEntries, currentCursor) = volume.ReadUsnJournal(armedCursor);

ApplyChanges(records, catchUpEntries);
PersistCursor(currentCursor);
```

Later, resume from the persisted cursor:

```csharp
var (entries, updatedCursor) = volume.ReadUsnJournal(persistedCursor);
ApplyChanges(entries);
PersistCursor(updatedCursor);
```

`ReadUsnJournal` throws `InvalidOperationException` if the journal was recreated or the
requested entries were overwritten. Treat that as a request to discard the stale cursor
and perform another full scan/catch-up cycle.

### Watch live changes

```csharp
await foreach (var (entries, cursor) in volume.WatchUsnJournalWithCursor(
    persistedCursor,
    cancellationToken))
{
    foreach (var entry in entries)
        Console.WriteLine($"{entry.Reason}: {entry.FileName}");

    PersistCursor(cursor);
}
```

The watch blocks in the kernel without polling. Cancelling the token calls `CancelIoEx`
to release the pending read. `WatchUsnJournal` provides the same batches without the
post-batch cursor.

USN entries include record and parent IDs, USN, UTC timestamp, reason flags, file
attributes, filename, and convenience flags such as `IsCreate`, `IsDelete`, `IsRename`,
and `IsClose`. A journal entry contains the changed name and parent ID, not an eagerly
resolved full path; maintain an index keyed by record number when full paths are needed.

## Keep the application non-elevated

For desktop applications and long-running tools, use the elevated broker instead of
running the entire process as Administrator. One broker session can arm cursors, scan
multiple drives, catch up changes that occurred during each scan, stream live journal
batches, and rescan without another UAC prompt.

At minimum, the application must dispatch broker mode before normal startup:

```csharp
if (ElevatedEntryPoint.TryHandle(
        Environment.GetCommandLineArgs(),
        new DefaultElevatedEntryRunner()))
{
    return;
}
```

The non-elevated side then creates one client:

```csharp
await using var broker = await JournalBrokerClient.SpawnAndConnectAsync(
    BrokerLauncher.Launch,
    cancellationToken);

var scan = await broker.ArmScanAndCatchUpAsync(
    new[] { "C" },
    cancellationToken);
```

See the [broker integration guide](https://github.com/mtschoen/MFTLib/blob/main/docs/broker-integration.md)
for startup dispatch, result handling, live watch, rescans, recovery, and diagnostics.

## Errors and recovery

Most volume, native parsing, and journal failures surface as `InvalidOperationException`
with the native error message. Common causes include:

- the process is not elevated;
- the target is not an NTFS volume;
- the volume cannot be opened;
- the USN journal is unavailable, recreated, or wrapped; or
- native allocation or path-pool capacity is exhausted.

Broker scans instead collect per-drive failures in `BrokerScanResult.Errors`, allowing
other requested drives to complete. `JournalBrokerClient.BrokerDied` fires at most once
when the pipe closes or fails.

## Building from source

Visual Studio 2022 with the Desktop development with C++ workload and .NET 8 SDK is
required. Build the solution with 64-bit MSBuild; `dotnet build` cannot build the native
C++ project:

```bash
MSBuild.exe MFTLib.sln -p:Configuration=Release -p:Platform=x64
```

Run non-interactive managed coverage with:

```powershell
.\scripts\run-coverage.ps1 -NonInteractive
```

The source is organized by responsibility:

- `MFTLib/Mft` — scans, records, results, filters, paths, and timings
- `MFTLib/Journal` — USN cursor, entries, reasons, and `MftVolume` journal APIs
- `MFTLib/Broker` — elevated host/client, protocol, payload, and diagnostics
- `MFTLib/Elevation` — elevation detection and injectable provider
- `MFTLib/Interop` — native result layouts
- `MFTLib/Internal` — native bindings and internal volume utilities

## License

[MIT](LICENSE.txt)
