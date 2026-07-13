# Changelog

## 0.3.0

### Features

- **USN journal support** on `MftVolume`:
  - `QueryUsnJournal()` — get the current journal cursor (`UsnJournalCursor`) to baseline incremental updates after a full scan
  - `ReadUsnJournal(cursor)` — batch catch-up read; returns `(UsnJournalEntry[] Entries, UsnJournalCursor UpdatedCursor)`. Throws `InvalidOperationException` if the journal was recreated or entries were overwritten (caller should fall back to a full rescan)
  - `WatchUsnJournal(cursor, cancellationToken)` — live `IAsyncEnumerable<UsnJournalEntry[]>` event stream; blocks on the kernel (zero CPU) until changes arrive, unblocks via `CancelIoEx` on cancellation
  - `WatchUsnJournalWithCursor(cursor, cancellationToken)` — same as above but yields `(UsnJournalEntry[] Entries, UsnJournalCursor Cursor)` so callers can persist progress without a separate `QueryUsnJournal` IOCTL
- `UsnJournalEntry` exposes `RecordNumber` / `ParentRecordNumber` (48-bit Master File Table (MFT) segment indices matching `MftRecord`), `Usn`, `Timestamp`, `Reason`, `FileAttributes`, `FileName`, plus `IsCreate` / `IsDelete` / `IsRename` / `IsClose` reason helpers
- `UsnJournalEntry.Create(...)` — public factory to reconstruct an entry from already-decoded values (e.g. journal data serialized to disk and rebuilt in another process)
- `MftRecord.FileAttributes` now sourced from `$STANDARD_INFORMATION` (preferred) with `$FILE_NAME` fallback
- Added public `IElevationProvider` interface (with `ElevationUtilities.DefaultProvider`) so consumers can substitute elevation behavior in their own tests
- **VolumeBroker subsystem** — an elevated broker host/client for running MFT scans and USN journal watches through a single UAC session, so a non-elevated caller never needs more than one elevation prompt per process lifetime:
  - `JournalBrokerHost` (elevated side) arms the journal cursor before scanning each drive, scans, and serves catch-up + live-watch requests over a pipe; `JournalBrokerHost.CreateDefault()` wires it to real `MftVolume` access
  - `JournalBrokerClient` (non-elevated side) owns the pipe and per-drive page-file-backed `MemoryMappedFile`s; `ArmScanAndCatchUpAsync` drives the cold scan, `SendStartWatchAsync`/`CreateBatchSource`/`StopLiveWatchAsync` drive the live watch, and `BrokerDied` signals broker death exactly once
  - `BrokerProtocol` — binary frame codec for the pipe (`BrokerFrameKind`, `BrokerFrame`) carrying scan requests, cursors, journal batches, and errors
  - `ScanPayload`/`ScanRecord` — packed binary cold-scan payload written into the shared MMF, read back without a disk round-trip
  - `ElevatedEntryPoint.TryHandle` dispatches the `--broker` (`--pipe`, `--once`, `--diag`) elevated child mode; `BrokerLauncher.Launch` starts it via a non-waiting `runas` relaunch
  - Opt-in `BrokerDiagnostics` frame/event tracing via `MFTLIB_BROKER_DIAG=1` or `BrokerDiagnostics.Enable(role)`, writing to `BrokerDiagnostics.LogDirectory` (consumers point this at their own app-data directory)
  - Live watches can be stopped and restarted on the same client for rescans without launching another elevated process
  - Duplicate live-watch starts are rejected client-side and ignored safely by the host, preventing multiple readers or writers from desynchronizing the shared pipe

### Improvements

- Native path resolution now parallelizes across worker threads (same fan-out as fixup+parse) when `numThreads > 1`, with a serial fallback
- Path name-pool exhaustion is now surfaced via the native `errorMessage` ("Path name pool exhausted; N names dropped, some paths truncated") instead of silently truncating
- Self-elevation now returns `false` without attempting UAC when no interactive desktop is available (for example, CI or a Session 0 service)
- Reorganized managed sources by MFT, journal, broker, elevation, interop, and internal responsibilities; split scan, journal, broker connection, transport, and session behavior into focused partials without changing the public API
- Reworked the README and added a broker integration guide covering installation, API selection, memory lifetime, race-free scan/catch-up, live watch, rescans, recovery, and deployment

### Tests

- 100% native coverage achieved without admin via synthetic seams
- Added USN journal test suites (synthetic, live, and admin-elevated)
- Added VolumeBroker test suites (protocol/payload round-trips, host and client pipe-loop behavior, elevated-entry dispatch, broker-death detection), keeping the repo at 100% managed line/branch/method coverage

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
