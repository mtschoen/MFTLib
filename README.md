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
`MFTLib.TestExtensions` is a separate package for consumer test assemblies. It depends
on the matching `MFTLib` version and contains `MFTLibTestExtensions.dll`; it is not part
of the `MFTLib` package.

## Install

After 0.3.0 is published:

```bash
dotnet add package MFTLib --version 0.3.0
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
| Non-elevated desktop/CLI app; one UAC prompt | `BrokerProcess` with `BrokerMftBlockProducer.CreateIndexSource()` |
| One-time filename lookup | `MftVolume.StreamRecords` with a name filter |
| Full in-memory index | `MftVolume.StreamRecords`, then `MftResult.ToArray()` |
| Process records while native memory is alive | `MftVolume.StreamRecords` |
| Parse a saved MFT image, no volume or elevation | `MftVolume.StreamMftFromFile` |
| Resume from a persisted journal cursor | `MftVolume.ReadUsnJournal` |
| Continuously receive changes | `WatchUsnJournal` or broker batches |
| Explain a rescan the change journal forced, at open or mid-watch | `DriveStatus.CheckpointLoss` |

## Quick start: find records by name

Run the application as Administrator when using `MftVolume` directly.

```csharp
using MFTLib;

using var volume = MftVolume.Open("C");
using var result = volume.StreamRecords(
    ".git",
    MatchFlags.ExactMatch | MatchFlags.ResolvePaths,
    progress: null, parseThreads: null, CancellationToken.None);
var records = result.ToArray();

foreach (var record in records.Where(record => record.IsDirectory))
    Console.WriteLine(record.FullPath);

Console.WriteLine($"Matched {records.Length:N0} of {result.TotalRecords:N0} records; {result.Timings}");
```

`MftVolume.Open` accepts a drive letter (`"C"`, `"C:"` or `"C:\\"`), a raw device path
(`\\.\C:`) or a volume GUID path (`\\?\Volume{guid}`). `MftResult.Timings` reports the native I/O, fixup, parse
and total durations as `TimeSpan` values; time your own `ToArray()` if you want the copy cost.

## Core MFT workflows

### Read a complete volume index

```csharp
using var volume = MftVolume.Open("C");
using var result = volume.StreamRecords(
    filter: null, MatchFlags.ResolvePaths, progress: null, parseThreads: null, CancellationToken.None);
var records = result.ToArray();

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
using var exact = volume.StreamRecords(
    "report.pdf", MatchFlags.ExactMatch, progress: null, parseThreads: null, CancellationToken.None);
using var containing = volume.StreamRecords(
    "report", MatchFlags.Contains | MatchFlags.ResolvePaths,
    progress: null, parseThreads: null, CancellationToken.None);
```

`ExactMatch` and `Contains` are case-insensitive. A filter with neither flag throws
`ArgumentException` before any native call. Add `ResolvePaths` only when full paths
are needed; path resolution has additional CPU and memory cost.

`MatchFlags.IncludeFreed` opts a scan into returning freed base records whose
attributes still validate. These rows have `InUse == false` and retain the stored
`SequenceNumber`; extension records are skipped. The default scan returns only
in-use records.

Combine it with `MatchFlags.ResolvePaths` to resolve freed records' paths. Every
parent reference, including the root, must name a directory record and match its
stored sequence. A freed parent also accepts a reference one sequence behind, with
16-bit wraparound. Missing or non-directory parents, reused records, cycles, and
chains longer than 128 components leave `FullPath` null and preserve the bare
`FileName`. Live records
keep their existing path behavior. Name filters work with `IncludeFreed`.

```csharp
using var result = MftVolume.StreamMftFromFile(mftFilePath, null,
    MatchFlags.IncludeFreed | MatchFlags.ResolvePaths);
