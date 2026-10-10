# MFTLib

MFTLib is a .NET library for building fast file indexes, search tools, backup
catalogs, and filesystem monitors. On NTFS volumes it reads the Master File Table (MFT)
directly for a high-throughput snapshot, then uses the USN change journal to keep that
snapshot current without repeatedly walking the filesystem.

The index in `MFTLib.Index` is not limited to NTFS or to Windows. A cached, memory-mapped
block holds each drive's file metadata, and a producer fills it: the MFT scan on an NTFS
volume, or the enumeration producer, which walks the directory tree and so runs over any
filesystem, including on Linux. Queries and the cache work the same way whichever producer
built the block; live USN watching requires an MFT-backed NTFS index, while
enumeration-backed indexes are refreshed by rescanning. The name stays MFTLib because the
MFT scan is the fast path the library was built around and the project's identity; the other
producers feed the same index.

The public API is managed C#; performance-sensitive volume I/O and record parsing
run in a native C++ core.

## Why MFTLib?

Normal directory enumeration visits the filesystem tree one directory at a time. MFTLib
instead reads NTFS's central record table, which is useful when an application needs to:

- discover files or directories across an entire volume quickly;
- build and maintain a searchable local file index;
- find records by filename without Windows Search;
- correlate full-scan records with later filesystem changes; or
- keep its main process non-elevated while raw-volume work runs in one elevated child.

MFTLib does not read file contents. The raw MFT and USN journal paths are specialized
for NTFS metadata on Windows; the index runs elsewhere through the enumeration producer.

## Highlights

- File index (`MFTLib.Index`) over any filesystem, with the enumeration producer on Linux
  and the MFT scan on NTFS
- In-process scan of a live volume (`MftIndexSources.FromLocalVolumes`) or a saved MFT dump (`MftIndexSources.FromMftDumpFile`) into the same index
- Runtime detection of the volume's MFT record size (1024 or 4096 bytes) instead of an
  assumed fixed size
- Native C++ I/O with parallel fixup and parsing
- Double-buffered reads that overlap I/O and compute
- Exact, substring, case-sensitive, size and time predicates over the packed index (`SearchQuery`)
- `FileIndex.Search` (list), `FileIndex.Enumerate` (streaming entries) and `FileIndex.EnumerateRows` (row views)
- Live USN watch per drive through `FileIndex` and `BrokerSession`
- Race-free scan/catch-up workflow through an elevated broker
- One reusable UAC-elevated child per broker session
- Per-drive error isolation and broker-death notification

A synthetic 8-million-record benchmark has exceeded 2.6 million records/second on a
Ryzen 9 7950X3D with a Samsung 990 PRO. Real-volume performance depends on storage,
volume size and hardware.

## Requirements

- An elevated Windows process on an NTFS volume for `MftIndexSources.FromLocalVolumes`
- `MftIndexSources.FromMftDumpFile` and enumeration indexing need no elevation and run on Windows and Linux x64
- .NET 10.0 or later
- x64 process architecture

The NuGet package includes `MFTLibNative.dll` under `runtimes/win-x64/native` and a
transitive build target that copies it to a Windows consumer's output directory. It also
includes `libMFTLibNative.so` under `runtimes/linux-x64/native`, which the .NET host
resolves on Linux x64 for the MFT dump source. The Linux
library is built on Ubuntu 24.04 and requires glibc 2.33 and GLIBCXX_3.4.22 (measured on the
released library by `scripts/check-linux-native.sh`).
`MFTLib.TestExtensions` is a separate package for consumer test assemblies. It depends
on the matching `MFTLib` version and contains `MFTLibTestExtensions.dll`; it is not part
of the `MFTLib` package. It provides test hooks and scripted types: `BrokerDiagnosticsIsolation`,
`BrokerTestHarness`, `FileIndexTestAccess`, `InProcessBrokerHandle`,
`InProcessBrokerScan`, `JournalIsolation`, `ScriptedBrokerVolumes`, `ScriptedDriveWatch`,
`ScriptedScan`, `ScriptedWatchSource`, `ScriptedWatchStart`, `SyntheticBlock`,
`SyntheticBlockEditor`, `SyntheticBlockOptions`, `SyntheticCacheTag`, `SyntheticCheckpointLoss`,
`SyntheticDriveHeader`, `SyntheticIndexInspection`, `SyntheticIndexSource`, `SyntheticJournalCursor`,
`SyntheticJournalReason`, `SyntheticJournalRecord`, `SyntheticJournalWindow`, `SyntheticMftProducer`,
`SyntheticNotifications`, `SyntheticRow`, and `SyntheticScanRecord`.

