# MFTLib

MFTLib is a .NET library for building file indexes, search tools, backup catalogs and
filesystem monitors. On a Windows NTFS volume it reads the Master File Table (MFT) directly
for a fast snapshot, then follows the USN change journal to keep that snapshot current
without walking the filesystem again. The same packed, memory-mapped index can also be
built from a saved MFT dump, or by walking a directory tree on any filesystem, including on
Linux. MFTLib reads file metadata, never file contents. The public API is managed C#;
volume I/O and record parsing run in a native C++ core.

## Platforms and requirements

- .NET 10 or later, in an x64 process.
- Windows x64 for live NTFS volumes. An in-process scan with `MftIndexSources.FromLocalVolumes`
  needs an elevated process. Live USN watching runs through `BrokerSession`, which keeps your
  application unelevated and performs volume access in one elevated child process.
- Windows or Linux x64, with no elevation, for saved MFT dumps (`MftIndexSources.FromMftDumpFile`)
  and for directory-walk indexing (`ProducerKind.Enumeration`).
- The Windows native library requires plain x64 (SSE2 baseline); AVX and AVX2 are not required.

Windows-only members carry `[SupportedOSPlatform("windows")]`. Target `net10.0-windows`, or guard
those calls with `OperatingSystem.IsWindows()` in a cross-platform project.

The package carries `runtimes/win-x64/native/MFTLibNative.dll` for Windows and
`runtimes/linux-x64/native/libMFTLibNative.so` for Linux. SDK-style .NET consumers
resolve the native library through the package's runtime assets. Builds and RID-less
publishes retain the runtime directories; RID-specific publishes place the selected
native library beside the application. The Linux library is built on Ubuntu 24.04
and requires glibc 2.33 and GLIBCXX_3.4.22.

## Install

```bash
dotnet add package MFTLib --version 0.3.0
dotnet add package MFTLib.TestExtensions --version 0.3.0
```

`MFTLib.TestExtensions` is a separate package for your test projects only. It provides an
in-process broker, scripted watches, synthetic cache blocks and synthetic journal records, so
tests exercise the production code paths without elevation or a real volume.

## Choose an integration model

| Scenario | Recommended API |
| --- | --- |
| Already elevated; scan once | `MftIndexSources.FromLocalVolumes` with `CacheMode.Disabled`, no watch |
| Saved MFT image on Windows or Linux | `MftIndexSources.FromMftDumpFile` as the only option set |
| Unelevated app, one UAC prompt, live changes | `BrokerSession.CreateIndexSource`, then `StartWatchingAsync` and `Changed` |
| Any directory tree on any filesystem | `IndexedDrive` with `ProducerKind.Enumeration`; refresh by rescanning |
| Warm start across runs | A persistent `CacheDirectory`; each cached block records its journal position |

## Quick start: query a live volume once

For a process already running elevated on Windows that needs a read-only snapshot, with no
broker, no watch and no cache directory:

```csharp
using MFTLib;
using MFTLib.Index;

var cancellationToken = CancellationToken.None;
var drive = IndexedDrive.FromWindowsDrive('C');
var options = new FileIndexOptions
{
    Drives = [drive],
    CacheMode = CacheMode.Disabled,
    MftSource = MftIndexSources.FromLocalVolumes()
};

await using var index = await FileIndex.OpenAsync(options, cancellationToken);
var status = index.Drives.Single();
if (status.State != DriveState.Ready)
{
    throw new InvalidOperationException(
        status.Failure is { } failure
            ? $"Drive {status.Drive} failed ({failure.Kind}): {failure.Message}"
            : $"Drive {status.Drive} is {status.State}.");
}

var query = new SearchQuery("*.log", NameMatchMode.Glob, IsDirectory: false);
foreach (var entry in index.Enumerate(query, cancellationToken).Take(20))
{
    var observedSize = entry.IsSizeKnown ? $"{entry.Size} bytes" : "size unknown";
    Console.WriteLine($"{entry.Path} ({observedSize}, last write observed {entry.LastWriteTime:u})");
}
```

- `IndexedDrive.FromWindowsDrive('C')` reads the volume serial from Windows and throws
  `IOException` when Windows cannot report it.
- Check `DriveStatus.State` before treating zero results as an empty drive. An unelevated
  process settles the drive `Failed` with `DriveFailureKind.ProducerFailed` rather than throwing.
- One open is one scan. Run as many queries as you like against it, and finish every
  enumeration inside the `await using` scope. Sizes and times are what the scan observed.

## Quick start: query a saved MFT dump

