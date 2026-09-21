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
- Runtime detection of the volume's MFT record size (1024 or 4096 bytes) instead of an
  assumed fixed size
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
- .NET 10.0 or later
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

## Pre-release consumption (Gitea submodule & CI recipe)

While 0.3.0 is unpublished, consumers of MFTLib (such as `file-wizard` and `git-wizard`) build it from source through a git submodule:

1. **Submodule convention**: Declare MFTLib as a submodule whose url resolves to `https://gitea.fleet.sticktoitive.net/schoen/MFTLib.git`. Both consumers declare it at `external/MFTLib` with the relative url `../MFTLib.git`, which keeps the submodule on the same Gitea instance and under the same owner as the consumer. The gitlink is the pin: the commit sha recorded at that path is the MFTLib revision the consumer builds, and it is the only place that revision is stored.
2. **Automated fan-out**: On push to `main` in MFTLib, `.gitea/workflows/sync-consumers.yml` executes `scripts/sync_consumers.sh`, enumerates `schoen/*` repos on Gitea, and opens a `chore/mftlib-pin-bump` pull request as the `claude-code` bot (backed by the `MFTLIB_SYNC_TOKEN` Actions secret) in every repo whose `.gitmodules` declares a submodule resolving to MFTLib. That pull request commits the new sha into the gitlink. The submodule path is read from `.gitmodules` rather than assumed. A repo with no such submodule is not a consumer and is skipped.
3. **A silent no-op is a failure**: a fan-out that matched zero consumers exits non-zero instead of reporting success, and so does one where any single consumer failed to bump. A green run that updated nothing is what let both consumers drift four MFTLib pull requests behind (issue #194).
4. **Local development**: Populate the submodule with `git submodule update --init --recursive`. Do this after a `git clean -ffxd`, which removes the checked-out submodule content along with every other untracked file.
5. **Post-0.3.0 NuGet transition**: Once 0.3.0 is published on NuGet, consumers drop the submodule and replace it with a standard `<PackageReference Include="MFTLib" Version="0.3.0" />` (automated package reference updates are planned for a future iteration of the fan-out workflow).

### Consumer CI recipe

Check the submodule out as part of the build instead of cloning MFTLib separately, so the revision CI builds is exactly the gitlink the repository pins:

```yaml
- uses: actions/checkout@v4
  with:
    submodules: recursive
```

MFTLib source then sits at `external/MFTLib`. Build the native core with the toolchain that can compile `MFTLibNative.vcxproj`, and the managed assemblies with `dotnet`.

#### Bash (Linux CI)

```bash
# Native core, plus its smoke test, driven by MFTLib's own Linux build script
# (cmake + Ninja into external/MFTLib/build/linux, which MFTLib gitignores).
bash external/MFTLib/scripts/build-linux.sh

# Managed assemblies
dotnet build external/MFTLib/MFTLib/MFTLib.csproj -c Release -p:Platform=x64
dotnet build external/MFTLib/MFTLibTestExtensions/MFTLibTestExtensions.csproj -c Release -p:Platform=x64
```

#### PowerShell (Windows CI)

```powershell
# VS MSBuild is the only toolchain that can compile MFTLib's native C++
# vcxproj. Install it x64 (microsoft/setup-msbuild@v2 with
# msbuild-architecture: x64) so a host-mode runner does not WOW64-redirect it.

# Restore first: VS MSBuild does not auto-restore SDK-style projects.
dotnet restore

msbuild external\MFTLib\MFTLibNative\MFTLibNative.vcxproj -t:Build -p:Configuration=Release -p:Platform=x64 -nologo -v:minimal
dotnet build external\MFTLib\MFTLib\MFTLib.csproj -c Release -p:Platform=x64 --no-restore
dotnet build external\MFTLib\MFTLibTestExtensions\MFTLibTestExtensions.csproj -c Release -p:Platform=x64 --no-restore
```

## Choose an integration model

| Scenario | Recommended API |
| --- | --- |
| Elevated CLI or service; simplest integration | `MftVolume` directly |
| Non-elevated desktop/CLI app; one UAC prompt | `JournalBrokerScanSession` (owned scan-to-watch session; `JournalBrokerClient` remains available as the low-level primitive) |
| One-time filename lookup | `MftVolume.FindByName` |
| Full in-memory index | `MftVolume.ReadAllRecords` |
| Process records while native memory is alive | `MftVolume.StreamRecords` |
| Resume from a persisted journal cursor | `MftVolume.ReadUsnJournal` |
| Continuously receive changes | `WatchUsnJournalWithCursor` or broker batches |
| Explain a rescan forced by the change journal | `DriveStatus.CheckpointLoss` |

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
same volume. Each record's own sequence number is carried separately on
`MftRecord.SequenceNumber` and `UsnJournalEntry.SequenceNumber`; combined with the record
number as `(sequenceNumber << 48) | recordNumber`, it forms the NTFS file reference that
detects an MFT record NTFS has since reused for a different file.

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

A watched volume whose change journal is too small wraps under load: records
are overwritten before the watch reads them, the watch faults, and the drive
needs a rescan. Windows sizes a new journal at 32 MB, which a busy system
drive can wrap in minutes.

`FileIndex.QueryUsnJournalSettings(driveLetter)` reports a volume's configured
sizing, `MaximumSize` and `AllocationDelta`, without elevation.

### When a rescan happened because the journal moved on

The only journal event that costs anything is the checkpoint in a drive's cached
block falling out of the journal, because the drive must then be scanned from
scratch instead of caught up. When opening a drive finds that, it cold-scans and
records what it found on that drive's status:

```csharp
await using var index = await FileIndex.OpenAsync(options, CancellationToken.None);

foreach (var drive in index.Drives)
{
    if (drive.CheckpointLoss is not { } loss)
    {
        continue;
    }

    switch (loss.Cause)
    {
        case JournalCheckpointLossCause.CheckpointTrimmed when loss.SizeThatWouldHaveRetained is { } size:
            Console.WriteLine(
                $"Drive {loss.DriveLetter}: the last checkpoint was {loss.BytesBehind} bytes " +
                $"older than the journal still holds, so a full rescan was needed. A journal " +
                $"of at least {size} bytes (it is {loss.MaximumSize} now) would have kept the " +
                "checkpoint.");
            break;
        case JournalCheckpointLossCause.CheckpointTrimmed:
            Console.WriteLine(
                $"Drive {loss.DriveLetter}: the last checkpoint was {loss.BytesBehind} bytes " +
                "older than the journal still holds, so a full rescan was needed. No size to " +
                "offer: it does not fit in a long.");
            break;
        default:
            Console.WriteLine(
                $"Drive {loss.DriveLetter}: the change journal was recreated, so the last " +
                "checkpoint no longer refers to anything and a full rescan was needed.");
            break;
    }
}
```

USNs are byte offsets into the journal, so every number there is exact integer
arithmetic on values read off the volume: `BytesBehind` is `FirstUsn` minus the
checkpoint, and `SizeThatWouldHaveRetained` is `NextUsn` minus the checkpoint,
rounded up to the journal's allocation delta, plus one more allocation delta.
That margin comes from NTFS's documented trimming behavior in
`CREATE_USN_JOURNAL_DATA` and `USN_JOURNAL_DATA`: the maximum is a target and a
trim can leave the journal below it. The value is not a live measurement, and
there is no clock, rate or cap.

The report is attached to the drive it describes, for as long as the block it
explains is in place. A `FileIndexOptions.InitialOpenCacheOnly` open reports it
too: that drive comes back `DriveState.Failed` with
`DriveFailureKind.CacheDeclined`, which says the cache was refused, while
`CheckpointLoss` says why and the size a journal would need to be at least to
have kept the checkpoint. A successful
`RescanAsync` clears it, because the block it explained has been replaced.

`Cause` separates the two situations MFTLib can actually tell apart.
`CheckpointTrimmed` means the journal is the one the checkpoint came from and has
trimmed past it, so `SizeThatWouldHaveRetained` says the size a journal would
need to be at least to have kept the checkpoint, when that size fits in a
`long`; it is null when it does not. `JournalRecreated` means the journal
was deleted and recreated and carries a different id, so the checkpoint refers to
a journal that no longer exists: no size would have helped, and none is offered.
A consumer decides what to say from `Cause`, not from whether the size is null,
since both causes can leave it null. A drive that warm-started, or whose volume
could not answer the query at all, reports `CheckpointLoss` as null rather than
guessing.

Turning that into the user's choice is the consumer's job: show the size,
say what the journal is now, and let the user decide whether a journal that large
is worth it or whether an occasional rescan is cheaper. Scans are fast by design,
so a rescan is an acceptable outcome, not a failure.

MFTLib never changes the journal on its own:
enlarging it is an explicit call, `JournalBrokerClient.GrowUsnJournalAsync`,
which the broker performs elevated and which only grows, refusing a requested
maximum at or below the current one. Growing is persistent and shared with
every other journal consumer on the volume (Windows Search, backup and
replication agents), so surface it as a user action, not a startup default.

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

The non-elevated side then creates one client. Given an existing `cacheDirectory` and
the discovered `uint volumeSerial` for C, supply the destination for that drive:

```csharp
await using var broker = await JournalBrokerClient.SpawnAndConnectAsync(
    BrokerLauncher.Launch,
    cancellationToken);

var scan = await broker.ArmScanAndCatchUpAsync(
    new[] { "C" },
    new BrokerScanOptions
    {
        BlockTargets = new Dictionary<string, BlockScanTarget>
        {
            ["C"] = new(Path.Combine(cacheDirectory, $"C-{volumeSerial:X8}.mlix"), volumeSerial, false)
        }
    },
    cancellationToken);
```

Completed blocks in `scan.BlockOutcomes` belong to the caller and must be disposed
separately when scanning through `JournalBrokerClient` directly like this.

`JournalBrokerScanSession` wraps this same client into one owned scan-to-watch object
(`StartAsync` through discovery, live watch, rescan, and disposal). A session owns the
blocks it publishes: those in `session.LatestScan.BlockOutcomes` are disposed by the next
rescan and by the session's own disposal, so take a block out of the result first if it
has to outlive either. The session also owns the client. See the [broker integration guide](https://github.com/mtschoen/MFTLib/blob/main/docs/broker-integration.md)
for startup dispatch, result handling, live watch, rescans, recovery, and diagnostics.

## Build a live index with FileIndex

`MFTLib.Index.FileIndex` builds a packed, substrate-neutral per-drive file index and
keeps it current with a live USN watch it owns end to end: `StartWatchingAsync` arms
every MFT-backed drive from its own block's journal cursor, `Changed` raises one event
per applied change, and `WatchFaulted` reports a per-drive or whole-stream failure
without tearing down the index. See [the block format](docs/index-format.md) for the
on-disk layout and [the broker integration guide](docs/broker-integration.md) for how
the live watch bridges to the elevated broker.

`FileIndexOptions.ProducerPolicy` selects how each drive's block is built:
`ProducerPolicy.Mft` (the default) reads the Master File Table through
`FileIndexOptions.MftProducer`, and `ProducerPolicy.Enumeration` walks the directory
tree instead. The two are never mixed within one open: a drive whose MFT scan fails is
reported `DriveState.Failed` and never falls back to a directory walk, and the
enumeration producer runs only when a caller chooses `ProducerPolicy.Enumeration`
explicitly, never as an inherited fallback decision. Why a `DriveState.Failed` drive has
no block is reported as `DriveStatus.FailureKind`: `CacheDeclined` when
`FileIndexOptions.InitialOpenCacheOnly` found no usable cache and forbade a scan, `InUse` when
another live `FileIndex` holds the cache block's owner lock, and `ProducerFailed` when the MFT
producer itself failed. A failed drive of either kind accepts a per-drive
`FileIndex.RescanAsync`, which scans it and clears the kind on success.

`FileIndexOptions.NoCache` blocks are created with `FileOptions.DeleteOnClose`, so the
operating system removes them when the last handle closes, including when the process
is killed rather than shut down gracefully.

A cache-mode index takes a per-block owner lock - a sibling `<block>.lock` file held open with
`FileShare.None` - for as long as it owns a canonical cache block, on Windows and on Linux.
While another live index holds that lock, a second `FileIndex` never validates, renames, or
deletes the block: a cache-only open reports the drive `Failed` with `DriveFailureKind.InUse`,
and a non-cache-only open scans into a private delete-on-close block in the temp directory
without replacing the canonical cache. The lock is released on disposal and by the operating
system if the process dies, so a killed index never strands its cache slot. Every block-file
delete is reported through `FileIndexOptions.Diagnostics` with the path and the reason.

`FileIndexOptions.OpenProgress` reports open-time progress per drive: `OpenAsync`
fires one `IndexDriveOpened` per configured drive, in configured order and
synchronously on the opening thread, after that drive settles: warm-started from
cache, cold-scanned, declined by `InitialOpenCacheOnly`, offline, or failed. The
report carries the drive letter, its 1-based ordinal in the configured drive
list, the total configured drive count, and the settled `BlockSource` and
`DriveState`, so a consumer can render "drive 3 of 9: G:" while the open is still
in flight. A declined or failed drive still counts toward the total and still
reports. It defaults to null, which reports and allocates nothing, and it fires
on warm starts too, unlike `FileIndexOptions.Progress`, which samples only while
a producer runs. `RescanAsync` stays silent: its caller already awaits the one
drive it rescans. Marshalling belongs to the `IProgress<T>` implementation, the
same convention `FileIndexOptions.Progress` uses.

`FileEntry.Path` is a real filesystem path: the drive block's root directory joined
with the entry's name chain using the host separator. It can be opened, and
`FileIndex.Find` accepts it back, resolving a native path against the longest
matching indexed root. Disposing a `FileIndex` releases every block mapping it
holds, so the `.mlix` files are closed at a point the caller chooses; a `FileEntry`
held across that disposal reports `IsDisposed` and throws `ObjectDisposedException`
on every read.

Eight entry points scan rows: `Find`, `FindByName`, `Search`, `Enumerate`, `Largest`,
`DuplicateNames` and `Root` on `FileIndex`, and `Children()` on a `FileEntry`. Each
takes an optional `CancellationToken`, read before the first row and then at least
every 4096 rows, and each holds the snapshot it reads for its whole duration.
`DisposeAsync` waits for every one of those readers before it unmaps anything, so
scanning on one thread while another disposes the index is safe. The seven on
`FileIndex` also observe the index's disposal, so disposing cancels them and each ends
with `OperationCanceledException`, or `ObjectDisposedException` if it had not started;
while actively scanning, the wait is bounded by how long they take to reach their
next checkpoint. A suspended `Enumerate` enumerator holds its borrow between yields,
so disposal waits until it advances or is disposed, or, if abandoned, until garbage
collection and finalization return its borrow. Dispose enumerators promptly;
collection timing is not guaranteed. `Children()`
is the exception: a handle holds no reference to its index, so disposal waits that
listing out instead, one pass over the drive's rows unless the caller passes a token.
Every other `FileEntry` member reads a single row rather than scanning and carries no
borrow, so a read ordered after the disposal throws `ObjectDisposedException` from the
per-access check instead.

`DriveStatus.BlockSource` says whether a drive warm-started from cache or was scanned,
so a rebuild loop can skip the drives an open already scanned, and
`CacheDirectory.EnumerateCached` lists the drives a cache directory holds without a
consumer parsing block file names.

`IsValid` and `IsDisposed` remain readable after disposal, and `ToString()` returns
a diagnostic string. Reads of mapped entry data throw as described above.
`DriveStatus.BlockSource` is `None` when no block is available,
`WarmStartedFromCache` for an adopted cache block, or `ProducedByScan` after a scan.
`CacheDirectory.EnumerateCached` returns `CachedBlockFile` records containing the
drive letter, volume serial, full cache-file path, size, and last-write time;
listing a file does not open or validate its block.

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

Visual Studio 2022 with the Desktop development with C++ workload and .NET 10 SDK is
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

- `MFTLib/Index` - the substrate-neutral packed index: block format, `FileIndex`, snapshots, queries, mutation, and the enumeration producer. It is not MFT-specific and depends on nothing else in the library beyond a few journal value types, a boundary an architecture test enforces.
- `MFTLib/Mft` - scans, records, results, filters, paths, and timings
- `MFTLib/Journal` - USN cursor, entries, reasons, and `MftVolume` journal APIs
- `MFTLib/Broker` - elevated host/client, protocol, block writing, and diagnostics
- `MFTLib/Elevation` - elevation detection and injectable provider
- `MFTLib/Interop` - native result layouts
- `MFTLib/Internal` - native bindings and internal volume utilities

## License

[MIT](LICENSE.txt)