## Install

Cached `FileIndex.OpenAsync` calls require a nonblank application-owned
`FileIndexOptions.CacheDirectory`, including empty-drive opens. Missing configuration
throws `ArgumentException`. `NoCache = true` needs no cache path and ignores any
supplied path without creating it. Dump sources prohibit cache options. Tests own
their temporary cache paths; no default-cache guard is needed.

The Watch sample defaults to `<user profile>/.MFTLib.Sample.Watch/cache`, with
policy subdirectories used by cached modes and cache inventory/deletion alike.
If no profile is available, supply `--cache-directory`. Direct uses NoCache.

```bash
dotnet add package MFTLib --version 0.3.0
dotnet add package MFTLib.TestExtensions --version 0.3.0
```

Or add a package reference:

```xml
<PackageReference Include="MFTLib" Version="0.3.0" />
```

For test isolation and synthetic journal helpers in a consumer test assembly, reference
the matching test extensions package:

```xml
<PackageReference Include="MFTLib.TestExtensions" Version="0.3.0" />
```

## Choose an integration model

| Scenario | Recommended API |
| --- | --- |
| Already elevated; scan once | `MftIndexSources.FromLocalVolumes` with `NoCache`, no watch |
| Saved image on any supported platform | `MftIndexSources.FromMftDumpFile` with `NoCache`, no watch, no elevation |
| Non-elevated desktop/CLI app; one UAC prompt | `BrokerSession` with `CreateIndexSource()` |
| Resume from a persisted journal cursor | Warm start from the cache block, whose header holds the cursor |
| Continuously receive changes | `FileIndex.StartWatchingAsync` and `Changed` |
| Explain a rescan the change journal forced, at open or mid-watch | `DriveWatchStatus.CheckpointLoss` |

## Samples

`SampleProgram.Direct` supports `search`, `tree`, `open`, `largest`, `duplicate-names`
and `scan`. Select `--source local` for a live volume or `--source dump --dump-file <path>`
for a saved MFT image; `--include-freed` applies to local scans.
The `scan` verb prints status and a one-pass inventory per drive: live files, live directories
(including the indexed root) and retained deleted rows. Deleted files and directories contribute
only to the deleted count; free slots contribute to none. `--include-freed` retains freed records
on local scans so the inventory can count them.
`SampleProgram.Watch` supports `scan-drive`, `watch`, `rescan`, `journal`, `cache`
and `elevation-status`. Its `cache` verb uses policy-digest folders to keep different
scan policies in distinct sample cache directories.

```powershell
.\SampleProgram.Direct\bin\x64\Release\net10.0\SampleProgram.Direct.exe search C --name notes
.\SampleProgram.Watch\bin\x64\Release\net10.0\SampleProgram.Watch.exe scan-drive C
```

Run the compiled executable for self-elevation. Direct local scans self-elevate;
Watch stays unelevated and its broker elevates. Dump scans need no elevation.
Set `MFTLIB_SAMPLE_UNATTENDED=1` to skip prompts and elevation requests in an
unelevated unattended run. See [elevation](docs/elevation.md).

The dump source loads one virtual drive in process on Windows or Linux x64:

```csharp
var letter = 'D';
var options = new FileIndexOptions
{
    Drives = [new IndexedDrive(letter, "dump:/" + letter, 0)],
    MftSource = MftIndexSources.FromMftDumpFile(filePath, letter),
    NoCache = true,
    ProducerPolicy = ProducerPolicy.Mft
};
await using var index = await FileIndex.OpenAsync(options, CancellationToken.None);
```

A dump is untrusted. An empty file, a record size other than 1024 or 4096, a partial
final record, an invalid fixup on an allocated record, a file unreadable to its opened
length, a missing or invalid root, duplicate record numbers or a parent outside the
index each fail the scan with a message in `DriveStatus.FailureMessage`.

## Freed MFT records

Freed MFT records are opt-in. `BrokerScanOptions.IncludeFreed` (default false) makes a cold scan
of a live volume, direct or through the broker, also import the records NTFS has freed; the
dump source offers no such option. Each freed record becomes a row whose `FileEntry.IsDeleted`
is true and which exists for that scan only: a later watch applies journal events to live rows
and never edits, renames or resurrects it.

A freed row keeps its parent and full path only when its parent chain verifies. Every parent
through the root must be a directory whose stored sequence equals the sequence the child's name
referenced, or is one higher when that parent was freed too (16-bit wraparound), within 128
components and without a cycle. Any other freed row is detached: its path is its bare name, it
has no parent, and it is no directory's child. A cached block carries the rows of the scan that
produced it, so a consumer that changes `IncludeFreed` also changes its `CacheTag` version.

