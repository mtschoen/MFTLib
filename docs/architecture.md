# Architecture contracts

- **MFTLibNative** (C++ DLL) - Core NTFS MFT parsing logic with multi-threaded parallel fixup+parse and double-buffered I/O. Fully thread-safe and re-entrant. MFT record geometry (1024 or 4096-byte records) is detected at runtime rather than assumed - `FSCTL_GET_NTFS_VOLUME_DATA` for a live volume, record 0's header for an exported file. Results cross the P/Invoke boundary through compact ABI version 7 (`MFT_NATIVE_ABI_VERSION`): 52-byte `MftCompactEntry` rows with int64 size at offset 32, int64 modified time (FILETIME) at offset 40, uint16 sequence number at offset 48, and uint16 parent sequence number (the sequence the record's name stores in its parent reference) at offset 50, plus separate UTF-16 string pools. Flags bit `0x8000` marks an unknown size. The parser emits each record's own name and parent reference and resolves no paths; `mft_api.h` pins the row layout with static asserts.
- **MFTLib** (C# Library) - Managed wrapper with P/Invoke interop. The `MFTLib.Index` namespace provides a substrate-neutral columnar block format and query engine; see `docs/index-format.md`.
    - **Index namespace boundary**: `MFTLib.Index` depends on nothing in the flat `MFTLib` namespace or in `MFTLib.Interop` beyond an allowlist of journal value types (`UsnJournalEntry`, `UsnJournalSettings`, `UsnReason`). Enforced by `MFTLib.Tests/Index/NamespaceBoundaryTests.cs`, an IL-level ArchUnitNET test over the built assembly, with a mandatory negative-control fixture. Not an aislop rule: the forbidden folders share the flat `MFTLib` namespace, so there is no `using` for an import rule to match. Growing the allowlist is a review decision.
    - **MFT dump drives**: a source built with a dump identity (`MftDumpSourceIdentity`) makes its drive a virtual
      inventory with the root `dump:/{DRIVE}` (upper-case letter), rendered with `/` and read with `/` or `\`,
      case-insensitive on every platform; `MftDumpPaths` owns those rules and touches no filesystem. The block
      carries `DriveBlock.IsMftDump`, and the index never probes the root, opens the entry (`FileEntry.Open` throws
      `InvalidOperationException`), answers journal settings (`QueryUsnJournalSettings` throws) or watches it:
      `DriveStatus.WatchSupported` is false, start and per-drive catch-up throw `Drive X: this source does not support
      watching.`, the same text a source with no watch source gives, and batched start, stop and catch-up report
      `NotApplicable` with no failure. Opening validates the options before any cache path is resolved or created: exactly one drive with the identity's
      letter, root `dump:/{DRIVE}`, `VolumeSerial` zero, `NoCache`, no cache directory, tag or cache-only open, and
      `ProducerPolicy.Mft`; a dump block is never written to or adopted from a cache. `DriveStatus.WatchSupported` is false for
      a dump drive and for any drive whose source has no watch source, the same fact a watch start's refusal uses.
    - **MFT dump source**: `MftIndexSources.FromMftDumpFile(filePath, driveLetter)` is the only way to build a dump
      source. It checks its arguments (`A dump file path is required.`, `The logical drive key must be an ASCII
      letter.`), makes the path absolute once and never opens the file; every scan and rescan opens the path anew, so
      a missing or rejected dump is a `ProducerFailed` drive, or a failed rescan that keeps the last block, with the
      reason in `MftProducerFailureMessage`. `MftDumpBlockProducer` is a direct producer with no broker, pipe or
      session: one `MftDumpInput` per request sizes the block (`NtfsVolumeInformation(fileLength, recordSize)`, never
      the live volume sharing the letter) and supplies every record, the shared `MftBlockScan.WriteToBlock` writes a
      plain `BlockFile` with the full profile, and the block carries a zero journal cursor. Record size is 1024 or
      4096 only. `MftDumpRecordValidation` runs before the block is completed: the root row 5 must be an allocated
      directory whose parent is 5 (`The dump has no valid allocated root record.`), no base record number repeats
      (`The dump contains duplicate base record numbers.`), and every parent a record names fits the planned block
      (`The dump contains required record numbers outside the index range.`). A record whose own number does not fit
      is skipped and counted by the row writer; a record the parser omits for a malformed attribute is not counted.
      Import is allocated records only. Content rejections are `InvalidDataException`, an open failure is `IOException`
      carrying the platform error code, cancellation stays `OperationCanceledException`.
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
      (an `MftOmittedRecords` batch), which adds it to `DriveStatus.SkippedRecordCount`, so no record disappears
      without a trace. A live volume's record zero is the exception: its decoded `$DATA` runs locate every other
      record, so an invalid fixup there fails the scan with `MFT record 0 has an invalid fixup` before any
      attribute is read. These rejections set `MftParseResult.invalidInput`, which
      `MftResult` throws as `InvalidDataException`; no caller classifies a failure by its text. Message buffers are
      UTF-16 (`MftMessageChar`) on every platform, so the managed struct layouts match the native ones on Linux.
    - **Consumer cache identity**: `FileIndexOptions.CacheTag` carries an opaque
      four-ASCII-character code plus a `uint` version; default is all zeros and
      compares exactly, not as a wildcard. Block format 3 stores the two values
      at offsets 104 and 108 in a 112-byte declared header. Old-format blocks
      cold-scan once. Consumers bump their own version when their profile or
      keep-list changes. Mismatches report `WrongCacheTag` and cold-scan; a
      cache-only open fails with `DriveFailureKind.CacheTagMismatch`. Both emit
      stored/requested tag diagnostics. `InspectCached` reports tags on available
      blocks. Tags are initialized before completion; the internal MFT producers copy
      `request.CacheTag` into creation options, and the built-in broker adapter
      forwards it automatically. `CacheTag` has no public component accessors: consumers
      construct and compare whole tags, and `MFTLibTestExtensions.SyntheticCacheTag` reads the
      components for policy tests. See `docs/index-format.md` for the contract.

    - **ABI versioning**: `MFTLibNative.EnsureCompatibleNativeAbi()` / `MftResult`'s constructor check the native ABI version and entry stride before parsing, and throw `InvalidOperationException` immediately on a managed/native mismatch instead of decoding mismatched memory. `MftDumpInput.Open` makes the same check before it opens a file.

    - **MFT source**: `FileIndexOptions.MftSource` is one `MftIndexSource` carrying the block
      producer and the watch source of every MFT-backed drive, so the two cannot come from
      different connections. `BrokerSession.CreateIndexSource()` builds the broker-backed
      one; `MftIndexSource.Unavailable(reason)` fails every scan and watch start with
      `Drive {letter}: {reason}.`. `MftIndexSources.FromLocalVolumes(scanOptions)` builds the in-process one for a caller
      that is already elevated: `LocalMftBlockProducer` plans capacity from the volume geometry,
      creates the requested block and fills it through `MftBlockScan.WriteToBlock` with a zero
      journal cursor, with no broker process or pipe. It has no watch source, so watch start and
      per-drive catch-up refuse with `Drive {letter}: this source does not support watching.`,
      batched start and catch-up report `NotApplicable`, and `DriveStatus.WatchSupported` is false. Concurrent
      direct scans share the process's parse threads through `ParseThreadAllocator`. Because the source
      cannot watch, cache reopening skips resume-checkpoint validation against the live journal, preserving
      cached blocks without spurious journal-recreation loss. The factory
      opens nothing until the first scan; a process that cannot open the volume fails the drive
      as `ProducerFailed`. A null `MftSource` is a configuration error for
      `ProducerPolicy.Mft` and is ignored by `ProducerPolicy.Enumeration`. The block producer
      delegate, the watch source and drive watch interfaces and their records are internal;
      only MFTLib implements them, and tests build sources through `MFTLibTestExtensions`.
    - **Watch state events**: `FileIndex.WatchStateChanged` reports every change of a drive's
      derived `WatchCatchUpState` with a per-drive `WatchStateVersion`, noted by one helper inside the
      state-lock section that made the change and delivered with neither `_stateLock` nor a write
      gate held, one drive at a time in version order, before the `WatchFaulted` of the fault
      that caused it. Handlers follow the `Changed`/`WatchFaulted` reentrancy rules. The full
      contract is in `docs/watch-lifetime.md`.
    - **Lazy Materialization**: `MftRecord` stores native pointers; strings are only created on access.
    - **Memory Safety**: `ToArray()` and `Materialize()` ensure strings are stable in managed memory after native buffers are freed.
    - **Streaming API**: `MftVolume.StreamRecords` (a volume) and `MftDumpInput.Parse` (a saved image) are the only scan entry points. Both support the same progress, `ParseThreadAllowance` and cancellation controls (supplied directly on `StreamRecords` or grouped into `MftFileScanOptions` for `MftDumpInput.Parse`) and share one managed path (`ParseWithControl` allocates the native `MftParseControl` block, attaches the allowance, registers the token and frees it) over one native core (`ParseMFTImpl`); the dump export and the volume export both take the control block and callback. Neither filters names or resolves paths. `StreamRecords(includeFreed, ...)` also returns freed base records when asked; a dump parse never does. `MftResult.Timings` is native-only (`NativeIo`, `NativeFixup`, `NativeParse`, `NativeTotal` as `TimeSpan`); `MftResult.TotalRecords` is the examined count. `StreamRecords` provides memory-efficient `IEnumerable<MftRecord>`; `MaterializeBatches` provides bounded-memory batch materialization over the same result; the internal `MftVolume.ReadRecordBatches` is the batch path the broker uses.
    - **ElevationUtilities**: Shared logic for detecting and ensuring Administrative privileges. `TryRunElevated(IReadOnlyList<string> arguments, TimeSpan timeout)` relaunches the executable with the `runas` verb and quotes each argument through `ProcessStartInfo.ArgumentList`, which .NET honours with `UseShellExecute = true`; `DefaultElevatedTimeout` is 60 seconds and the `_waitForExit` seam takes a `TimeSpan`.

- **TestProgram** (C# Console App) - CLI that compiles against the public API with no access to MFTLib internals. Its one mode today is `scan-drive` (the default), which opens a `NoCache` `FileIndex` per drive over `BrokerSession.CreateIndexSource()`, the path consumers ship, and prints the `DriveStatus` rows, skipped records and checkpoint loss. One session shared by the drives launches its broker on the first needed scan, so one UAC prompt serves the run, and TestProgram also dispatches `--broker` through `ElevatedEntryPoint`. `scan-drive` stays unelevated; the broker it launches asks for elevation. Just before that launch an attended run shows a blocking, system-modal `MessageBoxW` heads-up (with a beep) naming the executable and the exact arguments, so the UAC prompt that follows is expected: OK continues, Cancel prints the manual-elevation fallback and exits 1, a dismissal within 0.75 seconds is treated as an accidental key press and the dialog is shown again, and a dialog nobody answers within five minutes counts as Cancel. A mode that required elevation itself would self-elevate through `ElevationUtilities.DefaultProvider` with the same dialog. With `MFTLIB_TESTPROGRAM_UNATTENDED=1` TestProgram shows no dialog and requests no elevation at all (see [Elevation: attended and unattended runs](elevation.md)). The dialog lives in TestProgram, never in `ElevationUtilities`, so a consumer's runtime flow gains none; it is skipped when already elevated or when self-elevation is unavailable (including a session with no interactive desktop). The scan calls the library through `internal Func` seams on `DriveScanner` that `MFTLib.Tests` replaces.
- **Benchmark** (C# Console App) - Measures synthetic MFT parsing and warm packed-index queries. Run `Benchmark.exe index --synthetic N` to write N rows (including the root) through the internal block writer, or `Benchmark.exe index --cache-directory <path>` to open existing blocks cache-only, without scanning or starting a watch. Each drive reports row count, slot capacity, file bytes, used name-pool bytes and their percentage of the file, and reserved name-pool capacity. Both `FindByName` (exact) and `Search` (substring) query `file-0000000001`, warm up once, then report the median of K runs (`--iterations K`, default 3), with match counts. Synthetic file names are `file-0000000001` and onward, so this query compares same-length names in a full scan and matches one file when N is greater than one. Cache inspection supplies each block's root and consumer tag; unavailable or offline blocks are reported as errors. Synthetic blocks use production capacity headroom and are deleted after measurement.
- **MFTLib.Tests** (C# MSTest) - Unit tests for record mapping and path resolution.
- **MFTLibTestExtensions** (C# Library) - Public, consumer-facing `BrokerTestHarness` that runs a
  `JournalBrokerHost` in process over in-memory control and drive pipes and returns an `InProcessBrokerHandle`
  holding the production `BrokerProcess` connected to it, with `Crash()` to simulate broker death and `Scans`
  to read the scans it served. A test scripts the host through `ScriptedBrokerVolumes` (journal cursor,
  scan, catch-up, watch, volume query and journal grow sources) and `ScriptedScan`; the client clock,
  per-pipe connection failures and held host writes stay internal seams for `MFTLib.Tests`. Host faults surface only through production behavior:
  the `BrokerProcess.Ended` task, `BrokerChannelLostException` on pending operations, and
  `Error` frames; disposing the process never throws a host fault. `SyntheticBlock`,
  `SyntheticBlockEditor` and `SyntheticMftProducer` seed, edit and read cache blocks and produce MFT
  blocks through the production block writer, so consumer tests never call the block writer. Ships as the separate
  `MFTLib.TestExtensions` NuGet package at publish time; never folded into the `MFTLib` package.
  `ScriptedWatchSource` and `ScriptedDriveWatch` script the live watch an index runs beside its scan,
  `SyntheticIndexSource.Create` joins a producer and a watch source into the `MftIndexSource` an index takes,
  and `SyntheticJournalEntry` and `SyntheticMftRecord` mint journal entries and materialized records.

### Native error messages

Native exports write failure reasons into fixed-size `wchar_t errorMessage[256]` buffers on their result structs (`MftParseResult`, `UsnJournalInfo`, `UsnJournalResult`). Use the `SetErrorMessage` helper in `MFTLibNative/internal.h` - a variadic template that deduces buffer size, truncates via `_snwprintf_s(..., _TRUNCATE, ...)` on Windows or `std::swprintf` with explicit NUL termination on POSIX, and asserts in debug builds if a message doesn't fit. Avoid calling `swprintf_s` / `snprintf_s` directly at error-write sites; the helper keeps `cert-err33-c` silent and centralizes the truncation semantic.

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

To update an approved file, set `MFTLIB_PUBLIC_SURFACE_REGENERATE_TO` to a scratch directory
and run `PublicSurfaceTests`: the tests write the current surface there and fail on purpose.
Review the diff and copy the files into `MFTLib.Tests/PublicSurface/` by hand. A normal run
never writes an approved file.