The dump source loads one saved `$MFT` file as a virtual drive, in process, on Windows or
Linux, with no elevation. You choose the drive letter; entries render paths under `dump:/D`.
The source supplies its own drive and is never cached. Supply the source through
`MftSource`; `Drives` must be a non-null empty collection, `CacheMode` and
`CacheDirectory` must be null, and `CacheTag` must be default. Other values throw
`ArgumentException`. Explicit empty/default values are accepted.

```csharp
using MFTLib;
using MFTLib.Index;

var filePath = @"C:\dumps\volume.mft";
var cancellationToken = CancellationToken.None;
var options = new FileIndexOptions
{
    MftSource = MftIndexSources.FromMftDumpFile(filePath, 'D')
};

await using var index = await FileIndex.OpenAsync(options, cancellationToken);
var status = index.Drives.Single();
if (status.State != DriveState.Ready)
{
    throw new InvalidOperationException(
        $"Dump scan failed ({status.Failure?.Kind}): {status.Failure?.Message}");
}

var query = new SearchQuery("Notes.txt", NameMatchMode.Exact);
foreach (var entry in index.Enumerate(query, cancellationToken))
{
    var observedSize = entry.IsSizeKnown ? entry.Size.ToString() : "?";
    Console.WriteLine($"{entry.Path}  {observedSize}");
}
```

A dump is untrusted input. A missing, empty or malformed file settles the drive `Failed`
with the reason in `DriveStatus.Failure.Message`. A dump holds metadata only, so
`FileEntry.Open` throws `InvalidOperationException` for its entries.

## Quick start: watch a volume through the broker

An unelevated application dispatches broker mode first, then owns one `BrokerSession`. The
session launches its elevated child on first use, so the UAC prompt appears when the first
scan needs it.

```csharp
using MFTLib;
using MFTLib.Index;

if (ElevatedEntryPoint.RunBrokerIfRequested(Environment.GetCommandLineArgs()))
{
    return;
}

var cancellationToken = CancellationToken.None;
var cacheDirectory = Path.Combine(
    Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "MyApp", "index-cache");

await using var session = new BrokerSession();
var options = new FileIndexOptions
{
    Drives = [IndexedDrive.FromWindowsDrive('C')],
    CacheDirectory = cacheDirectory,
    MftSource = session.CreateIndexSource()
};

await using var index = await FileIndex.OpenAsync(options, cancellationToken);
index.Changed += change => Console.WriteLine($"{change.Kind}: {change.Path}");
index.WatchFaulted += fault => Console.Error.WriteLine(
    $"Drive {fault.Drive}: {fault.Kind}, automatic recovery: {fault.IsRecovering}");

var drives = index.Drives.Select(status => status.Drive).ToArray();
foreach (var result in await index.StartWatchingAsync(drives, cancellationToken))
{
    if (result.Outcome == DriveOperationOutcome.Failed)
    {
        Console.Error.WriteLine($"Drive {result.Drive} did not start: {result.Failure}");
    }
}

await index.WaitForCatchUpAsync(drives, cancellationToken);
Console.WriteLine("Watching. Press Enter to stop.");
Console.ReadLine();
```

- Run the compiled executable, not `dotnet MyApp.dll`: the session relaunches the current
  executable elevated, and that process must reach the dispatch call first.
- `CacheMode.Persistent`, the default, and `CacheMode.ExistingOnly` require a nonblank,
  application-owned `CacheDirectory`; without one, `OpenAsync` throws `ArgumentException`.
  `Persistent` warm-starts from cached blocks and scans what is missing, `ExistingOnly` opens
  only from the cache and never scans, and `Disabled` uses private blocks deleted on close.
  Change the version of `FileIndexOptions.CacheTag` whenever your scan options change the rows
  you keep.
- A journal event carries no size or last-write time, so an event can make a scanned
  observation unknown until a rescan observes it again. Content changes (overwrite, extend,
  truncate) invalidate both; a basic-information change invalidates only the last-write time;
  rename, move and other reasons keep both. Check `IsSizeKnown` and `IsLastWriteTimeKnown`
  independently.
- When `BrokerSession.Ended` completes, the elevated process is gone. Dispose the indexes
  built over that session, then the session, and create new ones.

## Failure channels

- **`DriveStatus.Failure`** reports why opening or scanning a drive failed: `CacheDeclined`,
  `CacheIdentityMismatch`, `InUse`, `ProducerFailed` or `MftSourceMissing`. A failed drive accepts
  `RescanAsync`, except that `MftSourceMissing` stays failed while `FileIndexOptions.MftSource` is null.