```

The flag is available through the two streaming scan APIs, `StreamRecords` and
`StreamMftFromFile`. Both accept progress, thread allowance, and cancellation
execution controls (`StreamMftFromFile` via `MftFileScanOptions`). `MFTLib.Index`
and the broker block scan remain a live-files index.

### Stream to reduce managed allocations

```csharp
using var result = volume.StreamRecords(
    filter: ".git",
    MatchFlags.ExactMatch | MatchFlags.ResolvePaths,
    progress: null, parseThreads: null, CancellationToken.None);

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

`MftResult.ToArray()` returns records whose strings are already materialized into managed
memory.

### Tune scan buffers

```csharp
// Number of MFT records per native buffer. Default: MftVolume.DefaultBufferSizeRecords (262,144).
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

var armedCursor = volume.QueryUsnJournalCursor();
using var result = volume.StreamRecords(
    filter: null, MatchFlags.ResolvePaths, progress: null, parseThreads: null, CancellationToken.None);
var records = result.ToArray();
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
await foreach (var (entries, cursor) in volume.WatchUsnJournal(
    persistedCursor,
    cancellationToken))
{
    foreach (var entry in entries)
        Console.WriteLine($"{entry.Reason}: {entry.FileName}");

    PersistCursor(cursor);
}
```

The watch blocks in the kernel without polling. Cancelling the token calls `CancelIoEx`
to release the pending read. Each batch includes its post-batch cursor for persistence.

USN entries include record and parent IDs, USN, UTC timestamp, reason flags, file
attributes, filename, and convenience flags such as `IsCreate`, `IsDelete`, `IsRename`,
and `IsClose`. A journal entry contains the changed name and parent ID, not an eagerly
resolved full path; maintain an index keyed by record number when full paths are needed.

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