```csharp
var options = new FileIndexOptions
{
    Drives = [new IndexedDrive('C', @"C:\", volumeSerial)],
    MftSource = MftIndexSources.FromLocalVolumes(new BrokerScanOptions { IncludeFreed = true }),
    CacheDirectory = cacheDirectory,
    CacheTag = new CacheTag("APPX", 2)
};
await using var index = await FileIndex.OpenAsync(options, CancellationToken.None);
var deleted = index.Search(new SearchQuery("report", IncludeDeleted: true))
    .Where(entry => entry.IsDeleted);
```

`SearchQuery.IncludeDeleted` (default false) makes a search return deleted rows as well, whether
the journal deleted them or a scan imported them freed. `FileIndex.Find`, `FileIndex.Root` and
`FileEntry.Children` return live rows only.

## Keep an index current with the USN journal

A cached block holds the journal instance and next position together in its header.
Open a broker-backed `FileIndex` to warm-start from that checkpoint or scan and catch
up when the cache cannot be resumed. Consumers query the index and receive applied
changes instead of persisting a separate cursor.

```csharp
await using var session = new BrokerSession();
var options = new FileIndexOptions
{
    Drives = [IndexedDrive.FromWindowsVolume("C:")],
    CacheDirectory = cacheDirectory,
    MftSource = session.CreateIndexSource()
};
await using var index = await FileIndex.OpenAsync(options, cancellationToken);
index.Changed += change =>
    Console.WriteLine($"{change.Kind}: {change.Path}");
await index.StartWatchingAsync('C', cancellationToken);
await index.WaitForCatchUpAsync('C', cancellationToken);
```

`Changed` events carry `FileChangeKind`, the current path, the timestamp and the
previous path for a rename. A start establishes the connection; the catch-up wait
observes the watch reaching its live position. Journal checkpoint loss is recorded
on `DriveWatchStatus.CheckpointLoss`; a successful `RescanAsync('C')` supplies a fresh
block and restarts a requested watch. `StopWatchingAsync(cancellationToken)` stops
every configured drive and returns its per-drive results.

A watched volume whose change journal is too small wraps under load: records
are overwritten before the watch reads them, the watch faults, and the drive
needs a rescan. Windows sizes a new journal at 32 MB, which a busy system
drive can wrap in minutes.

On an open index, `index.QueryUsnJournalSettings(driveLetter)` reports a configured
volume's sizing, `MaximumSize` and `AllocationDelta`, without elevation.

### When a rescan happened because the journal moved on

The only journal event that costs anything is a drive's journal position falling
out of the journal, because the drive must then be scanned from scratch instead
of caught up. It is detected when opening a cached block, when a live watch
faults, or when the journal outruns a scan before that scan can catch up. Each
case is recorded on that drive's status:

