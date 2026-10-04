# Architecture contracts

- **MFTLibNative** (C++ DLL) - Core NTFS MFT parsing logic with multi-threaded parallel fixup+parse and double-buffered I/O. Fully thread-safe and re-entrant. MFT record geometry (1024 or 4096-byte records) is detected at runtime rather than assumed - `FSCTL_GET_NTFS_VOLUME_DATA` for a live volume, record 0's header for an exported file. Results cross the P/Invoke boundary through compact ABI version 3 (`MFT_NATIVE_ABI_VERSION`): 50-byte `MftCompactEntry` rows with int64 size at offset 32, int64 modified time (FILETIME) at offset 40, and uint16 sequence number at offset 48, plus separate UTF-16 string pools. Flags bit `0x8000` marks an unknown size; `0x4000` marks an unresolved path whose path-table string is a bare name (path table only). The broker's block path parses without path resolution.
- **MFTLib** (C# Library) - Managed wrapper with P/Invoke interop. The `MFTLib.Index` namespace provides a substrate-neutral columnar block format and query engine; see `docs/index-format.md`.
    - **Index namespace boundary**: `MFTLib.Index` depends on nothing in the flat `MFTLib` namespace or in `MFTLib.Interop` beyond an allowlist of journal value types (`UsnJournalEntry`, `UsnJournalEntryOptions`, `UsnReason`). Enforced by `MFTLib.Tests/Index/NamespaceBoundaryTests.cs`, an IL-level ArchUnitNET test over the built assembly, with a mandatory negative-control fixture. Not an aislop rule: the forbidden folders share the flat `MFTLib` namespace, so there is no `using` for an import rule to match. Growing the allowlist is a review decision.
    - **Consumer cache identity**: `FileIndexOptions.CacheTag` carries an opaque
      four-ASCII-character code plus a `uint` version; default is all zeros and
      compares exactly, not as a wildcard. Block format 3 stores the two values
      at offsets 104 and 108 in a 112-byte declared header. Old-format blocks
      cold-scan once. Consumers bump their own version when their profile or
      keep-list changes. Mismatches report `WrongCacheTag` and cold-scan; a
      cache-only open fails with `DriveFailureKind.CacheTagMismatch`. Both emit
      stored/requested tag diagnostics. `InspectCached` reports tags on available
      blocks. Tags are initialized before completion; custom MFT producers copy
      `request.CacheTag` into creation options, and the built-in broker adapter
      forwards it automatically. See `docs/index-format.md` for the contract.

    - **ABI versioning**: `MFTLibNative.EnsureCompatibleNativeAbi()` / `MftResult`'s constructor check the native ABI version and entry stride before parsing, and throw `InvalidOperationException` immediately on a managed/native mismatch instead of decoding mismatched memory.

    - **MFT source**: `FileIndexOptions.MftSource` is one `MftIndexSource` carrying the block
      producer and the watch source of every MFT-backed drive, so the two cannot come from
      different connections. `BrokerMftBlockProducer.CreateIndexSource()` builds the broker-backed
      one; `MftIndexSource.Unavailable(reason)` fails every scan and watch start with
      `Drive {letter}: {reason}.`. A null `MftSource` is a configuration error for
      `ProducerPolicy.Mft` and is ignored by `ProducerPolicy.Enumeration`.
    - **Watch state events**: `FileIndex.WatchStateChanged` reports every change of a drive's
      derived `WatchCatchUp` with a per-drive `WatchStateVersion`, noted by one helper inside the
      state-lock section that made the change and delivered with neither `_stateLock` nor a write
      gate held, one drive at a time in version order, before the `WatchFaulted` of the fault
      that caused it. Handlers follow the `Changed`/`WatchFaulted` reentrancy rules. The full
      contract is in `docs/watch-lifetime.md`.
    - **Lazy Materialization**: `MftRecord` stores native pointers; strings are only created on access.
    - **Memory Safety**: `ToArray()` and `Materialize()` ensure strings are stable in managed memory after native buffers are freed.
    - **Streaming API**: `StreamRecords` provides memory-efficient `IEnumerable<MftRecord>`; `MaterializeBatches` provides bounded-memory batch materialization over the same result; the internal `MftVolume.ReadRecordBatches` is the batch path the broker uses.
    - **ElevationUtilities**: Shared logic for detecting and ensuring Administrative privileges.

- **TestProgram** (C# Console App) - CLI that runs the direct, non-index public API against specified drives or a saved MFT image. It is the one program outside the assembly that compiles against that API with no access to MFTLib internals, so every public member of the direct API is reached by a mode a person would plausibly run. Modes: `find-git` (the default), `find-name` (`FindByName` without timings), `read-records` (all four `ReadAllRecords` overloads through `--no-paths` and `--timings`), `stream-records` (`StreamRecords` with progress per phase, a `ParseThreadAllowance` from `--threads`, cancellation from `--timeout-seconds`, then `MftResult` counters, `MaterializeBatches`, `ToArray` and a record retained with `Materialize` past disposal), `parse-file` (`ParseMFTFromFile` and, with `--stream`, `StreamMFTFromFile`; needs no volume and no elevation), `volume-info` (`NtfsVolumeInformation.Query`), `usn-query`, `usn-read`, `usn-watch [--seconds N]`, `usn-grow` (`GrowUsnJournal`) and `scan-drive`, which runs `BrokerProcess.ScanDriveAsync` standalone with no `FileIndex`. `usn-grow` changes the volume and NTFS never shrinks a journal, so it requires one explicit drive, `--maximum-size` and `--allocation-delta`, never defaults and never runs inside another mode; it prints the settings before and after. `--name`, `--contains`, `--include-freed`, `--no-paths`, `--buffer-size`, `--batch-size`, `--threads`, `--stream` and the rest are rejected on a mode they do not apply to. Every mode except `scan-drive` and `parse-file` self-elevates (through `ElevationUtilities.DefaultProvider`); `scan-drive` stays unelevated and the broker it launches asks for elevation, so TestProgram also dispatches `--broker` through `ElevatedEntryPoint`. The manifest asks only for the invoker's own level, so launching `TestProgram.exe` shows no prompt until a mode that reads a volume relaunches itself. Each mode calls the library through an `internal Func` seam on `DriveScanner` that `MFTLib.Tests` replaces; `parse-file` has none and is tested over the fixture image. Members that only test construction needs (`UsnJournalEntry.Create`) are not reached.
- **Benchmark** (C# Console App) - Performance benchmark using synthetic MFT generation.
- **MFTLib.Tests** (C# MSTest) - Unit tests for record mapping and path resolution.
- **MFTLibTestExtensions** (C# Library) - Public, consumer-facing `BrokerTestHarness` that runs a
  `JournalBrokerHost` in process over in-memory control and drive pipes and returns an `InProcessBrokerHandle`
  holding the production `BrokerProcess` connected to it, with `Crash()` to simulate broker death. `BrokerTestHarnessOptions` supplies the client clock, per-pipe
  connection failures, and held host writes. Host faults surface only through production behavior:
  the `BrokerProcess.Ended` task, `BrokerChannelLostException` on pending operations, and
  `Error` frames; disposing the process never throws a host fault. Ships as the separate
  `MFTLib.TestExtensions` NuGet package at publish time; never folded into the `MFTLib` package.

### Native error messages

Native exports write failure reasons into fixed-size `wchar_t errorMessage[256]` buffers on their result structs (`MftParseResult`, `UsnJournalInfo`, `UsnJournalResult`). Use the `SetErrorMessage` helper in `MFTLibNative/internal.h` - a variadic template that deduces buffer size, truncates via `_snwprintf_s(..., _TRUNCATE, ...)` on Windows or `std::swprintf` with explicit NUL termination on POSIX, and asserts in debug builds if a message doesn't fit. Avoid calling `swprintf_s` / `snprintf_s` directly at error-write sites; the helper keeps `cert-err33-c` silent and centralizes the truncation semantic.

### Native test hooks ship in the release DLL

`MFTLibNative/core/test_hooks.cpp` exports failure-injection and observation hooks
(`SetAllocFailCountdown`, `SetReadFailCountdown`, `SetUsnIoFailError`, `SetUsnWatchPipe`,
`GetChunkThreadCounts`, `ResetTestState` and the rest). They are compiled into the Release
`MFTLibNative.dll` that the NuGet package ships, deliberately. The exports are test-only and
unsupported: they are not part of the managed public API, and they may change or disappear in
any release. Their managed `DllImport` declarations live in `MFTLib.Tests`
(`TestSupport/NativeTestHooks.cs`), not in `MFTLib`; the only native test-support export
MFTLib itself declares is `GenerateFixtureMFT`, reached from `MftVolume.GenerateFixtureMFT`.

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
pull request. The negative control in `PublicSurfaceTests` runs the enumerator over fixtures and
pins each of these properties.

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
