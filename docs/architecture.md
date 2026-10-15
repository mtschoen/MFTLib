# Architecture contracts

- **MFTLibNative** (C++ DLL) - Core NTFS MFT parsing logic with multi-threaded parallel fixup+parse and double-buffered I/O. Parses are thread-safe and re-entrant: concurrent parses share no mutable state except the test-hook observation, which each parse replaces once, when it finishes. The test hooks are process-global, so a test reading `GetChunkThreadCounts` or arming a failure injection owns the process while it runs. MFT record geometry (a power-of-two record size from 512 to 65536 bytes, validated in `ntfs.h`) is detected at runtime rather than assumed - `FSCTL_GET_NTFS_VOLUME_DATA` for a live volume, record 0's header for an exported file. Results cross the P/Invoke boundary through compact ABI version 8 (`MFT_NATIVE_ABI_VERSION`): 52-byte `MftCompactEntry` rows with int64 size at offset 32, int64 modified time (FILETIME) at offset 40, uint16 sequence number at offset 48, and uint16 parent sequence number (the sequence the record's name stores in its parent reference) at offset 50, plus separate UTF-16 string pools. Flags bit `0x8000` marks an unknown size. The parser emits each record's own name and parent reference and resolves no paths; `mft_api.h` pins the row layout with static asserts.
- **MFTLib** (C# Library) - Managed wrapper with P/Invoke interop. The `MFTLib.Index` namespace provides a substrate-neutral columnar block format and query engine; see `docs/index-format.md`.
    - **Drive keys**: `DriveKey` owns an ASCII letter, normalizes it to upper case once, and converts
      implicitly from `char`, with no string conversion. `Letter` and `ToString()` reject the invalid
      default key, as do index operations, cache selections and key-bearing record initializers.
      Public key members are `Drive`. Each lifecycle verb has one enumerable-key method returning
      ordered per-drive results; callers supply `Drives.Select(status => status.Drive)` for all drives.
      Cache filenames and broker frames convert at their boundaries and retain their formats.
    - **Public queries**: `FileIndex.Search` materializes `FileEntry` values, `Enumerate` streams them for LINQ,
      and `EnumerateRows` provides ref-struct `IndexRow` views with span names, sizes and flags for whole-drive
      passes that retain few rows. `IndexRow.ToEntry()` creates a handle for a retained row; `IndexRow` exposes
      no block layout, row index or mapped columns. `RowScanner` stays internal. Snapshot borrows and cancellation
      follow the contract in `docs/query-lifetime.md`.
    - **Index namespace boundary**: `MFTLib.Index` depends on nothing in the flat `MFTLib` namespace or in `MFTLib.Interop` beyond an allowlist of journal value types (`UsnJournalEntry`, `UsnJournalSettings`, `UsnReason`). Enforced by `MFTLib.Tests/Index/NamespaceBoundaryTests.cs`, an IL-level ArchUnitNET test over the built assembly, with a mandatory negative-control fixture. Not an aislop rule: the forbidden folders share the flat `MFTLib` namespace, so there is no `using` for an import rule to match. Growing the allowlist is a review decision.
    - **MFT dump drives**: a source built with a dump identity (`MftDumpSourceIdentity`) makes its drive a virtual
      inventory with the root `dump:/{DRIVE}` (upper-case letter), rendered with `/` and read with `/` or `\`,
      case-insensitive on every platform; `MftDumpPaths` owns those rules and touches no filesystem. The block
      carries `DriveBlock.IsMftDump`, and the index never probes the root, opens the entry (`FileEntry.Open` throws
      `InvalidOperationException`), answers journal settings (`QueryUsnJournalSettings` throws) or watches it:
       `DriveWatchStatus.IsSupported` is false, and start, stop and catch-up report
      `NotApplicable` with no failure. Opening validates the options before any cache path is resolved or created: `Drives`, `CacheMode` (every value,
      `Disabled` included), `CacheDirectory` and `CacheTag` must all be unset, each rejection an `ArgumentException` naming the setting. The index
      then configures the identity's one virtual drive itself (the upper-case letter, root `dump:/{DRIVE}`, `VolumeSerial` zero,
      `ProducerKind.Mft`) and opens it as `CacheMode.Disabled`, so `Drives`, `Root(drive)` and the settle see it; a dump block is never written to or adopted from a cache. `DriveWatchStatus.IsSupported` is false for
      a dump drive and for any drive whose source has no watch source, the same fact a watch start's refusal uses.
    - **MFT dump source**: `MftIndexSources.FromMftDumpFile(filePath, drive)` is the only way to build a dump
       source. It checks its path (`A dump file path is required.`) and rejects the default drive key,
       makes the path absolute once and never opens the file; every scan and rescan opens the path anew, so
      a missing or rejected dump is a `ProducerFailed` drive, or a failed rescan that keeps the last block, with the
      reason in `DriveStatus.Failure.Message`. `MftDumpBlockProducer` is a direct producer with no broker, pipe or
      session: one `MftDumpInput` per request sizes the block (`NtfsVolumeInformation(fileLength, recordSize)`, never
      the live volume sharing the letter) and supplies every record, the shared `MftBlockScan.WriteToBlock` writes a
      plain `BlockFile` with full retention, and the block carries a zero journal cursor. Record size is 1024 or
      4096 only. `MftDumpRecordValidation` runs before the block is completed: the root row 5 must be an allocated
      directory whose parent is 5 (`The dump has no valid allocated root record.`), no base record number repeats
      (`The dump contains duplicate base record numbers.`), and every parent a record names fits the planned block
      (`The dump contains required record numbers outside the index range.`). A record whose own number does not fit
      is skipped and counted by the row writer; a record the parser omits for a malformed attribute is not counted.
      Import is allocated records only. Content rejections are `InvalidDataException`, an open failure is `IOException`
      carrying the platform error code, cancellation stays `OperationCanceledException`. Every request is admitted
      through the same process-wide `ParseThreadAllocator` as local-volume scans and carries its changing allowance
      into the native dump parse. Completion, failure and cancellation release the registration and admit queued scans.
    - **Dump files are untrusted input**: every file parse runs on one native core in `mft/mft.dump_input.cpp`
      (`OpenMftDumpInput`, `ParseMftDumpInput`, `CloseMftDumpInput`, UTF-8 paths, exported on every platform; there
      is no other file parse, and it never returns freed records). The file is opened once and sized, and its
      geometry is read from record zero, before anything is parsed: `The dump file is empty.`, `Invalid or
      unsupported MFT record size.`, `File size is not a whole multiple of record size.`. A file cut inside its first header, a failed or short read, or
      a file whose length changed since the open, fails the parse with `The dump file could not be read completely.`
      instead of ending it early; an opened input protects against a replaced path, not against a writer changing
      the file in place. `ApplyFixup` validates the update sequence array before reading or writing through it: the
      array lies after the fixed header and before the first sector's last word, holds exactly one entry per
      512-byte sector plus the sequence number, and every sector's last word equals that number. A record that
      fails is never decoded; on a file an allocated one fails the parse with `The dump contains an invalid MFT
      record fixup.`, on a live volume it is passed over and counted: the parse reports the allocated ones in
      `MftParseResult.invalidFixupRecords`, and the live record source hands that count to the shared block scan
      (an `MftOmittedRecords` batch), which adds it to `DriveBlockStatus.SkippedRecordCount`, so no record disappears
      without a trace. A live volume's record zero is the exception: its decoded `$DATA` runs locate every other
      record, so an invalid fixup there fails the scan with `MFT record 0 has an invalid fixup` before any
      attribute is read. These rejections set `MftParseResult.invalidInput`, which
      `MftResult` throws as `InvalidDataException`; no caller classifies a failure by its text. Message buffers are
      UTF-16 (`MftMessageChar`) on every platform, so the managed struct layouts match the native ones on Linux.
      The volume export `ParseMFTRecordsWithProgress` is compiled on every platform. On Windows its handle is a
      volume (or a file) and `FSCTL_GET_NTFS_VOLUME_DATA` names the record size; elsewhere its handle is a file
      descriptor on an NTFS image or device and the boot sector's `clustersPerFileRecord` names it. The
      `SetVolumeRecordSizeOverride` test hook wins on both, so a synthetic image parses the same way everywhere.
      A failed or short read of an MFT batch is never taken for the end of the MFT: it fails the parse with
      `The volume could not be read completely.` and no entries. The same rejection applies when record zero's
      nonresident `$ATTRIBUTE_LIST` cannot be loaded, or an unnamed `$DATA` extension record is not covered by the
      current validated runs or has a failed or short read. Such an extension with wrong FILE magic, invalid fixup,
      or a malformed attribute chain also fails the scan with no entries and a reason naming the extension
      record. Extension runs are validated before appending them or using them for another record lookup.
      Bootstrap follows only unnamed `$DATA` references in ascending lowest VCN, skips record zero and
      duplicate records, and requires each followed record to be readable and valid through the runs merged
      so far. References for other attribute types or named streams are not read because they contribute
      no runs to the `$MFT`'s unnamed data stream.
    - **Cache directory ownership**: cached opens require an explicit nonblank
      application-owned `FileIndexOptions.CacheDirectory`, even with no drives.
      Missing configuration throws `ArgumentException` before directory creation.
      The library has no default cache directory or default-cache guard.
      Cached opens call `CacheDirectory.EnsureCreated(path)` to create or reprotect
      the directory: current user and SYSTEM only with inheritance blocked on Windows,
      owner-only `0700` on Unix. Applications call it explicitly before writing their
      own sidecars independently of an open; it returns the directory's `DirectoryInfo`.
      See [block ownership](block-ownership.md#preparing-the-cache-directory).
      `CacheMode.Disabled` opens use temporary blocks and ignore supplied cache paths without
      resolving or creating them. Dump validation runs first and prohibits every cache option. An unset `CacheMode` resolves to `Persistent`, or
      to `Disabled` for a dump source; `ExistingOnly` and `Persistent` both require the directory.
      Watch defaults to `<user profile>/.MFTLib.Sample.Watch/cache`, retaining policy
      subdirectories for production and cache inventory/deletion. Without a profile,
      it requires `--cache-directory`. Direct opens `CacheMode.Disabled`.
    - **Consumer cache identity**: `FileIndexOptions.CacheTag` carries an opaque
      four-ASCII-character code plus a `uint` version; default is all zeros and
      compares exactly, not as a wildcard. Block format 4 stores the two values
      at offsets 104 and 108 in a 112-byte declared header. Old-format blocks
      cold-scan once. Consumers bump their own version when their retention policy
      changes. Mismatches report `WrongCacheTag` and cold-scan; a
      cache-only open fails with `DriveFailureKind.CacheTagMismatch`. Both emit
      stored/requested tag diagnostics. `InspectCached` reports tags on available
      blocks. Tags are initialized before completion; the internal MFT producers copy
      `request.CacheTag` into creation options, and the built-in broker adapter
      forwards it automatically. `CacheTag` has no public component accessors: consumers
      construct and compare whole tags, and `MFTLibTestExtensions.SyntheticCacheTag` reads the
      components for policy tests. See `docs/index-format.md` for the contract.

    - **ABI versioning**: `MFTLibNative.EnsureCompatibleNativeAbi()` checks the native ABI version once, before any parse, and `MftResult`'s constructor checks the entry stride of each result; both throw `InvalidOperationException` immediately on a managed/native mismatch instead of decoding mismatched memory. `MftDumpInput.Open` makes the same check before it opens a file.

    - **Native export header**: `MFTLibNative/mft_api.h` declares every production C-linkage export, with the USN read/watch exports guarded by `_WIN32`. The volume and dump parse exports take a `MftProgressCallback` that receives `recordsScanned`, `totalRecords` and `elapsedMs`. Progress runs synchronously on the calling parse thread after each merged chunk; the managed delegate retains its closure state for the full parse. Native and managed binaries are built and deployed together from the same revision under ABI version 8.

    - **Per-drive producer**: `IndexedDrive` carries `(Drive, RootDirectory, VolumeSerial,
      Producer = ProducerKind.Mft)`. Each drive selects its own producer for initial scans
      and rescans, so one index can combine MFT-scanned drives with directory-walked drives.
      `FromWindowsVolume` selects Mft. Enumeration starts at that drive's `RootDirectory`;
      an operational producer failure leaves only that drive `Failed` with
      `DriveFailureKind.ProducerFailed`, for MFT and enumeration alike. Healthy siblings stay usable.
      A failed rescan keeps the previous block and records `DriveStatus.Failure` as `ProducerFailed`;
      a successful replacement clears it. Argument validation at admission (null options, undefined
      `CacheMode`, missing cache directory, dump-source option conflicts) throws before any drive is
      produced. A drive that cannot start production, including an MFT drive needing a scan with a null
      `MftSource`, settles `Failed` / `ProducerFailed` while siblings stay usable. Caller cancellation aborts
      the whole operation, and a cancelled open releases its adopted blocks. An MFT failure never selects enumeration.
      Warm adoption compares the header's `ProducerKind` directly with `IndexedDrive.Producer` before
      cache-tag, root and journal checks. A mismatch closes and best-effort deletes the rejected
      block under the owner lock, then cold-scans; cache-only opens report `CacheDeclined`.
      Enumeration drives have no live watch, even when their MFT siblings use a watch-capable source.
    - **MFT source**: `FileIndexOptions.MftSource` is one `MftIndexSource` carrying the block
      producer and the watch source of every MFT-backed drive, so the two cannot come from
      different connections. `BrokerSession.CreateIndexSource()` builds the broker-backed
      one; `MftIndexSources.Unavailable(reason)` fails every scan with
      `Drive {letter}: {reason}.`, and a watch start reports `NotApplicable`. `MftIndexSources.FromLocalVolumes(scanOptions)` builds the in-process one for a caller
      that is already elevated: `LocalMftBlockProducer` plans capacity from the volume geometry,
      creates the requested block and fills it through `MftBlockScan.WriteToBlock`, stamping the live
      journal cursor armed before a cached scan, with no broker process or pipe. It has no watch source, so watch start and
       catch-up report `NotApplicable`, and `DriveWatchStatus.IsSupported` is false. Concurrent
      direct live-volume and dump scans share the process's parse threads through `ParseThreadAllocator`. Cache reopening
      adopts only when the journal identifier matches and its next USN exactly equals the stamped cursor;
      recreation, trimming or movement causes a rescan, with `JournalAdvanced` reporting movement
      without retention-size hints. An unavailable or incoherent journal observation adopts without loss.
      Cache-only opens keep the snapshot flagged unresumable with its loss report. `CacheMode.Disabled` scans skip
      the cursor query and stamp zero; cached scans fail as `ProducerFailed` without an active journal.
      Dump blocks retain zero cursors and are never cached, so they never reach a cache freshness check. The factory
      opens nothing until the first scan; a process that cannot open the volume fails the drive
      as `ProducerFailed`. A null `MftSource` is a configuration error for a drive with
      `IndexedDrive.Producer == ProducerKind.Mft` when a scan or watch is needed:
      its scan reports `ProducerFailed` with the configuration error message, and a watch start
      reports `NotApplicable` without recording a request. Warm adoption needs no source. Enumeration drives
      ignore it, so an all-enumeration open accepts null and a mixed open isolates missing-source
      scan failures to its MFT drives. The block producer
      delegate, the watch source and drive watch interfaces and their records are internal;
      only MFTLib implements them, and tests build sources through `MFTLibTestExtensions`.
    - **Watch state events**: `FileIndex.WatchStateChanged` reports every change of a drive's
      derived `DriveWatchStatus.State` as a `WatchStateChange` whose `Watch` is the drive's `DriveWatchStatus`
      with a per-drive `StateVersion`, built by the same helper `Drives` uses and noted by one helper inside the
      state-lock section that made the change and delivered with neither `_stateLock` nor a write
      gate held, one drive at a time in version order, before the `WatchFaulted` of the fault
      that caused it. Handlers follow the `Changed`/`WatchFaulted` reentrancy rules. The full
      contract is in `docs/watch-lifetime.md`. A consumer seeds its history from `Drives[i].Watch`, the same
      type an event carries; event faults live only on the event. `WatchFault.IsRecovering` classifies
      recovery associated with a fault notification rather than querying current watch health.
    - **Lazy Materialization**: `MftRecord` stores native pointers; strings are only created on access.
    - **Memory Safety**: `ToArray()` and `Materialize()` ensure strings are stable in managed memory after native buffers are freed.
    - **Streaming API**: `MftVolume.StreamRecords` (a volume) and `MftDumpInput.Parse` (a saved image) are the only scan entry points. Both support the same progress, `ParseThreadAllowance` and cancellation controls (supplied directly on `StreamRecords` or grouped into `MftFileScanOptions` for `MftDumpInput.Parse`) and share one managed path (`ParseWithControl` allocates the native `MftParseControl` block, attaches the allowance, registers the token and frees it) over one native core (`ParseMFTImpl`); the dump export and the volume export both take the control block and callback. Neither filters names or resolves paths. `StreamRecords(includeFreed, ...)` also returns freed base records when asked; a dump parse never does. `MftResult.Timings` is native-only (`NativeIo`, `NativeFixup`, `NativeParse`, `NativeTotal` as `TimeSpan`); `MftResult.TotalRecords` is the examined count. `StreamRecords` provides memory-efficient `IEnumerable<MftRecord>`; `MaterializeBatches` provides bounded-memory batch materialization over the same result; the internal `MftVolume.ReadRecordBatches` is the batch path the broker uses.
    - **ElevationUtilities**: Names the public route to elevation: `DefaultProvider`, the production `IElevationProvider`. Callers supply a timeout appropriate to their operation; the shared Defender exclusion sequence allows 60 seconds for its elevated child. `IsElevated()`, `CanSelfElevate()` and `TryRunElevated(IReadOnlyList<string> arguments, TimeSpan timeout)` are interface members; `TryRunElevated` relaunches the executable with the `runas` verb and quotes each argument through `ProcessStartInfo.ArgumentList`, which .NET honours with `UseShellExecute = true`. `TryAddDefenderProcessExclusions` is the one Windows Defender process-exclusion sequence built on those members ([Defender exclusions](elevation.md#windows-defender-process-exclusions)). The internal `DefaultElevationProvider` holds the logic and takes its process, platform and wait dependencies through an internal constructor, so tests build their own instance with fakes.

- **SampleProgram.Direct** and **SampleProgram.Watch** (C# Console Apps) - the two samples, each a CLI that compiles against the public API with no access to MFTLib internals; together their IL is the caller set the public surface is measured against. The elevation flow, the heads-up dialog, the unattended check and the option reader live once in `SampleProgram.Shared` and compile into both as linked source. Each verb is small, so a public member that needs contortion to appear is an internalization candidate, not a reason to grow a sample.
  - **Direct** opens a `CacheMode.Disabled` `FileIndex` in this process, with no broker and no watch. `--source local` (the default) is `MftIndexSources.FromLocalVolumes` and self-elevates; `--source dump --dump-file PATH` is `MftIndexSources.FromMftDumpFile`, needs no elevation and runs on every platform. Verbs: `search` (name, exact, case, under, directories or files, size and date bounds, `--stream`, `--limit`, `--include-freed`), `tree`, `open`, `largest`, `duplicate-names` and `scan`, plus `exclude-defender` (no drive; it calls `ElevationUtilities.TryAddDefenderProcessExclusions` for the sample's own executable). `--include-freed` sets `MftScanOptions.IncludeFreed` and `SearchQuery.IncludeDeleted` together, prints `FileEntry.IsDeleted` per row, and is refused on a dump, which never yields freed rows. The `open` verb prints a known last-write timestamp in UTC, or `<unknown>` when `FileEntry.IsLastWriteTimeKnown` is false, before attempting to open the entry.
  - **Watch** opens a `FileIndex` over `BrokerSession.CreateIndexSource()`, the path consumers ship, and also dispatches `--broker` through `ElevatedEntryPoint`. Verbs: `watch` (the default; state changes, faults and each `FileChangeKind`), `scan-drive` (one pass: a `CacheMode.Disabled` open that prints the `DriveStatus` rows, skipped records and checkpoint loss), `rescan`, `journal` (settings, or `BrokerSession.GrowUsnJournalAsync` with a target settings struct), `cache` (`CacheDirectory.InspectCached`, and `--clear` through `DeleteCached`) and `elevation-status`. The other verbs open the cached index, so they show `DriveBlockStatus.Source`, `CacheSlot` and checkpoint loss, including its growth-target refusal reason. No retention flag means full scanning; `--directories-only` keeps directories alone, and `--keep-name A,B` keeps directories plus those names. These two flags are mutually exclusive and an empty keep-name value is refused. `RetainedFileNames` supplies the nullable retention policy, with a full/directory marker and sorted names in the policy directory fingerprint; the sample tag is `SMPW` version 2. One session shared by the drives launches its broker on the first needed scan, so one UAC prompt serves the run.
  - **Direct row algorithms**: `largest` and `duplicate-names` are implemented in the sample over
    `EnumerateRows`. `LargestFiles` keeps a bounded heap of live, known-size files and prints paths and sizes
    followed by an entry count. `DuplicateNameSieve` and `SieveBitmap` make several hash passes, then group
    real names without regard to case. The query selects live files; the sample excludes empty names.
    Members keep enumeration order; the verb prints each name, member count and first member's path.
    Only the bitmaps are bounded (about 80 MiB at maximum bucket count); final candidate and duplicate-group
    storage depends on surviving rows. Each pass takes its own snapshot and may see concurrent changes.
    The `scan` verb adds a per-drive inventory in one `EnumerateRows` pass with `IncludeDeleted: true`:
    live files, live directories (including the root), and retained deleted rows. Deleted files and
     directories count only as deleted; free slots are excluded. `Drive` selects the tally,
    `IsDeleted` separates retained history, and `IsDirectory` classifies the live inventory. A second per-drive
    line reads `IsSizeKnown`, `IsLastWriteTimeKnown`, `Attributes` and `LastWriteTime` from the same rows to report
    unknown-size, unknown-last-write, hidden and newest-known-last-write figures for the live files.
  - **Elevation:** a verb that launches the broker stays unelevated and the broker asks for elevation; a Direct local scan self-elevates through `ElevationUtilities.DefaultProvider`, waiting indefinitely for the elevated child process. Just before either prompt an attended run shows a blocking, system-modal `MessageBoxW` heads-up (with a beep) naming the executable and the exact arguments, so the UAC prompt that follows is expected: OK continues, Cancel prints the manual-elevation fallback and exits 1, a dismissal within 0.75 seconds is treated as an accidental key press and the dialog is shown again, and a dialog nobody answers within five minutes counts as Cancel. With `MFTLIB_SAMPLE_UNATTENDED=1` the samples show no dialog and request no elevation at all (see [Elevation: attended and unattended runs](elevation.md)). The dialog lives in the samples, never in `ElevationUtilities`, so a consumer's runtime flow gains none; it is skipped when already elevated and in an unattended run, and a Direct run that cannot self-elevate (including a session with no interactive desktop) shows none and prints the manual-elevation fallback; a Watch broker launch does not consult self-elevation and shows it in every attended unelevated run. Verbs call the library through `internal Func` seams on `SampleHost` that `MFTLib.Tests` replaces.
- **Benchmark** (C# Console App) - Measures synthetic MFT parsing and warm packed-index queries. Run `Benchmark.exe index --synthetic N` to write N rows (including the root) through the internal block writer, or `Benchmark.exe index --cache-directory <path>` to open existing blocks cache-only, without scanning or starting a watch. Each drive reports row count, slot capacity, file bytes, used name-pool bytes and their percentage of the file, and reserved name-pool capacity. Exact and substring `Search` query `file-0000000001`, warm up once, then report the median of K runs (`--iterations K`, default 3), with match counts. Synthetic file names are `file-0000000001` and onward, so this query compares same-length names in a full scan and matches one file when N is greater than one. Cache inspection supplies each block's root and consumer tag; unavailable or offline blocks are reported as errors. Synthetic blocks use production capacity headroom and are deleted after measurement.
- **MFTLib.Tests** (C# MSTest) - Unit tests for record mapping and path resolution.
- **MFTLibTestExtensions** (C# Library) - Public, consumer-facing `BrokerTestHarness` that runs a
  real broker host in process over in-memory control and drive pipes and returns an `InProcessBrokerHandle`
  connected to it, with `Crash()` to simulate broker death and `Scans`
  to read the scans it served. A test scripts the host through `ScriptedBrokerVolumes` (journal cursor,
  scan, catch-up, watch and journal grow sources) and `ScriptedScan`; the harness supplies fixed
  volume sizing (256 KiB of 1024-byte records). The volume-query override, client clock,
  per-pipe connection failures and held host writes stay internal seams for `MFTLib.Tests`. Host faults surface only through production behavior:
  the `InProcessBrokerHandle.Ended` task, an `IOException` on pending operations, and
  `Error` frames; disposing the process never throws a host fault. `SyntheticBlock`,
  `SyntheticBlockEditor` and `SyntheticMftProducer` seed, edit and read cache blocks and produce MFT
  blocks through the production block writer, so consumer tests never call the block writer. Ships as the separate
  `MFTLib.TestExtensions` NuGet package at publish time; never folded into the `MFTLib` package.
  `ScriptedWatchSource` and `ScriptedDriveWatch` script the live watch an index runs beside its scan,
  without naming the internal `IIndexWatchSource` or `IIndexDriveWatch` (an internal adapter presents them to the index),

  `SyntheticIndexSource.Create` joins a producer and a watch source into the `MftIndexSource` an index takes,
  and `SyntheticJournalRecord` and `SyntheticScanRecord` supply journal and scan records.
  Test-package shapes: a double's configuration properties are init-only, except one a test changes while the
  double runs (`ScriptedWatchSource.StartFailure`, documented `set`); batches of `SyntheticJournalRecord` are
  `IReadOnlyList` in every public signature; and no public type lists a non-public base type or interface
  (`TestPackageSurfaceTests`, with a negative control).

### Native error messages

C++ exceptions do not cross the native C ABI: exports translate them into their existing error-message, null-pointer or boolean failure channels, and exception failures return no entries. Worker exceptions are carried to the joining thread, all started workers are joined on failure, and out-of-memory handling does not allocate.

Native exports write failure reasons into fixed-size `MftMessageChar errorMessage[256]` buffers on their result structs (`MftParseResult`, `MftDumpInputInfo`, `UsnJournalResult`). Each unit is UTF-16: `wchar_t` on Windows and `char16_t` elsewhere. Use the `SetErrorMessage` helper in `MFTLibNative/internal.h` - a variadic template that deduces buffer size, truncates via `_snwprintf_s(..., _TRUNCATE, ...)` on Windows or `std::swprintf` with explicit NUL termination on POSIX, uses fixed stack storage to narrow the ASCII message into UTF-16 units on POSIX, and asserts in debug builds if a message doesn't fit. Avoid calling `swprintf_s` / `snprintf_s` directly at error-write sites; the helper keeps `cert-err33-c` silent and centralizes the truncation semantic.

### Native test hooks ship in the release DLL

`MFTLibNative/core/test_hooks.cpp` exports failure-injection and observation hooks
(`SetAllocFailCountdown`, `SetReadFailCountdown`, `SetUsnIoFailError`, `SetUsnWatchPipe`,
`GetChunkThreadCounts`, `ResetTestState` and the rest). They are compiled into the Release
`MFTLibNative.dll` that the NuGet package ships, deliberately. The exports are test-only and
unsupported: they are not part of the managed public API, and they may change or disappear in
any release. Their managed `DllImport` declarations live in `MFTLib.Tests`
(`TestSupport/NativeTestHooks.cs`), not in `MFTLib`; the only native test-support exports
MFTLib itself declares are `GenerateSyntheticMFTSizedUtf8`, reached from
`MftVolume.GenerateSyntheticMFT`, and `GenerateFixtureMFTUtf8`, reached from
`MftVolume.GenerateFixtureMFT`, both taking a UTF-8 path on every platform.

Why they stay:

- The shipped DLL must be the tested DLL. Coverage and CI exercise the exact binary in the
  package, failure paths included. Compiling the hooks out for the package would mean tests run
  against a different binary than consumers get, and would add a second native build
  configuration to the vcxproj, CMake, the coverage scripts and CI.
- The checks cannot live anywhere else. They sit in front of allocations and reads inside the
  parser and journal code, so a managed package such as `MFTLibTestExtensions` cannot provide
  them. `MFTLibTestExtensions` does not use them.
- They are not a privilege boundary. Only code already loaded in the same process can call the
  exports, and such code can already do worse.
- The cost was measured and is not significant. On 2026-10-02, at commit `d8439ab`, the Release
  DLL was built twice with the same settings: as shipped, and with every parse-path hook
  compiled to a constant (allocation, read and platform I/O failure, forced cancel, the
  capacity and record-size overrides, and the per-chunk thread-count recording with its mutex).
  `Benchmark.exe 1000000 5` ran eight times against each DLL, alternating. Median throughput
  in records per second, with hooks and without: compat 3,243,295 and 3,312,850; bounded
  3,374,366 and 3,451,267; broker-stream 2,408,314 and 2,410,359. The same DLL varied by 10 to
  25 percent between runs on a machine that was also running builds, so the gap of about 2
  percent is inside the noise; the best runs were equal (bounded 3,583,085 and 3,584,190).
  The benchmark is bound by I/O and memory throughput, not by parser overhead. The hook-free
  DLL was 2,048 bytes smaller (75,776 against 77,824).

Revisit this only with a measurement that shows a cost: a quiet-machine benchmark where the
hook-free build wins by more than the run-to-run spread. Do not propose removing the hooks
from the shipping DLL on the grounds that test code is present in a release binary.

### Public surface

A type or member is public only when a consumer needs it in production code. Test-only
access goes through `MFTLibTestExtensions`, which forwards to internal code and never
re-implements it, and `InternalsVisibleTo` on the MFTLib assembly names only `MFTLib.Tests`,
`MFTLibTestExtensions` and `Benchmark`, never a consumer assembly.

`MFTLib.Tests/PublicSurfaceTests.cs` pins the surface with a reflection enumerator,
`MFTLib.Tests/PublicSurfaceEnumerator.cs`. It walks every public top-level type, whatever its
namespace, and every public or protected nested type at any depth, and lists every public or
protected constructor, method, property, field and event each type declares. Members the
compiler synthesizes are included because consumers can call them: record equality members and
operators, `Deconstruct`, the clone method, and on unsealed records the copy constructor,
`EqualityContract` and `PrintMembers`.

Each approved line names the declaring type, then the member kind, modifiers, name, parameters
and type, for example
`MFTLib.Index.FileIndex :: method public DisposeAsync() : System.Threading.Tasks.ValueTask`.
Types are written with every nesting level's own generic arguments (`Outer<T>.Nested<U>`,
never `Outer<T, U>`) and with the nullable annotations the compiler's `NullableAttribute` and
`NullableContextAttribute` metadata carries, including on unconstrained generic parameters,
plus the nullable flow attributes such as `MaybeNullWhen`. The enumerator decodes that metadata
itself because `NullabilityInfoContext` reports an unconstrained `T` and `T?` alike. Lines are
sorted ordinally. The approved files in `MFTLib.Tests/PublicSurface/` are `MFTLib.approved.txt` (every
MFTLib type outside `MFTLib.Index`), `MFTLib.Index.approved.txt` and
`MFTLibTestExtensions.approved.txt`. The test fails on any added or removed line and prints
both sets. A change to the public surface is therefore a diff to an approved file in the same
pull request. A pull request that grows an approved file names, in its body, the consumer
production caller of every added member. The negative control in `PublicSurfaceTests` runs the
enumerator over fixtures and pins each of these properties.

Detection contract: the gate pins the set of public and protected types and members of MFTLib,
MFTLib.Index and MFTLib.TestExtensions, each with its signature: the declaring type with every
nesting level's own generic arguments, the name, the parameter and return types, and the
nullable annotations the compiler records on them. Adding or removing a type or member, or
changing a signature, fails the test. Modifier and annotation rendering covers the constructs
these assemblies use today. A construct the formatter does not render still appears as a line
when it is introduced, so its arrival is reviewable, and rendering for it is added when MFTLib
first uses it. Not rendered today: type-parameter variance, function pointers, volatile fields,
scoped parameters, control characters in literals, and nullability on base types, interface
implementations, generic constraints and accessor flow attributes. That is a stated limit of the
method, not a defect.

Caller gate: every authored public or protected member and type of `MFTLib` and `MFTLib.Index`
needs a real caller, and `MFTLib.Tests/PublicMemberCallerTests.cs` proves it. The caller is a use in
`SampleProgram.Direct` or `SampleProgram.Watch`, or, for the narrow exceptions below, a consumer
production caller. `MFTLib.Tests/MetadataSurfaceReader.cs` and `MetadataSurfaceReader.IL.cs` read
the compiled assemblies with `System.Reflection.Metadata`, never loading them. One side is the
library's surface; the other is the set of member, generic-method and type references in the IL of
the two samples. Both sides key a member by declaring type, name and a signature decoded by one
provider, so overloads stay apart. An enum is referenced by its type, and its literals ride with
it. A property or event is referenced when any accessor is. A member is excluded as synthesized only on
evidence in the compiled metadata: a member carrying `System.Runtime.CompilerServices.CompilerGeneratedAttribute`
(matched by full name; an unrelated attribute of the same short name hides nothing) is not surface (record
equality, the clone method, `PrintMembers`, the synthesized `Deconstruct` and copy constructor, and
compiler-generated members generally), nor is a record's `EqualityContract` whose property itself carries the
mark. The primary constructor carries no mark, so it is excluded when its parameter names and types equal the
marked `Deconstruct` exactly; a zero-parameter positional record, a record whose author wrote the
primary-signature `Deconstruct`, and a primary constructor with an `in` parameter leave no such evidence and keep
the constructor on the surface, which fails loudly. Everything else a record declares is surface, every property
included. A type is a record when it carries the `op_Equality` the compiler marks; an authored `PrintMembers` or
`op_Equality` on a class does not make it one. Metadata cannot tell a compiler mark from the same attribute applied
by hand to an authored member or operator, so that case is outside what the gate detects.
A record's automatic property (every exposed accessor marked) is called either by a sample reference to one of
its accessors or by a sample reference to a public constructor of the record whose own IL stores one of its
arguments (`ldarg` 1 or higher, immediately followed by `stfld`) into the field the property's getter reads,
which is how a positional property and an authored `{ get; init; } = First;` are initialized. The credit follows
the field store, never names. The constructor must be straight-line: no branch of any kind, no `starg`, and
exactly one `stfld` of that field, so an overwritten, mutated or conditional store, and a constructor with a
guard branch, credit nothing and the property then needs an accessor reference. A store through a setter, of a
constant or computed value, into another field, from an `in` parameter, or in a constructor that is not public
also credits nothing. A constructor that delegates with `this(...)` and also stores an argument itself is
credited on the same terms; pure delegation credits nothing. Constructor credit is evidence of a direct store,
not a proof of data flow. A `constrained. T` prefix followed by `callvirt` of `ToString`, `Equals(object)` or
`GetHashCode` declared on `System.Object` dispatches to the override `T` declares, so it counts as a reference to
a method of `T` that is virtual and reuses a slot that starts at `System.Object`, never a method that hides it with
`new`, and never an override of such a hiding method in a descendant (an ancestor that introduces the same method on a
new slot, a base class from another assembly other than `ValueType` and `Enum`, and a generic base are refused): `T` is a
library type or a closed instantiation of a library generic type (credited to its definition). A type-parameter
constraint, an interface callee, a prefix separated from its call by another instruction, a boxed or
`Nullable<T>` receiver and string interpolation credit nothing, and `Equals(T)` is a different member. The gate
reads the library project's own build output, because coverage instrumentation rewrites the copy
beside the tests, and fails on every platform when that output is missing. It also fails when two public members share a key, because a function pointer parameter renders as `fnptr`. A
public type outside those two namespaces fails the
gate by name, so a new namespace cannot escape it. The failure message lists each unreferenced
member by type, name, parameters and result.

A new public member is admitted by a sample use, which belongs in `SampleProgram.Shared` when the
verb files are full, and never by a contrived call. A member with no sample use and no consumer
production caller is internalized. Two lists in the test are the only exceptions, one entry per
member, each with a reason. The exempt list holds members with no sample use that a consumer
production caller needs; each reason names the consumer repository and file, never a line number
(which rots), and each key is the member's full display text, with generic arity, parameters and
result, so an entry cannot cover a sibling overload. The one exception is a `ToString` reached only by
string interpolation, which emits no member reference and so is invisible to every check; its entry also
cites the line that formats the value. An exempt reason is a statement by a person; no check verifies it against
consumer IL. The constant list covers the `const` fields the compiler
inlines, which leave no IL reference; a second test requires it to equal the literal fields the
library metadata declares, in both directions. An entry that names no member, or whose member
gained a sample use, fails the gate.

The full gate runs on Windows, where `SampleProgram.Watch.dll` is built, and a missing assembly
there fails the test. Elsewhere it reports Inconclusive. Controls run on every platform and assert
member identity, never counts: the real sample references with one known member removed must
report exactly that member; `MFTLib.Tests/CallerGateFixtureCaller.cs`, whose compiled but uninvoked method
references the launcher-and-timeout `BrokerSession` constructor, must be found by
the type-scoped collector with the parameterless constructor absent;
fixture types prove that records keep their authored `Deconstruct`, `ToString`, constructor overloads and
copy constructor while the synthesized members are dropped, that the evidence-free shapes behave as described
above, and that overloads differing only by arity or indexer parameter get distinct keys, that the namespace check fires, and that duplicate keys are
reported. `CallerGateReaderTests`
proves the credit rules through real IL: a generated assembly calls the fixture members, and each constructor
store, accessor read and constrained call is credited or refused as described, one case per predicate.

To update an approved file, set `MFTLIB_PUBLIC_SURFACE_REGENERATE_TO` to a scratch directory
and run `PublicSurfaceTests`: the tests write the current surface there and fail on purpose.
Review the diff and copy the files into `MFTLib.Tests/PublicSurface/` by hand. A normal run
never writes an approved file.

## Source layout

The managed source is organized by responsibility:

- `MFTLib/Index` - the substrate-neutral packed index: block format, `FileIndex`, snapshots, queries, mutation, and the enumeration producer. It depends on nothing else in the library beyond a few journal value types, a boundary an architecture test enforces.
- `MFTLib/Mft` - scans, records, results and timings
- `MFTLib/Journal` - USN cursor, entries and reasons
- `MFTLib/Sources` - producers, row writer and scan
- `MFTLib/Broker` - elevated host and client, protocol, block writing, and diagnostics
- `MFTLib/Elevation` - elevation detection and the injectable provider
- `MFTLib/Interop` - native result layouts
- `MFTLib/Internal` - native bindings and internal volume utilities