```csharp
await using var index = await FileIndex.OpenAsync(options, CancellationToken.None);

foreach (var drive in index.Drives)
{
    if (drive.Watch.CheckpointLoss is not { } loss)
    {
        continue;
    }

    switch (loss.Cause)
    {
        case JournalCheckpointLossCause.CheckpointTrimmed when loss.SizeThatWouldHaveRetained is { } size:
            Console.WriteLine(
                $"Drive {loss.DriveLetter}: the last checkpoint was {loss.BytesBehind} bytes " +
                $"older than the journal still holds, so a full rescan was needed. A journal " +
                $"of at least {size} bytes (it is {loss.JournalSettings.MaximumSize} now) would have kept the " +
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
arithmetic on values read off the volume: `BytesBehind` is the journal's oldest
retained USN minus the checkpoint, and `SizeThatWouldHaveRetained` is the journal's
next USN minus the checkpoint, rounded up to the journal's allocation delta, plus one
more allocation delta. The three positions themselves are internal; the report
carries the two derived sizes.
That margin comes from NTFS's documented trimming behavior in
`CREATE_USN_JOURNAL_DATA` and `USN_JOURNAL_DATA`: the maximum is a target and a
trim can leave the journal below it. The value is not a live measurement, and
there is no clock, rate or cap.

The report is attached to the drive it describes, for as long as the block it
explains is in place. A `FileIndexOptions.InitialOpenCacheOnly` open adopts a
usable cached block even when its checkpoint is gone: the drive is `Ready`, the
report explains why its cursor cannot be resumed, and a watch start is refused
until `RescanAsync` supplies a fresh cursor. A successful manual rescan clears
the report and the refusal. The refused start retains the watch request, so the
successful rescan starts the watch from the fresh cursor.

A loss found mid-session sits alongside `Watch.FailureMessage` and
`Watch.CatchUpState`. It answers the question those two cannot: the watch did not
merely stop, the journal moved past where it had reached. `Drive` and `Apply`
faults recover by rescanning automatically; `Channel` faults do not. A scan-time
loss records `JournalCheckpointLossDetection.ScanCatchUp`, publishes the complete
but unresumable block, and retries the scan until automatic recovery is exhausted.
`DriveWatchStatus.RecoveryStopped` records that decision. Subscribe to `FileIndex.WatchFaulted` to
observe live and scan-time losses. The report is recorded before the fault is
announced, so a handler that reads `index.Drives` already has it.

**In a fault handler, branch on `DetectedDuring`, not on the loss being
non-null.** A report lives until a rescan replaces the block it explains, so a
drive that cold-scanned at open still carries that report while it is being
watched, and an unrelated fault later would otherwise read as the journal's
doing. `DetectedDuring` says which check found it:

```csharp
index.WatchFaulted += fault =>
{
    var drive = index.Drives.Single(d => d.DriveLetter == fault.DriveLetter);
    if (drive.Watch.CheckpointLoss is
        {
            DetectedDuring: JournalCheckpointLossDetection.LiveWatch or
                JournalCheckpointLossDetection.ScanCatchUp
        } loss)
    {
        Console.WriteLine(
            $"Drive {fault.DriveLetter}: {loss.DetectedDuring}, {loss.Cause}");
        return;
    }

    // Anything else is a plain watch failure, whatever else the drive is carrying.
    Console.Error.WriteLine(drive.Watch.FailureMessage);
};
```

`JournalCheckpointLossDetection.DriveOpening` means the report explains why this
session cold-scanned at open, or why a cache-only drive cannot be watched. It is
not a diagnosis of a later fault. `LiveWatch` means the drive's live cursor was
gone when its watch faulted. `ScanCatchUp` means the cursor armed before a scan
was gone when that scan finished. A watch that fails for any other reason leaves
the report exactly as it was, `DriveOpening` label included, or leaves it null,
because the classification is the journal's answer about that drive's position
rather than a reading of the exception that ended the watch. A newer loss
replaces an earlier report; an unrelated fault neither rewrites nor deletes it.

`Cause` separates the situations MFTLib can actually tell apart.
`CheckpointTrimmed` means the journal is the one the checkpoint came from and has
trimmed past it, so `SizeThatWouldHaveRetained` says the size a journal would
need to be at least to have kept the checkpoint, when that size fits in a
`long`; it is null when it does not. `JournalRecreated` means the journal
was deleted and recreated and carries a different id, so the checkpoint refers to
a journal that no longer exists: no size would have helped, and none is offered.
`JournalAdvanced` means a scan-only cached source still has a retained cursor but
the journal's next USN differs from it. With no catch-up source it rescans; no
retention size would help, so `BytesBehind` and `SizeThatWouldHaveRetained` are null.
A consumer decides what to say from `Cause`, not from whether the size is null,
since all causes can leave it null. `Cause` and `DetectedDuring` answer
different questions and are read together: `Cause` says whether a journal size
would have helped, `DetectedDuring` says whether the drive needs anything done
about it now. A drive that warm-started, whose watch has never lost its
position, or whose volume could not answer the query at all, reports
`Watch.CheckpointLoss` as null rather than guessing.

Turning that into the user's choice is the consumer's job: show the size,
say what the journal is now, and let the user decide whether a journal that large
is worth it or whether an occasional rescan is cheaper. Scans are fast by design,
so a rescan is an acceptable outcome, not a failure.

MFTLib never changes the journal on its own:
enlarging it is an explicit call, `BrokerSession.GrowUsnJournalAsync`,
which the broker performs elevated and which only grows, refusing a requested
maximum at or below the current one. Growing is persistent and shared with
every other journal consumer on the volume (Windows Search, backup and
replication agents), so surface it as a user action, not a startup default.

Use `loss.TryGetGrowthTarget(out var target)` to obtain the recorded retention size
and allocation delta for that action. It succeeds only for `CheckpointTrimmed`
with a positive representable retention size greater than the recorded maximum
and a positive allocation delta; on failure, `target` is default. The method
does not recompute sizing or query the volume. Immediately before growth, query
fresh settings with `index.QueryUsnJournalSettings(driveLetter)` and require
`target.MaximumSize` to exceed the current maximum. Pass `target.MaximumSize` and
`target.AllocationDelta` to `GrowUsnJournalAsync`, then rescan the drive: growth
does not restore records already lost.

## Keep the application non-elevated

For desktop applications and long-running tools, use the elevated broker instead of
running the entire process as Administrator. One `BrokerSession` owns the elevated process and
its control pipe for the consumer session. Each scan and each drive watch gets its own drive pipe, so a
slow, stopped, or failed drive does not end another drive's operation.

At minimum, the application must dispatch broker mode before normal startup:

```csharp
if (ElevatedEntryPoint.TryHandle(Environment.GetCommandLineArgs()))
{
    return;
}
```

The non-elevated side creates one session. It launches the broker on first use, so the UAC
prompt appears when the first scan or watch needs it, not when the session is created:

```csharp
await using var session = new BrokerSession();