- **`DriveOperationResult.Failure`** carries per-drive failures of the four lifecycle verbs.
  Each verb returns one result per requested drive, in request order, and one failure never
  discards the others. `StopWatchingAsync` returns an outstanding watch fault once as a
  `Failed` result.
- **`FileIndex.WatchFaulted`** reports live watch faults. `WatchFault.IsRecovering` says
  whether the drive is rescanning itself, and `FileIndex.WatchStateChanged` reports each
  state change until it is `CaughtUp` again.
- **Exceptions** are for caller errors: invalid options, unknown or duplicate drive keys,
  a lifecycle call from inside an index event handler, cancellation and use after disposal.

## Samples

`SampleProgram.Direct` runs one-shot queries over a live volume or a saved dump: `search`,
`tree`, `children`, `open`, `largest`, `duplicate-names` and `scan`. `SampleProgram.Watch` uses the broker:
`scan`, `watch`, `rescan`, `journal`, `cache` and `elevation-status`. With no arguments
it watches every ready NTFS fixed drive until a key press.

```powershell
.\SampleProgram.Direct\bin\x64\Release\net10.0\SampleProgram.Direct.exe search C --name notes
.\SampleProgram.Watch\bin\x64\Release\net10.0\SampleProgram.Watch.exe watch C
```

Run the compiled executables so they can request elevation. Set `MFTLIB_SAMPLE_UNATTENDED=1`
to skip every prompt.

## Where to go next

- [Broker integration guide](https://github.com/mtschoen/MFTLib/blob/main/docs/broker-integration.md): session ownership, cached opens, watches, faults and journal growth.
- [Broker scan sizing](https://github.com/mtschoen/MFTLib/blob/main/docs/broker-scan-tuning.md): block capacity, concurrent parse threads and catch-up tuning.
- [Testing your integration](https://github.com/mtschoen/MFTLib/blob/main/docs/broker-testing.md): the `MFTLib.TestExtensions` harness, scripted watches and synthetic blocks.
- [Checkpoint loss](https://github.com/mtschoen/MFTLib/blob/main/docs/checkpoint-loss.md): why a drive rescanned instead of resuming, and what journal size would have helped.
- [Watch lifetime](https://github.com/mtschoen/MFTLib/blob/main/docs/watch-lifetime.md): start, stop, rescan, recovery and event ordering.
- [Query lifetime](https://github.com/mtschoen/MFTLib/blob/main/docs/query-lifetime.md): snapshot borrows, row views, entry handles and paths.
- [Block format](https://github.com/mtschoen/MFTLib/blob/main/docs/index-format.md): cache files, cache identity, freed records and row columns.
- [Block ownership](https://github.com/mtschoen/MFTLib/blob/main/docs/block-ownership.md): cache directory protection, owner locks and safe cache deletion.
- [Broker lifetime](https://github.com/mtschoen/MFTLib/blob/main/docs/broker-lifetime.md): pipes, heartbeats, stall limits and diagnostics configuration.
- [Elevation](https://github.com/mtschoen/MFTLib/blob/main/docs/elevation.md): attended and unattended sample runs.
- [Linux package](https://github.com/mtschoen/MFTLib/blob/main/docs/linux-package.md): which native library ships and how it is checked.
- [API decisions](https://github.com/mtschoen/MFTLib/blob/main/docs/api-decisions.md): the reasons behind the public surface.

## Building from source

On Windows:

- Install Visual Studio 2022 with the Desktop development with C++ workload, and the .NET 10 SDK.
- Run `.\init.ps1 -Build`, or `.\scripts\build-windows.ps1` to build without checkout
  initialization. The default is Release|x64; pass `-Configuration Debug` for Debug|x64.
- Run `.\scripts\run-coverage.ps1 -NonInteractive` for the tests with coverage. See
  [coverage procedures](https://github.com/mtschoen/MFTLib/blob/main/docs/development-coverage.md).

On Linux:

- Install the .NET 10 SDK, cmake, ninja and g++.
- Run `./init.sh --build` to build the native library and the managed projects.
- Run `bash scripts/coverage-linux.sh` for the tests with coverage.

The [architecture contracts](https://github.com/mtschoen/MFTLib/blob/main/docs/architecture.md)
describe the components and the source layout.
Maintainers follow the [0.3.0 release runbook](https://github.com/mtschoen/MFTLib/blob/main/docs/handoff-release-0.3.0.md)
for release validation, publication and consumer bridge retirement.

## License

[MIT](https://github.com/mtschoen/MFTLib/blob/main/LICENSE.txt)
