# Changelog

## 0.3.0

MFTLib 0.3.0 replaces the 0.2 record arrays with one file index. `FileIndex` builds a packed,
memory-mapped index per volume from a live volume through the elevated broker, an in-process scan
of a live volume, a saved MFT dump, or a directory walk. It keeps MFT-backed volumes current from
the USN journal and caches blocks between runs. The MFT scanner, journal reader and native parser
are internal; applications reach them through `FileIndex`, `MftIndexSources` and `BrokerSession`.
Test support ships as the separate `MFTLib.TestExtensions` package.

### Added

- `FileIndex` in `MFTLib.Index` holds one root: `OpenAsync`, `Status` for that root, and the queries `Search`, `Enumerate`, `EnumerateRows`, `Find` and `Root`.
- `FileIndexSet` answers `Search`, `Enumerate`, `EnumerateRows` and `Find` across several indexes, so an application that holds a volume, a walked folder and a dump together queries them as one.
- `FileEntry` handles with `Name`, `Path`, `Size`, `LastWriteTime`, `Attributes`, `Parent`, `Children()` and `Open`, with `IsSizeKnown` and `IsLastWriteTimeKnown` for values a scan could not observe.
- `SearchQuery`: exact, substring and glob name matching, case sensitivity, subtree, file or directory, size and last-write bounds, and deleted rows.
- `EnumerateRows` yields non-allocating `IndexRow` views for whole-root passes.
- Index sources: `BrokerSession.CreateIndexSource`, `MftIndexSources.FromLocalVolumes` for an elevated process, and `MftIndexSources.FromMftDumpFile` for a saved `$MFT` file on Windows or Linux without elevation.
- `VolumeKey` names an NTFS volume by ASCII letter, normalized to upper case, and `VolumeKey.TryParse` accepts a volume spec such as `C`, `c:` or `C:\`. `RootKey` names any indexed root, a `VolumeKey` or a normalized directory path, with `IsVolume`, `TryGetVolume`, `FromDirectory`, `TryParse` and ordering by `CompareTo`; it has no `Letter`.
- `IndexedRoot.FromDirectory` builds a directory-walk root and derives its key and its volume serial (the containing volume's serial, or zero when the platform does not report one). There is no public constructor for an arbitrary key and serial.
- `IndexedRoot.Producer` selects an MFT scan or a directory walk for the root. `IndexedRoot.FromWindowsDrive` builds an MFT volume from a `VolumeKey`.
- `IndexedRoot.EnumerateWindowsVolumes()` returns `IReadOnlyList<WindowsVolume>`, a record of `Volume`, `RootDirectory`, `DriveType`, `IsReady`, `Format`, `IsIndexable` and a display `Reason`. Read `IsReady` and `Format` instead of matching the reason text.
- Live USN watching per volume: `StartWatchingAsync`, `StopWatchingAsync`, `WaitForCatchUpAsync` and `RescanAsync` each take a cancellation token and return one `RootOperationResult` for the index's one root.
- `Changed` raises a `FileChange` with kind, path, previous path and journal timestamp. `WatchFaulted` raises a `WatchFault` with its kind and `IsRecovering`. `WatchStateChanged` reports every watch state change with a per-volume version.
- Root and apply faults recover automatically by rescanning the affected root.
- Cache blocks store their root key in page 0 (block format 8); a directory root is cached under a hashed file name (`D` plus 16 hexadecimal digits, then the serial) and a block whose name is not the one its stored key derives is discarded and rescanned.
- Persistent block cache: `FileIndexOptions.CacheMode`, `CacheDirectory` and `CacheTag`, plus `CacheDirectory.EnsureCreated`, `InspectCached` and `DeleteCached`. A per-block owner lock keeps two live indexes from sharing a cache block.
- `FileIndex.Status` reports the root's `RootState`, `RootFailure`, `RootBlockStatus` and `RootWatchStatus`. A missing MFT source reports `RootFailureKind.MftSourceMissing` with a volume-specific library message; applications choose their own contextual wording.
- `RootWatchStatus.CheckpointLoss` explains a rescan forced by the journal moving past a volume's position, with the journal size that would have retained it. `JournalCheckpointLoss.TryGetGrowthTarget`, `FileIndex.QueryUsnJournalSettings()` (the journal of the index's own root volume) and `BrokerSession.GrowUsnJournalAsync` support an explicit journal growth.
- `FileIndexOptions.ScanProgress` and `Diagnostics` report scan progress and block-file deletions.
- `MftScanOptions.IncludeFreed` imports freed MFT records as deleted rows.
- `FileIndexOptions.DirectoryWalk` takes a `DirectoryWalkOptions` with `PrunedDirectoryNames`, `PrunedDirectoryNamePatterns`, `PrunedDirectoryPaths`, `PrunedDirectoryAttributes` and `PruneDotDirectories`. A pruned directory is recorded but not entered, and the root is never pruned. MFT volumes ignore it.
- Block format 6 stores a library-computed scan shape of the walk and freed-record options, so a warm start under other options cold-scans without a cache tag change, and records `BlockValidationResult.WrongScanShape`. It also stores the walk's access-denied subtree count, so a warm start reports it.
- `BrokerSession` owns one elevated broker process per consumer session. `ElevatedEntryPoint.RunBrokerIfRequested` dispatches broker mode, and `BrokerDiagnostics.Enable` writes per-role diagnostics logs.
- `ElevationUtilities.TryAddDefenderProcessExclusions` adds Windows Defender process exclusions, elevating when needed.
- `MFTLib.TestExtensions`: an in-process broker harness, scripted watches, synthetic cache blocks, synthetic journal records and journal isolation for consumer tests.
- The package carries a Linux x64 native library, requiring glibc 2.33 and GLIBCXX_3.4.22, and XML documentation for the whole public surface.
- The Windows x64 native DLL statically links the Visual C++ runtime, so a consumer needs no redistributable. Release package validation inspects the packaged DLL with `dumpbin /dependents` and rejects `MSVCP*` and `VCRUNTIME*` imports.

### Changed

- Targets .NET 10; 0.2 targeted .NET 8.
- The MFT record size is detected at runtime instead of assumed to be 1024 bytes. A live volume accepts power-of-two sizes from 512 through 65536 bytes, and a saved dump accepts 1024 or 4096.
- Elevation runs through `IElevationProvider`, reached by `ElevationUtilities.DefaultProvider`. `TryRunElevated` takes an argument list and a `TimeSpan`, and passes each argument separately.
- Self-elevation returns false without attempting UAC when no interactive desktop is available, such as in CI or a service.
- Native ABI 11 passes a caller-supplied context pointer, then `recordsScanned`, `totalRecords` and `elapsedMs`, to the progress callback for volume and dump parses. `mft_api.h` is the shared declaration source for all production native exports. Consumer submodule updates rebuild and deploy the native library and managed assembly from the same revision.
- Block format 8 stores each distinct name once, by exact spelling, in an ASCII-or-UTF-16 byte pool with an in-block hash table. Rows hold a name identifier, name reads are validated when they happen instead of at open, and a query resolves its matching names once before testing rows. A cache block written by an earlier format is rebuilt on its first open. Directory root keys now hold at most 1,967 UTF-16 code units.

### Removed

- The public `MftVolume`, `MftResult`, `MftRecord`, `MftParseTimings`, `MatchFlags` and `MftPathUtilities` types; scans run through index sources and are queried through `FileIndex`.
- `ElevationUtilities.IsElevated`, `CanSelfElevate`, `TryRunElevated`, `GetProcessPath` and `DefaultElevatedTimeout`.
- `FileIndexOptions.RetainedFileNames` and the Watch sample's `--keep-name` and `--directories-only` flags; every producer writes every eligible record.
- Name filtering and path resolution in the native parser; queries filter names and `FileEntry.Path` renders paths.

### Fixed

- A directory-walk block that runs out of rows or name pool grows in place by doubling both capacities while the walk continues, so the index is never silently truncated and no directory is read twice.
- A native failure inside the parser, including a failed allocation or thread start, fails the scan instead of terminating the host process.
- A volume scan fails instead of returning a partial inventory when the `$MFT` attribute list or a `$DATA` extension record is unreadable or invalid.
- The parser validates each record's update sequence array before decoding it, so a record with an invalid fixup is never read as data.
- `Microsoft.SourceLink.GitHub` 10.0.401 moves the transitive `Microsoft.Build.Tasks.Git` past CVE-2026-62900.

### Migrating from 0.2

- `MftVolume.Open` with `ReadAllRecords`, `FindByName` or `StreamRecords` -> `FileIndex.OpenAsync` over `MftIndexSources.FromLocalVolumes()` or `BrokerSession.CreateIndexSource()`, then `Search`, `Enumerate` or `EnumerateRows`
- `MftVolume.ParseMFTFromFile` -> `MftIndexSources.FromMftDumpFile(filePath, volume)`
- `MftVolume.FindDirectories`, `FindFiles` and `FindRecords` -> `SearchQuery` with `IsDirectory` true, false or null
- `MatchFlags.ExactMatch` and `MatchFlags.Contains` -> `NameMatchMode.Exact` and `NameMatchMode.Substring`
- `MatchFlags.ResolvePaths`, `MftRecord.FullPath` and `MftPathUtilities.ResolvePath` -> `FileEntry.Path`
- `MftRecord` -> `FileEntry`: `FileName` -> `Name`, `RecordNumber` -> `RecordKey.RecordNumber`, `ParentRecordNumber` -> `Parent`, `InUse` -> `!IsDeleted`
- `MftResult.ToArray` and `MftRecord.Materialize` -> `FileIndex.Search`
- `MftResult.Timings` and `MftParseTimings` -> removed; `FileIndexOptions.ScanProgress` reports scan progress
- `bufferSizeRecords` parameters -> removed
- `MftScanOptions.RetainedFileNames` and `FileIndexOptions.RetainedFileNames` -> removed; every producer writes every eligible record
- `RootFailureKind.CacheTagMismatch` -> `RootFailureKind.CacheIdentityMismatch`, which also covers a scan-shape mismatch
- A `CacheTag` version bumped for freed-record inclusion -> no bump; the library compares `MftScanOptions.IncludeFreed` as the scan shape
- `MftVolume.GenerateSyntheticMFT` -> internal test support, used by MFTLib's own tests and Benchmark; `SyntheticBlock` and `SyntheticMftProducer` in `MFTLib.TestExtensions` build index cache blocks, not MFT dumps
- `ElevationUtilities.IsElevated()` and `CanSelfElevate()` -> `ElevationUtilities.DefaultProvider.IsElevated()` and `CanSelfElevate()`
- `ElevationUtilities.TryRunElevated(string arguments, int timeoutMs)` -> `ElevationUtilities.DefaultProvider.TryRunElevated(IReadOnlyList<string> arguments, TimeSpan timeout)`
- `ElevationUtilities.GetProcessPath()` -> `Environment.ProcessPath`

## 0.2.0

### Breaking Changes

- Removed `MFTParse` class (use `MftVolume.ReadAllRecords()` or `MftVolume.FindByName()` instead)
- Removed `MftFileEntry` interop struct (internal; replaced by `MftRecord`)
- Replaced `uint matchFlags` with `MatchFlags` enum (`ExactMatch`, `Contains`, `ResolvePaths`) across all public APIs
- `FileUtilities`, `MFTUtilities`, and `Kernel32` are now internal
- `FindByName` now takes `MatchFlags` instead of `bool exactMatch` / `bool resolvePaths` (e.g. `FindByName(".git", MatchFlags.ExactMatch | MatchFlags.ResolvePaths)`)
- `MftVolume.BufferSizeRecords` settable property replaced with `bufferSizeRecords` parameter on `MftVolume.Open()`
- Removed `EnsureElevated()` (use `CanSelfElevate()` + `TryRunElevated()` instead)
- Removed `MftVolume.ResolvePath(ulong)` (use `ReadAllRecords(resolvePaths: true)` or `MftPathUtilities.ResolvePath()` with a lookup)
- Removed `MFTUtilities.GetFileNameForDriveLetter()` (use `MFTUtilities.GetVolumePath()`)
- Removed unused `Kernel32` P/Invoke methods (`CloseHandle`, `ReadFile`, `SetFilePointerEx`, `DeviceIoControl`)

### Improvements

- Added `MatchFlags` flags enum for type-safe filter options
- Added `CanSelfElevate()` and `TryRunElevated(string, int)` to `ElevationUtilities`
- Swappable native function indirection (`MFTLibNative`) for testability
- Swappable dependencies in `ElevationUtilities` for testability
- Extracted `MftVolume.ExtractDriveLetter()` helper
- Replaced magic numbers in `MftResult` with named constants (`NativeEntrySize`, `NativePathEntrySize`)
- Native error messages (e.g. "Volume is not NTFS", allocation failures) are now surfaced as `InvalidOperationException` instead of being silently ignored
- Fixed path resolution when filter is null
- Enabled `ContinuousIntegrationBuild` for deterministic NuGet DLLs

### Tests

- Expanded test suite from 16 to 80+ tests
- Achieved 100% line, branch, method, and full-method coverage
- Added admin-elevated test suite (`MftVolumeAdminTests`)
- Added coverage for `ElevationUtilities`, `MftResult`, `MftRecord`, path resolution, native mock indirection
- Added combined coverage script for admin + non-admin tests
- Tagged interactive UAC tests with `TestCategory("Interactive")`

## 0.1.2

- Initial public release
- MFT parsing with native C++ core
- Path resolution via `MftPathUtilities`
- `MftVolume.ReadAllRecords()` and `FindByName()` APIs
- Filename filtering (exact match or contains, case-insensitive)
- Configurable buffer sizes
- Self-elevation via `ElevationUtilities`