session.Connecting += () => Console.Error.WriteLine("Requesting elevation...");
_ = session.Ended.ContinueWith(
    ended => Console.Error.WriteLine($"Broker ended: {ended.Result}"),
    TaskScheduler.Default);

var drive = IndexedDrive.FromWindowsVolume("C:");
var options = new FileIndexOptions
{
    Drives = [drive],
    CacheDirectory = cacheDirectory,
    MftSource = session.CreateIndexSource()
};

await using var index = await FileIndex.OpenAsync(options, cancellationToken);
```

The session owns the broker process: a launch that fails (the UAC prompt declined, a connect
timeout) is not remembered, so the next use asks again; a process that ends stays ended
(`HasEnded`, `Ended`) and every later use throws `InvalidOperationException`. Dispose the
indexes, then the session. Scans run only through the index source, and
`BrokerSession.GrowUsnJournalAsync` is the one direct broker operation:

```csharp
UsnJournalSettings grown = await session.GrowUsnJournalAsync(
    drive.DriveLetter,
    requestedMaximumSize,
    requestedAllocationDelta,
    cancellationToken);
```

Journal growth is an explicit user action; the broker refuses a requested maximum at or
below the current value. `Ended` completes with the reason when the control pipe is lost.

Consumers obtain a broker-backed source only through `BrokerSession.CreateIndexSource`.
The producer and the process launch and journal-growth methods are internal.

See the [broker integration guide](https://github.com/mtschoen/MFTLib/blob/main/docs/broker-integration.md)
for startup dispatch, index scans, watch channels, recovery, and diagnostics.

## Build a live index with FileIndex

`MFTLib.Index.FileIndex` builds a packed, substrate-neutral per-drive file index and
keeps it current with one live USN watch handle per drive. A start arms the named drive
from its own block's journal cursor, `Changed` raises one event per applied change, and
every `WatchFaulted` notification names the affected drive. See [the block format](docs/index-format.md) for the
on-disk layout and [the broker integration guide](docs/broker-integration.md) for how
the live watch bridges to the elevated broker.

The single-drive lifecycle calls throw that drive's failure directly:

```csharp
await index.StartWatchingAsync('C', cancellationToken);
await index.WaitForCatchUpAsync('C', cancellationToken);

await index.RescanAsync('C', cancellationToken); // Restarts C if its watch is still requested.
IReadOnlyList<DriveOperationResult> stops = await index.StopWatchingAsync(cancellationToken);
```

The list-form rescan and all-drive lifecycle forms run drives concurrently and return one
`DriveOperationResult` per drive in request order. One drive's `Failed` result does not
discard the others. `NotApplicable` means the operation has nothing to do for that
drive, such as starting an enumeration-backed drive or waiting on a drive with no watch.

```csharp
IReadOnlyList<char> driveLetters = ['C', 'D'];

IReadOnlyList<DriveOperationResult> starts =
    await index.StartWatchingAsync(cancellationToken);
IReadOnlyList<DriveOperationResult> catchUps =
    await index.WaitForCatchUpAsync(cancellationToken);
IReadOnlyList<DriveOperationResult> rescans =
    await index.RescanAsync(driveLetters, cancellationToken);
IReadOnlyList<DriveOperationResult> stops =
    await index.StopWatchingAsync(cancellationToken);