A loss found mid-session sits alongside `WatchFailureMessage` and
`WatchCatchUp`. It answers the question those two cannot: the watch did not
merely stop, the journal moved past where it had reached. `Drive` and `Apply`
faults recover by rescanning automatically; `Channel` faults do not. A scan-time
loss records `JournalCheckpointLossDetection.ScanCatchUp`, publishes the complete
but unresumable block, and retries the scan up to
`FileIndex.LostCatchUpRecoveryLimit`. Subscribe to `FileIndex.WatchFaulted` to
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
    if (drive.CheckpointLoss is
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
    Console.Error.WriteLine(drive.WatchFailureMessage);
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

`Cause` separates the two situations MFTLib can actually tell apart.
`CheckpointTrimmed` means the journal is the one the checkpoint came from and has
trimmed past it, so `SizeThatWouldHaveRetained` says the size a journal would
need to be at least to have kept the checkpoint, when that size fits in a
`long`; it is null when it does not. `JournalRecreated` means the journal
was deleted and recreated and carries a different id, so the checkpoint refers to
a journal that no longer exists: no size would have helped, and none is offered.
A consumer decides what to say from `Cause`, not from whether the size is null,
since both causes can leave it null. `Cause` and `DetectedDuring` answer
different questions and are read together: `Cause` says whether a journal size
would have helped, `DetectedDuring` says whether the drive needs anything done
about it now. A drive that warm-started, whose watch has never lost its
position, or whose volume could not answer the query at all, reports
`CheckpointLoss` as null rather than guessing.

Turning that into the user's choice is the consumer's job: show the size,
say what the journal is now, and let the user decide whether a journal that large
is worth it or whether an occasional rescan is cheaper. Scans are fast by design,
so a rescan is an acceptable outcome, not a failure.

MFTLib never changes the journal on its own:
enlarging it is an explicit call, `BrokerProcess.GrowUsnJournalAsync`,
which the broker performs elevated and which only grows, refusing a requested
maximum at or below the current one. Growing is persistent and shared with
every other journal consumer on the volume (Windows Search, backup and
replication agents), so surface it as a user action, not a startup default.

## Keep the application non-elevated

For desktop applications and long-running tools, use the elevated broker instead of
running the entire process as Administrator. One `BrokerProcess` owns the control pipe
for the consumer session. Each scan and each drive watch gets its own drive pipe, so a
slow, stopped, or failed drive does not end another drive's operation.

At minimum, the application must dispatch broker mode before normal startup:

```csharp
if (ElevatedEntryPoint.TryHandle(
        Environment.GetCommandLineArgs(),
        new DefaultElevatedEntryRunner()))
{
    return;
}
```

The non-elevated side launches one broker process, then shares it with the index adapters:

```csharp
await using var broker = await BrokerProcess.LaunchAsync(
    BrokerLauncher.Launch,
    cancellationToken);

_ = broker.Ended.ContinueWith(
    ended => Console.Error.WriteLine($"Broker ended: {ended.Result}"),
    TaskScheduler.Default);

Task<BrokerProcess> ConnectBrokerAsync(CancellationToken _) =>
    Task.FromResult(broker);

var brokerAdapter = new BrokerMftBlockProducer(ConnectBrokerAsync);
var drive = IndexedDrive.FromWindowsVolume("C:");
var options = new FileIndexOptions
{
    Drives = [drive],
    CacheDirectory = cacheDirectory,
    MftSource = brokerAdapter.CreateIndexSource()
};

await using var index = await FileIndex.OpenAsync(options, cancellationToken);
```

The adapter's connection callback does not transfer ownership: the application keeps and
disposes the shared `BrokerProcess`. `BrokerProcess.QueryVolumeAsync`,
`GrowUsnJournalAsync`, and `ScanDriveAsync` are the direct broker entry points. A block
returned by `ScanDriveAsync` belongs to the caller and must be disposed:

```csharp
NtfsVolumeInformation volume =
    await broker.QueryVolumeAsync(drive.DriveLetter, cancellationToken);

var scan = await broker.ScanDriveAsync(
    drive.DriveLetter,
    new BlockScanTarget(directBlockPath, drive.VolumeSerial, true),
    new BrokerScanOptions(),
    cancellationToken);
using var directBlock = scan.Block.Block;

UsnJournalSettings grown = await broker.GrowUsnJournalAsync(
    drive.DriveLetter,
    requestedMaximumSize,
    requestedAllocationDelta,
    cancellationToken);
```

Journal growth is an explicit user action; the broker refuses a requested maximum at or
below the current value. `Ended` completes with the reason when the control pipe is lost. See the [broker integration guide](https://github.com/mtschoen/MFTLib/blob/main/docs/broker-integration.md)
for startup dispatch, direct scans, watch channels, recovery, and diagnostics.

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
await index.StopWatchingAsync('C', cancellationToken);
```

The batched forms run the requested drives concurrently and return one
`DriveOperationResult` per drive in request order. One drive's `Failed` result does not
discard the others. `NotApplicable` means the operation has nothing to do for that
drive, such as starting an enumeration-backed drive or waiting on a drive with no watch.

```csharp
IReadOnlyList<char> driveLetters = ['C', 'D'];

IReadOnlyList<DriveOperationResult> starts =
    await index.StartWatchingAsync(driveLetters, cancellationToken);
IReadOnlyList<DriveOperationResult> catchUps =
    await index.WaitForCatchUpAsync(driveLetters, cancellationToken);
IReadOnlyList<DriveOperationResult> rescans =
    await index.RescanAsync(driveLetters, cancellationToken);
IReadOnlyList<DriveOperationResult> stops =
    await index.StopWatchingAsync(driveLetters, cancellationToken);
```

Omit the drive list to apply a batched call to every configured drive in
`FileIndexOptions.Drives` order. Batched calls throw for invalid input, disposal, and
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
arrive out of order; `SettledCount` gives the order. The report
carries the drive letter, `SettledCount` (this drive was the n-th to settle,
counted from 1), and the total configured drive count, so a consumer can render "3 of 9 drives settled" while the open
is still in flight. Keep the report with the largest `SettledCount`, not simply
the last callback to arrive. A declined or failed drive still counts toward the total and
still reports. Block ordinals follow the same order for drives with a block;
`FileIndex.Drives` keeps the configured order. It defaults to null, which reports
and allocates nothing, and it fires on warm starts too, unlike
`FileIndexOptions.Progress`, which samples only while a producer runs.
`RescanAsync` stays silent: its caller already awaits the one drive it rescans.
Marshalling belongs to the `IProgress<T>` implementation, the same convention
`FileIndexOptions.Progress` uses.

A cold drive that loses its journal catch-up is scanned again by its own settle,
up to `FileIndex.LostCatchUpRecoveryLimit` times in a row, and settles `Ready`
with its last block unresumable if every attempt lost it. `OpenAsync` raises no
`WatchFaulted` event because the caller cannot subscribe before it returns; inspect
`DriveStatus.ConsecutiveLostCatchUps`, `CheckpointLoss`, `WatchFailureMessage`, and
`WatchCatchUp` instead. The drive's watch is refused until a manual `RescanAsync`
produces a resumable block. The refusal retains the watch request, so a successful
rescan clears the refusal and starts the watch.

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

`WatchFaulted` reports a `WatchFault` containing `Kind`, `DriveLetter`, and the original
`Exception`:

- `Subscriber`: a `Changed` handler threw. The drive keeps watching, and this is announced
  once for that drive's current watch.
- `Drive` or `Apply`: the watch or batch application failed. The drive publishes
  `WatchCatchUpState.Recovering` before the event, rescans itself, and starts a fresh watch
  from the replacement block.
- `CatchUpLost`: a scan completed, but the journal no longer held the cursor armed before
  it. The exception is `JournalCatchUpLostException`; the drive records a
  `JournalCheckpointLossDetection.ScanCatchUp` report and rescans itself while its
  consecutive count is below `FileIndex.LostCatchUpRecoveryLimit`.
- `Channel`: the drive pipe was lost or ended without a stop. This fault does not recover
  automatically.
- `RescanRestart`: a rescan replaced the block but its watch could not start. The scan
  succeeds; the exception and `WatchFailureMessage` identify the rescan and the exception's
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
                $"Drive {fault.DriveLetter}: {status.WatchCatchUp == WatchCatchUpState.Recovering}");
            break;

        case WatchFaultKind.CatchUpLost
            when fault.Exception is JournalCatchUpLostException lost:
            Console.WriteLine(
                $"Drive {fault.DriveLetter}: loss {status.ConsecutiveLostCatchUps}, " +
                $"stopped {lost.RecoveryStopped}, report {status.CheckpointLoss?.DetectedDuring}");
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
`FileIndex.WatchStateChanged`: it reports every change of a drive's `WatchCatchUp` with the
drive's next `DriveStatus.WatchStateVersion`, before the `WatchFaulted` of the fault that
caused it, so a recovery reads `Recovering`, then `CatchingUp`, then `CaughtUp` with no
polling. One drive's events arrive in version order; a consumer that also reads `Drives`
applies an event only when its version is newer than the last it applied for that drive. When a lost catch-up
reaches the retry limit, `JournalCatchUpLostException.RecoveryStopped` is true, the drive
keeps its last queryable block, and its watch is refused until a manual rescan succeeds.

The single-drive `StopWatchingAsync` rethrows the watch's outstanding fault once. A
batched stop returns that exception in the affected drive's `DriveOperationResult`.
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
the control pipe completes the `BrokerProcess.Ended` task with the reason and
fails pending operations with `BrokerChannelLostException`. Losing a drive pipe faults
only that drive. Idle control and watch pipes, queued scans, and processing operations
that recently reported progress receive heartbeats. A processing operation with no
progress past the processing limit receives `Stalled` and its channel is cancelled. A
pipe with a write already in flight is skipped by the heartbeat sender; if it remains
silent, the client's 30-second no-frame limit ends that pipe.

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

MFTLibTestExtensions is additionally required to have complete line, branch, and executable-method coverage. The publisher checks the assembly's raw Cobertura line hits, branch counts, and method records; partial coverage, absent evidence, and malformed evidence fail closed even when aggregate coverage would pass. The existing aggregate baseline rule remains in force for the whole report. Run `pwsh -NoProfile -File scripts/test-coverage-status.ps1` for the offline publisher regression checks.

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