```

Start, stop and catch-up wait accept a token-only all-drive call in
`FileIndexOptions.Drives` order; rescan accepts a drive letter or a drive list.
Batched calls throw for invalid input, disposal, and
cancellation; drive-specific operational failures stay in `DriveOperationResult.Failure`.

`FileIndexOptions.ProducerPolicy` selects how each drive's block is built:
`ProducerPolicy.Mft` (the default) reads the Master File Table through
`FileIndexOptions.MftSource`, and `ProducerPolicy.Enumeration` walks the directory
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
fires one `IndexDriveOpened` for each drive that settles, synchronously on the
thread that settled it: warm-started from cache, cold-scanned, declined by
`InitialOpenCacheOnly`, offline, or failed. A cancelled or failed open may have
reported only some of the drives. `OpenAsync` settles its
drives concurrently and runs no lock around a handler, so reports can overlap and
arrive out of order; `SettledDriveCount` gives the order. The report
carries the drive letter, `SettledDriveCount` (this drive was the n-th to settle,
counted from 1), and the total configured drive count, so a consumer can render "3 of 9 drives settled" while the open
is still in flight. Keep the report with the largest `SettledDriveCount`, not simply
the last callback to arrive. A declined or failed drive still counts toward the total and
still reports. Block ordinals follow the same order for drives with a block;
`FileIndex.Drives` keeps the configured order. It defaults to null, which reports
and allocates nothing, and it fires on warm starts too, unlike
`FileIndexOptions.Progress`, which samples only while a producer runs.
`RescanAsync` stays silent: its caller already awaits the one drive it rescans.
Marshalling belongs to the `IProgress<T>` implementation, the same convention
`FileIndexOptions.Progress` uses.

A cold drive that loses its journal catch-up is scanned again by its own settle,
until automatic recovery is exhausted, and settles `Ready`
with its last block unresumable if every attempt lost it. `OpenAsync` raises no
`WatchFaulted` event because the caller cannot subscribe before it returns; inspect
`DriveWatchStatus.RecoveryStopped`, `CheckpointLoss`, `FailureMessage`, and
`CatchUpState` instead. The drive's watch is refused until a manual `RescanAsync`
produces a resumable block. The refusal retains the watch request, so a successful
rescan clears the refusal and starts the watch.

`FileEntry.Path` is a real filesystem path: the drive block's root directory joined
with the entry's name chain using the host separator. It can be opened, and
`FileIndex.Find` accepts it back, resolving a native path against the longest
matching indexed root. Disposing a `FileIndex` releases every block mapping it
holds, so the `.mlix` files are closed at a point the caller chooses; a `FileEntry`
held across that disposal reports `IsDisposed` and throws `ObjectDisposedException`
on reads of mapped entry data.

### Streaming entries and row views

Use `EnumerateRows` for whole-drive passes that read names, sizes or flags and keep few rows.
It yields non-allocating `IndexRow` views, with `Name` as a `ReadOnlySpan<char>` over the mapped
name pool. Use `Enumerate` (streaming) or `Search` (a materialized list) when you want
`FileEntry` values and LINQ.

```csharp
long total = 0;
foreach (var row in index.EnumerateRows(new SearchQuery("*.log", NameMatchMode.Glob, Directories: false)))
{
    if (row.IsSizeKnown)
    {
        total += row.Size;
    }
}
```

The borrow begins when `foreach` calls `GetEnumerator()` and ends on completion, `break`,
an exception or cancellation. Each row and its name span are valid until the enumerator
advances or is disposed; call `row.ToEntry()` to keep an entry handle. The result, enumerator
and rows are ref structs, so they cannot cross an `await`, be captured in a lambda or be stored
in a class or ordinary struct field. If you call `GetEnumerator()` directly, dispose the
enumerator yourself, even after an exception or a false `MoveNext`. Enumerator copies share one
borrow: dispose it once through any copy. `Current` and `MoveNext` on every copy then throw
`ObjectDisposedException` naming `IndexRowEnumerator`; repeated `Dispose` is harmless. Previously
obtained rows and spans must obey their lifetime themselves. A reachable undisposed enumerator
can keep `DisposeAsync` waiting indefinitely. Its internal borrow object has a finalizer that can
eventually return an unreachable borrow, but collection timing is not guaranteed. Dispose promptly.

Each `GetEnumerator` takes its own snapshot borrow, even on copies of the enumerable. This keeps
that set of mappings owned and mapped; it does not freeze row contents. A live watch writes rows
in place. Name text is append-only and its offset and length are read together, so a captured name
span keeps its text and cannot combine one name's offset with another's length. Other row properties
read live fields; there is no atomic whole-row view or atomic filtering-plus-consumption. A scanner
captures the row-count bound when entering each drive; separate passes can see different contents
and different mappings. Index disposal cancels an active borrowed scan at its cancellation checkpoint.

`EnumerateRows` rejects a null query or undefined match mode at call time. Cancellation and index
disposal are checked on admission at `GetEnumerator`; cancellation and unresolved subtree parent
chains can throw during `MoveNext`.

Six entry points scan rows: `Find`, `Search`, `Enumerate`, `EnumerateRows`
and `Root` on `FileIndex`, and `Children()` on a `FileEntry`. Each
takes an optional `CancellationToken`, read before the first row and then at least
every 4096 rows, and each holds the snapshot it reads for its whole duration.
`DisposeAsync` waits for every one of those readers before it unmaps anything, so
scanning on one thread while another disposes the index is safe. The five on
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

`DriveBlockStatus.Source` says whether a drive warm-started from cache or was scanned,
so a rebuild loop can skip the drives an open already scanned, and
`CacheDirectory.InspectCached` lists the drives a cache directory holds without a
consumer parsing block file names.

`IsValid` and `IsDisposed` remain readable after disposal, and `ToString()` returns
a diagnostic string. Reads of mapped entry data throw as described above.
`DriveBlockStatus.Source` is `None` when no block is available,
`WarmStartedFromCache` for an adopted cache block, or `ProducedByScan` after a scan.
`CacheDirectory.InspectCached` returns `CachedBlockStatus` records whose `File` is a `CachedBlockFile` containing the
drive letter, volume serial, full cache-file path, size, and last-write time, plus
whether the block is available, in use by another index, or invalid.

## Errors and recovery

Most volume, native parsing, and journal failures surface as `InvalidOperationException`
with the native error message. Common causes include:

- the process is not elevated;
- the target is not an NTFS volume;
- the volume cannot be opened;
- the USN journal is unavailable, recreated, or wrapped; or
- native allocation capacity is exhausted.

`WatchFaulted` reports a `WatchFault` containing `Kind`, `DriveLetter`, and the original
`Exception`:

- `Subscriber`: a `Changed` handler threw. The drive keeps watching, and this is announced
  once for that drive's current watch.
- `Drive` or `Apply`: the watch or batch application failed. The drive publishes
  `WatchCatchUpState.Recovering` before the event, rescans itself, and starts a fresh watch
  from the replacement block.
- `CatchUpLost`: a scan completed, but the journal no longer held the cursor armed before
  it. The exception is `JournalCatchUpLostException`; the drive records a
  `JournalCheckpointLossDetection.ScanCatchUp` report and rescans itself until
  `DriveWatchStatus.RecoveryStopped` is true. A successful catch-up clears the flag;
  failed or cancelled scans and stopping the watch preserve it.
- `Channel`: the drive pipe was lost or ended without a stop. This fault does not recover
  automatically.
- `RescanRestart`: a rescan replaced the block but its watch could not start. The scan
  succeeds; the exception and `DriveWatchStatus.FailureMessage` identify the rescan and the exception's
  inner exception is the start failure. The drive stays `Faulted` without automatic recovery
  until a consumer starts or rescans it. Stop rethrows this fault once.
- `Recovery`: an automatic recovery scan or restart failed, or its restarted watch failed before
  reaching `WatchCatchUpState.CaughtUp`. The drive stays `Faulted` until the consumer calls
  `RescanAsync` or `StartWatchingAsync` for that drive.

A bounded catch-up read that returns entries without advancing its cursor fails that
catch-up immediately. Those entries are not delivered, so they cannot be applied twice.
The live journal check then reports `CatchUpLost` when it proves the armed cursor was lost,
or an `Error` when it cannot prove that loss.

```csharp
index.WatchFaulted += fault =>
{
    var status = index.Drives.Single(drive => drive.DriveLetter == fault.DriveLetter);
    switch (fault.Kind)
    {
        case WatchFaultKind.Drive:
        case WatchFaultKind.Apply:
            Console.WriteLine(
                $"Drive {fault.DriveLetter}: {status.Watch.CatchUpState == WatchCatchUpState.Recovering}");
            break;

        case WatchFaultKind.CatchUpLost
            when fault.Exception is JournalCatchUpLostException lost:
            Console.WriteLine(
                $"Drive {fault.DriveLetter}: recovery stopped {status.Watch.RecoveryStopped}, " +
                $"stopped {lost.RecoveryStopped}, report {status.Watch.CheckpointLoss?.DetectedDuring}");
            break;

        case WatchFaultKind.Channel:
        case WatchFaultKind.Recovery:
        case WatchFaultKind.RescanRestart:
            Console.Error.WriteLine(fault.Exception.Message);
            break;

        case WatchFaultKind.Subscriber:
            Console.Error.WriteLine($"Changed handler failed: {fault.Exception.Message}");
            break;
    }
};
```

A rescan keeps its healthy watch running during production. Failed or cancelled production
leaves that watch and its catch-up waits attached. A successful commit retires the old watch
and cancels its pending waits, drains it, then starts from the replacement cursor when still
requested. An old `Changed` event can arrive after the swap and can repeat during replacement
catch-up. Queries can lag until the new watch reports `CaughtUp`.

During automatic recovery, a catch-up wait faults immediately with the fault that started
the recovery. To observe the replacement watch reaching `CaughtUp`, subscribe to
`FileIndex.WatchStateChanged`: it reports every change of a drive's `DriveWatchStatus.CatchUpState` with the
drive's next `DriveWatchStatus.StateVersion`, before the `WatchFaulted` of the fault that
caused it, so a recovery reads `Recovering`, then `CatchingUp`, then `CaughtUp` with no
polling. One drive's events arrive in version order; a consumer that also reads `Drives`
applies an event only when its version is newer than the last it applied for that drive. When a lost catch-up
reaches the retry limit, `JournalCatchUpLostException.RecoveryStopped` is true, the drive
keeps its last queryable block, and its watch is refused until a manual rescan succeeds.

The all-drive `StopWatchingAsync` consumes each watch's outstanding fault once,
returning that exception in the affected drive's `DriveOperationResult.Failure`.
The following edge dispositions describe current behavior: a consumer source start failure leaves
a refused-start fault that a later stop clears without rethrowing; a fresh start supersedes
a faulted watch instance and discards its outstanding fault; and a stop that arrives while
a recovery or rescan is restarting the watch wins, leaves the drive stopped, and rethrows
the stopped instance's outstanding fault once. `DisposeAsync` does not rethrow watch or
broker-host faults.

Do not call a lifecycle method from inside that index's `Changed`, `WatchFaulted` or
`WatchStateChanged` handler. Start, stop, rescan, an unsettled catch-up wait, disposal, and their batched forms
fail immediately with `InvalidOperationException`. Queue the operation, for example with
`Task.Run`, so it begins after the handler returns. Queries, `Drives`, and already-settled
catch-up waits are allowed.

A broker `Error` frame fails only its pending operation with the host's message. Losing
the control pipe completes the `BrokerSession.Ended` task with the reason and
fails pending operations with an `IOException`. Losing a drive pipe faults
only that drive. Idle control and watch pipes, queued scans, and processing operations
that recently reported progress receive heartbeats. A processing operation with no
progress past the processing limit receives `Stalled` and its channel is cancelled. A
pipe with a write already in flight is skipped by the heartbeat sender; if it remains
silent, the client's 30-second no-frame limit ends that pipe.

## Building from source

Visual Studio 2022 with the Desktop development with C++ workload and .NET 10 SDK is
required. Use the shared Windows build script to restore packages, build the native
DLL with amd64 MSBuild, and build all six managed projects: MFTLib,
MFTLibTestExtensions, SampleProgram.Direct, SampleProgram.Watch, Benchmark and MFTLib.Tests:

```powershell
.\scripts\build-windows.ps1
```

The default is Release|x64; pass `-Configuration Debug` for Debug|x64. For checkout
initialization plus the same build, run `.\init.ps1 -Build`.

Run non-interactive managed coverage with:

```powershell
.\scripts\run-coverage.ps1 -NonInteractive
```

MFTLibTestExtensions is additionally required to have complete line, branch, and executable-method coverage. The publisher checks the assembly's raw Cobertura line hits, branch counts, and method records; partial coverage, absent evidence, and malformed evidence fail closed even when aggregate coverage would pass. The existing aggregate baseline rule remains in force for the whole report. Run `pwsh -NoProfile -File scripts/test-coverage-status.ps1` for the offline publisher regression checks.

The source is organized by responsibility:

- `MFTLib/Index` - the substrate-neutral packed index: block format, `FileIndex`, snapshots, queries, mutation, and the enumeration producer. It is not MFT-specific and depends on nothing else in the library beyond a few journal value types, a boundary an architecture test enforces.
- `MFTLib/Mft` - scans, records, results and timings
- `MFTLib/Journal` - USN cursor, entries and reasons
- `MFTLib/Sources` - producers, row writer and scan
- `MFTLib/Broker` - elevated host/client, protocol, block writing, and diagnostics
- `MFTLib/Elevation` - elevation detection and the injectable provider
- `MFTLib/Interop` - native result layouts
- `MFTLib/Internal` - native bindings and internal volume utilities

## License

[MIT](LICENSE.txt)
