# Packed Index Live Watch Bridge Implementation Plan (plan 2b)

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Close the consumer gaps that plan 2 left open, so the index owns its own live USN watch, reports a per-drive failure instead of failing the whole open, hands each change its own path, and can open an MFT-producer entry by file id.

**Architecture:** The index gains a substrate-neutral watch seam declared in `MFTLib.Index`, exactly like the existing `MftBlockProducer` delegate, and `BrokerMftBlockProducer` supplies the broker implementation on the other side of the namespace boundary. `FileIndex.StartWatchingAsync` reads each MFT drive's cursor out of its own block header, opens one merged batch stream through that seam, and pumps every batch into `ApplyJournalEntries`. The block format gains a live row count in the header and a sequence-number region beside the row region, because `OpenFileById` will not accept a bare MFT record number. The native parser and the USN journal reader both stop discarding the NTFS sequence number so that region can be filled.

**Tech Stack:** C++17 in `MFTLibNative` (MSBuild on Windows, CMake plus Ninja on Linux), C# on `net10.0` in `MFTLib`, `System.IO.MemoryMappedFiles`, `System.Threading.Channels`, MSTest, coverlet for coverage, aislop as the quality gate.

**Spec:** `docs/superpowers/specs/2026-09-02-packed-index-design.md`, sections 5.1 through 5.4 and section 7. This plan does **not** delete the spec, contrary to the writing-plans default: plan 2's handoff records that the spec stays in place for plans 3 to 5 and is consumed by plan 5.

**Tracking issue:** https://gitea.fleet.sticktoitive.net/schoen/MFTLib/issues/132

**Inputs:** the two gap analyses written against the pre-merge tip, `gap-file-wizard.md` (11 gaps) and `gap-git-wizard.md` (7 gaps). They are leads, not facts; every claim used here was re-verified against `cb806f5` and the ones the code disproves are called out in the decisions below.

**Consumers stay pinned.** file-wizard and git-wizard consume MFTLib through a source submodule pinned at `32f40b9` and remain pinned until plans 3 and 4 land their ports. Every task in this plan is free to break them.

---

## Decisions

The spec's section 2 decisions are locked and are not reopened. These are the decisions this plan had to make.

1. **No back-compat surfaces, anywhere in this plan.** No transitional fields, no legacy overloads, no `[Obsolete]`, no compatibility parameters, no dual-read of an old block. Every shape change deletes the old shape in the same commit and breaks the pinned consumers. Plan 2 paid for violating this twice: decision 6's transitional output-format wire field carried a dual-arm host through tasks 12 to 16, and Task 17's "kept for compatibility" session overloads always threw and cost a review fix round. If a task looks like it needs a shim, the answer is that it needs a bigger delete.

2. **`OpenFileById` requires the real NTFS sequence number.** Measured on chonkers on 2026-09-09 against `C:\Windows\System32\notepad.exe` and a fresh temp file, unelevated, with a volume-root handle opened `FILE_FLAG_BACKUP_SEMANTICS`: a `FILE_ID_DESCRIPTOR` carrying the full 64-bit file reference opens successfully, and the same descriptor with the sequence bits zeroed fails with `ERROR_INVALID_PARAMETER` (87) in both cases. The file-wizard gap report's Gap 4 assumed `FileId.RecordNumber` was sufficient; it is not. The sequence number is available at both producers and is discarded by both today, so tasks 2, 4, and 5 exist purely to carry it.

3. **Sequence numbers get their own block region, not a wider row.** A row is exactly 32 bytes with every byte assigned, and `RowFlags` has 11 free bits against a 16-bit sequence number, so nothing fits in place. A fourth 4 KB-aligned region of one `ushort` per slot costs 2 bytes per row (about 42 MB on the owner's 21 million file volume) against 8 bytes for a 40-byte row.

4. **Nothing here has ever shipped, so there is no version lineage.** `BlockLayout.FormatVersion` is `2` because the on-disk C: block from plan 2's attended run carries `1` and must be discarded; the number is a discriminator, not a revision history. `MFT_NATIVE_ABI_VERSION` and its managed counterpart are set to `1` on both sides, because no build of this interface has ever been consumed outside the repository and a mismatching DLL is rejected exactly as it is today. There is no migration, no dual-read, and no compatibility surface anywhere in this plan.

5. **The watch seam is declared in `MFTLib.Index` and implemented by the broker, over one merged multi-drive stream.** `MFTLib.Index` must never reference `MFTLib.Broker`, so the seam is declared in `MFTLib.Index` and supplied by the broker, exactly as `MftBlockProducer` already is (plan 2 decision 5). It takes every drive at once and returns one stream of drive-tagged batches, because the broker protocol starts a watch for all drives with a single `SendStartWatchAsync` frame and only then hands out per-drive enumerables. Merging lives on the broker side, where the session state does; the index side is one pump loop, which is what makes it testable on Linux with a fake. Merging the drives into one stream must preserve per-drive fault isolation: one drive's failure travels as an item on the stream and ends only that drive's watch, never the merged stream and never another drive (Task 7c). Decision 15 replaces the bare delegate with a three-member interface once the seam has to arm and disarm one drive on a running stream; the reason the seam is declared in `MFTLib.Index` is unchanged, and an interface declared there holds the namespace boundary exactly as the delegate did.

6. **No catch-up plumbing.** `BrokerMftBlockProducer` validates that the block header's cursor equals the cursor armed before the scan, so a watch resumed from the header cursor replays everything that changed during the scan. `BrokerScanResult.CatchUpEntries` therefore stays unread, and the git-wizard gap report's request to apply catch-up before publishing is satisfied without a second code path.

7. **There is no fallback from the MFT producer to a directory walk, in any form.** Today `FileIndex.Scanning.cs:123-135` swallows a producer failure and runs a full unelevated directory walk of the volume, which is exactly the multi-hour recursive scan file-wizard disables by default. Task 3 deletes that path outright rather than moving it behind an opt-in: a failed producer marks the drive `DriveState.Failed` and the other drives open normally. `ProducerPolicy` collapses to `Mft` and `Enumeration`, and the enumeration producer exists for Linux, the tests, and non-NTFS volumes, never as a recovery route.

8. **A failed drive is a `DriveStatus`, not an exception.** The git-wizard gap report proposed a new `IndexDriveScanException` type. Nothing needs it once the failure stops propagating: the drive appears in `Drives` with `DriveState.Failed` and its `MftProducerFailureMessage`. Not adding the type deletes more.

9. **`FileChange.Path` is materialized at the individual mutation.** The index raises `Changed` only after the whole batch has mutated the block, so reading `Entry.Path` inside a handler already sees later entries' effects. Each change therefore carries its own path string, built before the next entry is applied, and a rename carries the full old path built before `TryRenameRow` moves the row.

10. **`IndexScanProgress` is reshaped, not extended.** The current `(uint RowsWritten, string CurrentDirectory)` has no drive, no phase, and no total, and `CurrentDirectory` is meaningless for an MFT scan. It becomes an init-property record with a phase enum. Adding optional trailing parameters would be a compatibility shim under decision 1.

11. **`FileOptions.DeleteOnClose` replaces the managed delete.** `FileIndexOptions.NoCache`'s doc comment currently documents the weaker contract ("a process that is killed rather than disposed leaves the temp file behind") and `AddDriveAsync` carries `CleanupStaleNoCacheBlocks` to compensate. Both are deleted in Task 10 along with `DriveBlock`'s `deleteFileOnRelease` parameter.

12. **`IndexedDrive.FromWindowsVolume` declares its own `GetVolumeInformationW`.** The namespace rule forbids `MFTLib.Index` from naming a type under `MFTLib/Interop/`. A locally declared `LibraryImport` inside `MFTLib/Index/` is a platform invoke, not a reference to that namespace, so the boundary holds and the check in Task 15 stays clean.

13. **Out of scope, and not to be added opportunistically:** `SearchQuery.PathPattern`, `FileIndex.LargestDirectories`, `ProducerPolicy.WarmStartOnly`, `FileIndex.DuplicateSizes`, cloud-placeholder recall bits, bringing an offline drive online through `RescanAsync`, real filesystem paths for enumeration entries, and the file-wizard and git-wizard ports themselves. Those are plans 3 to 5 or deferred behind a measurement.

14. **`DriveStatus` does not gain a journal cursor.** The file-wizard gap report's Gap 1 wanted `JournalId`, `NextUsn`, and `Generation` on `DriveStatus` so a consumer could resume its own watch. The owner's decision that the index owns the watch removes the need: no consumer runs a broker watch, so no consumer needs the cursor. Not adding three public properties deletes more.

15. **The watch lifecycle is per drive end to end, and a stale journal cursor is a per-drive failure that requires a rescan.** Every layer of the watch arms, disarms, and fails one drive at a time: the broker protocol arms one drive into a live generation and disarms one drive out of it without touching the others, the host runs and retires one task per drive, the client keeps one channel per drive and one armed-drive record, the seam yields a per-drive failure as an item and exposes an arm-drive and a disarm-drive operation on the running stream, and the index freezes exactly one drive's cursor and reports exactly that drive's `WatchFailureMessage`. A cached journal cursor that has fallen outside the journal's live window is that drive's failure, never a warning followed by a resume from the current position: resuming skips USN records nothing will ever replay, and the index would then advance that drive's cursor past a gap and diverge from the volume in silence with no signal that it had. The recovery for it is `RescanAsync` on that drive, which disarms it, swaps its block, and re-arms it from the fresh header cursor while every other drive keeps streaming. This is ordered before the index work in Tasks 7b and 7c because file-wizard and git-wizard integrate after plan 2b and must never see a whole-generation re-arm; by the time they consume the seam, re-arming one drive is the only re-arm there is.

16. **A per-drive arm epoch on the wire is what makes a re-arm mean anything, and it is the client that issues it.** Frame ordering on the request path guarantees only that the host is quiesced for a drive before it re-arms that drive. The `JournalBatch` and `Error` frames the host had already written are travelling the other way, and once the client re-arms the drive they land in the fresh channel indistinguishable from post-re-arm ones, which would apply pre-rescan batches to a freshly rebuilt block and drag that drive's cursor backwards. So the client assigns each drive a monotonically increasing `uint` epoch every time it arms that drive, the `StartWatch` spec carries it beside that drive's cursor, the host tags every `JournalBatch` and per-drive `Error` it writes for that drive with the epoch of the arm that produced it, and the client's demux delivers a frame only while that is still the epoch the drive is armed under. There is no acknowledgement for `DisarmDrive` and no whole-generation barrier: `EndWatch` and `EndWatchAck` keep the meaning they have, and the epoch is what makes a barrier unnecessary rather than a replacement for one. The counter is client-wide and is never reset, so a frame from a generation whose stop timed out before its acknowledgement was read can never match a fresh arm either. Zero is never issued and is the value a scan, catch-up, or volume-query frame carries to say that no live arm produced it. The epoch is never a parameter and never a return value on any public member: a caller that could pass one could pass a stale one. Task 7b2 lands it, Task 7c depends on it, and it is not the same mechanism as the seam's own per-drive arm generation, which drops what has already crossed the wire and is sitting on the merged stream.

---

## Global Constraints

Every task's requirements implicitly include this section.

- **This project has never shipped, so there is no compatibility surface to protect.** No opt-in that preserves an old behavior, no deprecated or `[Obsolete]` member, no transitional field, no legacy overload, no parameter kept with a default so an existing caller still compiles, no version-bump ceremony and no migration path. A version constant is a discriminator that makes stale data or a stale binary fail loudly, not a history. When a task says delete, trace the call sites and delete; a caller that genuinely needs the old behavior is rewritten to ask for it plainly. If a change looks like it needs a shim, it needs a bigger delete instead, and that is the answer to give in the task report.
- **Target framework is `net10.0`.** No new `TargetFramework`, no `net10.0-windows`, no new `PackageReference` in any project.
- **C++ standard is C++17** (`CMAKE_CXX_STANDARD 17`). No C++20 constructs.
- **Namespace boundary.** `MFTLib.Index` never references `MFTLib.Mft`, `MFTLib.Broker`, `MFTLib.Internal`, or `MFTLib.Interop`, and never names a type declared under `MFTLib/Mft/`, `MFTLib/Broker/`, `MFTLib/Internal/`, or `MFTLib/Interop/`. The only allowed cross-folder types are `UsnJournalEntry`, `UsnJournalEntryOptions`, and `UsnReason` from `MFTLib/Journal/`. Code under `MFTLib/Broker/` may reference `MFTLib.Index`.
- **Files stay under 400 lines.** `.aislop/config.yml` sets `quality.maxFileLoc: 400`, `maxFunctionLoc: 80`, `maxNesting: 5`, `maxParams: 6`, `ci.failBelow: 100`. Never edit `.aislop/config.yml` and never suppress a rule to pass. `MFTLib/Index/FileIndex.Scanning.cs` is 342 lines and `MFTLib/Broker/Client/JournalBrokerClient.LiveWatch.cs` is 347; a task that grows either splits it by responsibility.
- **No abbreviations in identifiers.** `maximum` not `max`, `configuration` not `config`, `cancellationToken` not `ct`, `directory` not `dir`, `sequenceNumber` not `seq`. Applies to new C++ identifiers too.
- **No em-dashes** anywhere: code, comments, doc comments, markdown, commit messages.
- **Test-driven development per task.** Write the failing test, run it and watch it fail for the stated reason, implement, run it and watch it pass, commit. A lane that cannot run its own test on its own machine gets the remote loop in its dispatch and quotes both the red and the green output.
- **Every bug fix gets a regression test that fails before the fix and passes after.** Verify both states.
- **Never assert on wall-clock time.** No sleep followed by an elapsed-time assertion. Timestamps written into blocks come from an injected value.
- **No hard-coded machine-specific absolute paths** in production code. Test code derives every path from `Path.GetTempPath()` (managed) or the `/tmp` constant `linux_smoke_test.cpp` already uses (native).
- **Coverage baseline is 100 percent** on touched files under `MFTLib/Index/`. Defensive branches get a swappable `Func` indirection so a test can drive them, following `MFTLibNative` and `ElevationUtilities`. The non-admin `-NonInteractive` run legitimately settles near 99.82 percent line and 99.08 percent method because of the admin-only volume-handle hook; that is not a regression.
- **Windows-only tests guard with `OperatingSystem.IsWindows()` and `Assert.Inconclusive`,** the pattern already used at `MFTLib.Tests/JournalBrokerClientTests.ConnectionAndWatchFailures.cs:16`. Nothing this plan adds goes into the Linux exclusion filter in `scripts/coverage-linux.sh`. Everything that does not need a real Windows named section, a real volume, or a real journal must run on Linux through `MFTLib.Tests/TestSupport/InProcessBlockBrokerHarness.cs`.
- **Clean-tree invariant.** `git clean -ffxd` must stay safe. Nothing this plan produces lives untracked.
- **Commit after every task** with a green test run. Never leave the tree dirty between tasks.
- **Native error messages** go through the `SetErrorMessage` helper in `MFTLibNative/internal.h`, never `swprintf_s` directly.

## Build and verification notes

**Work only in the worktree `C:\Users\mtsch\MFTLib-worktrees\watch-bridge` on branch `feat/index-watch-bridge`.** Do not touch the main checkout at `C:\Users\mtsch\MFTLib`.

**Building.** Never `dotnet build` the solution: the dotnet CLI cannot build `MFTLibNative.vcxproj`, and a per-csproj build breaks the post-build copy because `$(SolutionDir)` does not resolve.

```powershell
MSBuild.exe MFTLib.sln -p:Configuration=Debug -p:Platform=x64
```

For a native-only rebuild inside a worktree, pass `SolutionDir` explicitly with a trailing backslash, otherwise a stale `MFTLibNative.dll` from another project's output on `PATH` (git-wizard's `bin` is the usual culprit) shadows the one just built:

```powershell
$msbuild = & "${env:ProgramFiles(x86)}\Microsoft Visual Studio\Installer\vswhere.exe" -products '*' -requires Microsoft.Component.MSBuild -property installationPath -latest
& "$msbuild\MSBuild\Current\Bin\amd64\MSBuild.exe" MFTLibNative\MFTLibNative.vcxproj -t:Build -p:Configuration=Release -p:Platform=x64 -p:PlatformToolset=v143 -p:SolutionDir="C:\Users\mtsch\MFTLib-worktrees\watch-bridge\"
Get-Item MFTLib.Tests\bin\x64\Release\net10.0\MFTLibNative.dll | Select-Object FullName, LastWriteTime
```

**Building the native library on Linux** (llamabox, or the Linux continuous-integration job):

```bash
cmake -S MFTLibNative -B build/linux -G Ninja -DCMAKE_BUILD_TYPE=Debug -DBUILD_TESTING=ON
cmake --build build/linux
LD_LIBRARY_PATH=build/linux ./build/linux/test/linux_smoke_test
```

**Where each surface can be verified.**

| Surface | Linux | Windows headless | Windows attended, elevated |
| --- | --- | --- | --- |
| Block format, `BlockWriter`, `JournalMutator`, queries | yes | yes | yes |
| Index watch pump against a fake `IIndexWatchSource` | yes | yes | yes |
| Broker watch source over the in-process pipe harness | yes | yes | yes |
| Native parse from an exported MFT file (`ParseMFTFromFileUtf8`) | yes, `linux_smoke_test` | no | yes |
| Managed parse from an exported MFT file (`MftVolume.ParseMFTFromFile`) | no | yes | yes |
| `OpenFileById` against a real volume root | no | yes, no elevation needed | yes |
| `GetVolumeInformationW` against a real drive | no | yes, no elevation needed | yes |
| `FileOptions.DeleteOnClose` surviving process death | no | yes, no elevation needed | yes |
| Real volume scan, real elevation, real journal | no | no, `RequiresAdmin` is skipped | yes, attended only |

**Test commands.**

```bash
dotnet test MFTLib.Tests/MFTLib.Tests.csproj -p:Platform=x64 --filter "TestCategory!=RequiresAdmin"
```

```powershell
.\scripts\run-coverage.ps1 -NonInteractive
```

```bash
bash scripts/coverage-linux.sh
aislop scan .        # before declaring a task done
aislop ci .          # the gate; exit non-zero below score 100
```

**Reference used for the sequence-number decision.** `FILE_ID_DESCRIPTOR` and `OpenFileById`: https://learn.microsoft.com/en-us/windows/win32/api/winbase/nf-winbase-openfilebyid . The 64-bit NTFS file reference is `(sequenceNumber << 48) | recordNumber`; `MFTLibNative/ntfs.h:79` already models `FILE_RECORD_SEGMENT_HEADER.SequenceNumber` and `MFTLibNative/usn/usn_journal.cpp:66` already masks it off the USN record's `FileReferenceNumber`.

---

## Dispatch waves

Tasks inside one wave touch disjoint files and may run in parallel lanes. A wave starts only after every task in the previous wave is committed. This plan is mostly serial for the same reason plan 2 was: `MFTLib/Index/FileIndex.Scanning.cs`, `MFTLib/Index/BlockFile.cs`, and `MFTLib/Index/JournalMutator.cs` are hubs that several tasks each need. Task 11 is the exception among the late tasks: it reshapes the progress types and never touches `FileIndex.Scanning.cs`, because `FileIndexOptions.Progress` keeps its declared type and the two call sites that pass it through are unchanged.

| Wave | Tasks | Lanes | Why serial against the previous wave |
| --- | --- | --- | --- |
| 1 | 1 | 1 | Every later block change builds on format version 2. |
| 2 | 2, 3 | 2 | The sequence region touches the block layout; the producer policy touches drive selection. Disjoint. |
| 3 | 4 | 1 | The native interface change rewrites `mft_api.h` and the record parser. |
| 4 | 5 | 1 | The USN change may share a native header with task 4; do not race it. |
| 5 | 6 | 1 | The watch pump defines the seam task 7 implements. |
| 6 | 7 | 1 | The broker watch source consumes task 6's delegate. |
| 6b | 7b | 1 | Per-drive arm and disarm reshapes the protocol, the host, and the client under the seam, and task 7c's recovery contract cannot be written until one drive can be re-armed on its own. |
| 6b2 | 7b2 | 1 | The arm epoch rewrites the same three frame codecs, the same host watch path, and the same client demux task 7b just landed, so it cannot run beside it. |
| 6c | 7c | 1 | Per-drive fault isolation reshapes the seam task 8's `JournalMutator` callers sit behind, and it can only reshape it once both halves of the seam exist and the protocol underneath arms one drive at a time, tagged with the arm that produced each frame. |
| 7 | 8 | 1 | `FileChange` gaining a path rewrites `JournalMutator`. |
| 8 | 9 | 1 | Hydration edits the methods task 8 just rewrote. |
| 9 | 10, 11 | 2 | Delete-on-close edits `BlockFile`, `NamedBlockSection`, `DriveBlock`, and `FileIndex.Scanning.cs`; progress forwarding edits the progress types, the enumeration producer, and `BrokerMftBlockProducer`. Disjoint. |
| 10 | 12, 13 | 2 | Opening by file id and the volume factory touch disjoint files. |
| 11 | 14 | 1 | Documentation describes the finished state. |
| 12 | 15 | 1 | The gate runs last. |

---

## Phase 1: Block format

### Task 1: Live row count in the header

**Files:**
- Modify: `MFTLib/Index/BlockHeader.cs`, `MFTLib/Index/BlockLayout.cs`, `MFTLib/Index/BlockWriter.cs`, `MFTLib/Index/DriveStatus.cs`, `MFTLib/Index/FileIndex.cs`
- Modify: `docs/index-format.md`
- Test: `MFTLib.Tests/Index/BlockHeaderTests.cs`, `MFTLib.Tests/Index/BlockWriterTests.cs`, `MFTLib.Tests/Index/FileIndexDriveStatusTests.cs`

**Interfaces:**
- Consumes: nothing new.
- Produces:
  - `[FieldOffset(88)] public uint LiveRowCount;` on `BlockHeader`
  - `public const uint BlockLayout.FormatVersion = 2;` and `public const int BlockLayout.HeaderFieldBytes = 96;`
  - `public required uint LiveRowCount { get; init; }` on `DriveStatus`

**Design notes:** `DriveStatus.RowCount` is the header's "highest used slot plus one", so on an MFT block it counts free slots and tombstones. Every consumer surface that shows a file count needs the live count instead. The header page is 4096 bytes and only 88 are declared (`MFTLib/Index/BlockLayout.cs:19`), so the field costs no growth on disk, but it does change what a block's bytes mean, so `FormatVersion` must stop matching the blocks already on disk. `HeaderFieldBytes` goes to 96 rather than 92 so the explicit struct size stays 8-byte aligned. There is no migration: a block carrying the old number fails `BlockHeader.Validate` and is discarded and rescanned, which is what the owner decided for the plan 2 attended block on C:.

`BlockWriter` is the only writer of rows, so it is the only place the count is maintained. `TryWriteRow` must read the row's existing flags before it publishes the new descriptor word, because a create can reuse a previously tombstoned slot.

- [ ] **Step 1: Write the failing tests**

Add to `MFTLib.Tests/Index/BlockHeaderTests.cs`:

```csharp
[TestMethod]
public void LiveRowCount_SitsAtHeaderOffsetEightyEight()
{
    Assert.AreEqual(88, (int)Marshal.OffsetOf<BlockHeader>(nameof(BlockHeader.LiveRowCount)));
}

[TestMethod]
public void FormatVersion_IsTwo()
{
    Assert.AreEqual(2u, BlockLayout.FormatVersion);
}
```

Add to `MFTLib.Tests/Index/BlockWriterTests.cs`:

```csharp
[TestMethod]
public void LiveRowCount_CountsLiveRowsNotSlots()
{
    using var builder = new SyntheticBlockBuilder();
    using var block = builder.OpenForWriting();
    var writer = new BlockWriter(block);
    var columns = new RowColumns(0, RowFlags.InUse, 0, 10, 0);

    Assert.IsTrue(writer.TryWriteRow(0, "a.txt", in columns));
    Assert.IsTrue(writer.TryWriteRow(7, "b.txt", in columns));

    Assert.AreEqual(8u, block.Header.RowCount);
    Assert.AreEqual(2u, block.Header.LiveRowCount);
}

[TestMethod]
public void LiveRowCount_DropsOnTombstoneAndDoesNotDoubleCount()
{
    using var builder = new SyntheticBlockBuilder();
    using var block = builder.OpenForWriting();
    var writer = new BlockWriter(block);
    var columns = new RowColumns(0, RowFlags.InUse, 0, 10, 0);
    writer.TryWriteRow(3, "a.txt", in columns);

    writer.MarkTombstone(3);
    Assert.AreEqual(0u, block.Header.LiveRowCount);

    writer.MarkTombstone(3);
    Assert.AreEqual(0u, block.Header.LiveRowCount);

    Assert.IsTrue(writer.TryWriteRow(3, "reused.txt", in columns));
    Assert.AreEqual(1u, block.Header.LiveRowCount);
}
```

`SyntheticBlockBuilder` already exposes the members plan 2 added; if `OpenForWriting` is not among them, add it as the writable counterpart of `OpenForReading` in the same commit.

Add to `MFTLib.Tests/Index/FileIndexDriveStatusTests.cs`:

```csharp
[TestMethod]
public async Task DriveStatus_ReportsLiveRowCountFromTheHeader()
{
    using var temporaryRoot = new TemporaryDirectory();
    File.WriteAllText(Path.Combine(temporaryRoot.Path, "one.txt"), "1");
    File.WriteAllText(Path.Combine(temporaryRoot.Path, "two.txt"), "2");
    await using var index = await FileIndex.OpenAsync(new FileIndexOptions
    {
        Drives = [new IndexedDrive('T', temporaryRoot.Path, 4242)],
        CacheDirectory = temporaryRoot.Path,
        ProducerPolicy = ProducerPolicy.Enumeration
    }, TestContext.CancellationTokenSource.Token);

    var status = index.Drives.Single();
    Assert.AreEqual(3u, status.LiveRowCount);
    Assert.IsTrue(status.LiveRowCount <= status.RowCount);
}
```

Three rows: the root plus two files. Use whatever temporary-directory helper `MFTLib.Tests` already provides rather than introducing a new one.

- [ ] **Step 2: Run to verify they fail**

```bash
dotnet test MFTLib.Tests/MFTLib.Tests.csproj -p:Platform=x64 --filter "FullyQualifiedName~BlockHeaderTests|FullyQualifiedName~BlockWriterTests"
```

Expected: FAIL to compile, `BlockHeader` has no `LiveRowCount` and `DriveStatus` has no `LiveRowCount`.

- [ ] **Step 3: Implement**

In `MFTLib/Index/BlockHeader.cs` add, after `NamePoolOffset`:

```csharp
/// <summary>
///     Rows that are in use and not tombstoned. <see cref="RowCount" /> is the highest used
///     slot plus one, so on a block whose rows are dense by record number it counts free
///     slots and deleted files too. Maintained by <see cref="BlockWriter" />, the only writer
///     of rows, so both producers and the journal mutator get it without their own bookkeeping.
/// </summary>
[FieldOffset(88)] public uint LiveRowCount;
```

In `MFTLib/Index/BlockLayout.cs` set `FormatVersion` to `2` and `HeaderFieldBytes` to `96`. The `FormatVersion` doc comment stays as it is: a mismatch means discard the block and rescan, and there is no migration path. Do not add a changelog of past values to it.

In `MFTLib/Index/BlockWriter.cs`, inside `TryWriteRow`, before the descriptor-word publish:

```csharp
var previousFlags = FileRow.DescriptorFlags(FileRow.ReadDescriptorWord(in row));
var wasLive = (previousFlags & RowFlags.InUse) != 0 && (previousFlags & RowFlags.Tombstone) == 0;
var isLive = (columns.Flags & RowFlags.InUse) != 0 && (columns.Flags & RowFlags.Tombstone) == 0;
```

and after the publish, `if (isLive && !wasLive) { header.LiveRowCount++; } else if (!isLive && wasLive) { header.LiveRowCount--; }`.

In `AddRowFlags`, decrement when the row transitions from live to tombstoned:

```csharp
var flags = FileRow.DescriptorFlags(descriptor);
if (additionalFlags.HasFlag(RowFlags.Tombstone) &&
    (flags & RowFlags.InUse) != 0 && (flags & RowFlags.Tombstone) == 0)
{
    Block.Header.LiveRowCount--;
}
```

Keep the whole read-modify-write inside the existing single descriptor-word store; do not add a second store.

In `MFTLib/Index/DriveStatus.cs` add `public required uint LiveRowCount { get; init; }` with a doc comment pointing at the difference from `RowCount`. In `FileIndex.DescribeDrive` (`MFTLib/Index/FileIndex.cs:228`) fill it from `header.LiveRowCount`, and set `LiveRowCount = 0` on the blockless `DriveStatus` built in `AddDriveAsync` (`MFTLib/Index/FileIndex.Scanning.cs:14`).

Update the header table in `docs/index-format.md` with a row at offset 88.

- [ ] **Step 4: Run to verify they pass, then the full suite**

```bash
dotnet test MFTLib.Tests/MFTLib.Tests.csproj -p:Platform=x64 --filter "TestCategory!=RequiresAdmin"
```

Expected: PASS. Any existing test that hard-codes format version 1 or `HeaderFieldBytes == 88` is updated in this commit; that is the intended break, not a regression.

- [ ] **Step 5: Commit**

```bash
git add MFTLib/Index/BlockHeader.cs MFTLib/Index/BlockLayout.cs MFTLib/Index/BlockWriter.cs MFTLib/Index/DriveStatus.cs MFTLib/Index/FileIndex.cs MFTLib/Index/FileIndex.Scanning.cs docs/index-format.md MFTLib.Tests/Index
git commit -m "feat(index): live row count in the block header, format version 2"
```

---

### Task 2: Sequence-number region

**Files:**
- Modify: `MFTLib/Index/BlockLayout.cs`, `MFTLib/Index/BlockHeader.cs`, `MFTLib/Index/BlockFile.cs`, `MFTLib/Index/BlockWriter.cs`, `MFTLib/Index/RowColumns.cs`, `MFTLib/Index/EnumerationProducer.cs`
- Modify: `docs/index-format.md`
- Test: `MFTLib.Tests/Index/BlockLayoutTests.cs`, `MFTLib.Tests/Index/BlockWriterTests.cs`, `MFTLib.Tests/Index/BlockValidationMatrixTests.cs`

**Interfaces:**
- Consumes: `BlockLayout.FormatVersion == 2` from Task 1.
- Produces:
  - `public static long BlockLayout.SequenceRegionBytes(uint slotCapacity)` and `public static long BlockLayout.SequenceRegionOffset(uint slotCapacity)`
  - `[FieldOffset(96)] public ulong SequenceRegionOffset;` on `BlockHeader`, `HeaderFieldBytes` 104
  - `public Span<ushort> SequenceNumbers { get; }` on `BlockFile`
  - `RowColumns` gains a sixth component, `ushort SequenceNumber`

**Design notes:** `OpenFileById` needs `(sequenceNumber << 48) | recordNumber`, measured on 2026-09-09 (decision 2). The region sits between the row region and the name pool, one `ushort` per slot, 4 KB aligned like every other region, so `NamePoolOffset` shifts by its length. `RowColumns` reaches six positional components, exactly the `maxParams: 6` limit; a seventh column later needs regrouping, not a seventh parameter. Enumeration blocks have no sequence numbers and write zero, which is also what a hydrated journal row writes when the journal did not supply one. `FormatVersion` does not change again: Task 1 already set it to a value no block on disk carries.

- [ ] **Step 1: Write the failing tests**

Add to `MFTLib.Tests/Index/BlockLayoutTests.cs`:

```csharp
[TestMethod]
public void SequenceRegion_SitsBetweenTheRowRegionAndTheNamePool()
{
    const uint slotCapacity = 1000;
    var sequenceOffset = BlockLayout.SequenceRegionOffset(slotCapacity);
    Assert.AreEqual(BlockLayout.RowRegionOffset + BlockLayout.RowRegionBytes(slotCapacity), sequenceOffset);
    Assert.AreEqual(0, sequenceOffset % BlockLayout.PageSize);
    Assert.AreEqual(sequenceOffset + BlockLayout.SequenceRegionBytes(slotCapacity),
        BlockLayout.NamePoolOffset(slotCapacity));
}

[TestMethod]
public void SequenceRegion_IsTwoBytesPerSlotRoundedToAPage()
{
    Assert.AreEqual(BlockLayout.PageSize, BlockLayout.SequenceRegionBytes(1));
    Assert.AreEqual(8192, BlockLayout.SequenceRegionBytes(4097));
}
```

Add to `MFTLib.Tests/Index/BlockWriterTests.cs`:

```csharp
[TestMethod]
public void TryWriteRow_StoresTheSequenceNumberBesideTheRow()
{
    using var builder = new SyntheticBlockBuilder();
    using var block = builder.OpenForWriting();
    var writer = new BlockWriter(block);

    Assert.IsTrue(writer.TryWriteRow(5, "root", new RowColumns(5, RowFlags.InUse | RowFlags.Directory, 0, 0, 0, 42)));

    Assert.AreEqual((ushort)42, block.SequenceNumbers[5]);
    Assert.AreEqual((ushort)0, block.SequenceNumbers[4]);
}
```

- [ ] **Step 2: Run to verify they fail**

```bash
dotnet test MFTLib.Tests/MFTLib.Tests.csproj -p:Platform=x64 --filter "FullyQualifiedName~BlockLayoutTests|FullyQualifiedName~BlockWriterTests"
```

Expected: FAIL to compile, `BlockLayout` has no `SequenceRegionOffset` and `RowColumns` takes five components.

- [ ] **Step 3: Implement**

In `MFTLib/Index/BlockLayout.cs`:

```csharp
public const int SequenceBytes = 2;

public static long SequenceRegionOffset(uint slotCapacity)
{
    return RowRegionOffset + RowRegionBytes(slotCapacity);
}

public static long SequenceRegionBytes(uint slotCapacity)
{
    return AlignUp((long)slotCapacity * SequenceBytes, PageSize);
}
```

`NamePoolOffset` becomes `SequenceRegionOffset(slotCapacity) + SequenceRegionBytes(slotCapacity)`. `TotalBlockBytes` is unchanged in shape because it already builds on `NamePoolOffset`.

In `MFTLib/Index/BlockHeader.cs` add `[FieldOffset(96)] public ulong SequenceRegionOffset;` and raise `BlockLayout.HeaderFieldBytes` to `104`. `BlockFile.InitializeHeader` fills it from `BlockLayout.SequenceRegionOffset(options.SlotCapacity)`, and `BlockHeader.Validate` rejects a header whose `SequenceRegionOffset` does not equal that computed value, alongside the existing row-region and name-pool offset rules.

In `MFTLib/Index/BlockFile.cs` add `SequenceNumbers` as a `Span<ushort>` over `SequenceRegionOffset` for `Header.SlotCapacity` elements, built exactly the way `Rows` and `NamePoolCharacters` already are from the mapped base pointer.

In `MFTLib/Index/RowColumns.cs` append `ushort SequenceNumber` as the sixth component with the doc line "NTFS record sequence number, zero when the producer has none." In `BlockWriter.TryWriteRow`, after the parent and attribute writes and before the descriptor-word publish, `Block.SequenceNumbers[(int)rowIndex] = columns.SequenceNumber;`.

`MFTLib/Index/EnumerationProducer.cs` passes `0` for the new component at every `RowColumns` construction. Update every other `RowColumns` construction the compiler flags, including `MFTLib/Index/JournalMutator.cs:91` and `MFTLib/Broker/Host/MftBlockRowWriter.cs:98`, passing `0` for now; tasks 4 and 5 fill them.

Add the region to `docs/index-format.md`: a new section after the row region, and a header table row at offset 96.

- [ ] **Step 4: Run to verify they pass, then the full suite on both platforms**

```bash
dotnet test MFTLib.Tests/MFTLib.Tests.csproj -p:Platform=x64 --filter "TestCategory!=RequiresAdmin"
bash scripts/coverage-linux.sh
```

Expected: PASS. `BlockValidationMatrixTests` gains a case for a corrupted `SequenceRegionOffset`.

- [ ] **Step 5: Commit**

```bash
git add MFTLib/Index MFTLib/Broker/Host/MftBlockRowWriter.cs docs/index-format.md MFTLib.Tests/Index
git commit -m "feat(index): sequence-number region beside the row region"
```

---

## Phase 2: Drive failure isolation

### Task 3: Per-drive producer failure

**Files:**
- Modify: `MFTLib/Index/ProducerPolicy.cs`, `MFTLib/Index/DriveState.cs`, `MFTLib/Index/FileIndex.Scanning.cs`
- Test: `MFTLib.Tests/Index/FileIndexProducerSelectionTests.cs`

**Interfaces:**
- Consumes: nothing new.
- Produces:
  - `ProducerPolicy` reduced to exactly two members, `Mft` and `Enumeration`
  - `DriveState.Failed` as a fifth member
  - `ProduceDriveBlockAsync` returns `ScanDriveResult?`; `null` means the drive failed

**Design notes:** Today `MftOnly` lets a producer failure propagate out of `AddDriveAsync`, and `FileIndex.OpenAsync` (`MFTLib/Index/FileIndex.cs:99-105`) releases every block already opened and rethrows, so one declined UAC prompt fails the whole index. `Auto` does the opposite and silently runs `ScanDrive`, the full unelevated enumeration walk of the volume. Both are wrong for a consumer with a UAC prompt, in opposite directions, and both gap reports found it independently.

**There is no fallback path after this task, in any form.** A failed MFT producer never starts a directory walk: not automatically, not behind a policy member, not behind a boolean. The enumeration producer is reachable only by asking for it, which is what Linux, the tests, and non-NTFS volumes do. That collapses `ProducerPolicy` to two members:

```csharp
public enum ProducerPolicy
{
    /// <summary>
    ///     Build every drive's block with <see cref="FileIndexOptions.MftProducer" />. A drive
    ///     whose producer fails is reported as <see cref="DriveState.Failed" /> and gets no
    ///     block; it is never rebuilt by a directory walk instead.
    /// </summary>
    Mft,

    /// <summary>
    ///     Build every drive's block by walking its directory tree, ignoring
    ///     <see cref="FileIndexOptions.MftProducer" /> entirely. The only policy that reads the
    ///     filesystem recursively, and it is never selected on a caller's behalf.
    /// </summary>
    Enumeration
}
```

`Auto`, `MftOnly`, and `EnumerationOnly` are deleted. `Mft` is the first member and therefore the default, deliberately: an options object that names no policy and no producer throws rather than quietly walking a volume. `Mft` with a null `MftProducer` is a configuration error and throws at the first drive. Cancellation still propagates from both policies and is never recorded as a drive failure.

Every existing test that names a deleted member is rewritten to one of the two, and any test that relied on `Auto` recovering from a failed producer is deleted rather than adapted; the behavior it pinned is gone on purpose.

The blockless-drive list in `FileIndex.Scanning.cs:14` is currently named for offline drives only; rename the field to `_blocklessDriveStatuses` in this commit, since it now carries two states.

- [ ] **Step 1: Write the failing tests**

Add to `MFTLib.Tests/Index/FileIndexProducerSelectionTests.cs`:

```csharp
[TestMethod]
public async Task Mft_MarksTheFailedDriveAndKeepsTheOthers()
{
    using var firstRoot = new TemporaryDirectory();
    using var secondRoot = new TemporaryDirectory();
    await using var index = await FileIndex.OpenAsync(new FileIndexOptions
    {
        Drives = [new IndexedDrive('T', firstRoot.Path, 1), new IndexedDrive('U', secondRoot.Path, 2)],
        CacheDirectory = firstRoot.Path,
        ProducerPolicy = ProducerPolicy.Mft,
        MftProducer = (request, _) => request.DriveLetter == 'T'
            ? throw new UnauthorizedAccessException("elevation declined")
            : Task.FromResult(FakeMftBlocks.Produce(request))
    }, TestContext.CancellationTokenSource.Token);

    var failed = index.Drives.Single(drive => drive.DriveLetter == 'T');
    Assert.AreEqual(DriveState.Failed, failed.State);
    Assert.AreEqual("elevation declined", failed.MftProducerFailureMessage);
    Assert.AreEqual(DriveState.Ready, index.Drives.Single(drive => drive.DriveLetter == 'U').State);
}

[TestMethod]
public async Task Mft_NeverWalksTheDirectoryTreeAfterAProducerFailure()
{
    using var root = new TemporaryDirectory();
    File.WriteAllText(Path.Combine(root.Path, "should-not-be-indexed.txt"), "x");
    await using var index = await FileIndex.OpenAsync(new FileIndexOptions
    {
        Drives = [new IndexedDrive('T', root.Path, 1)],
        CacheDirectory = root.Path,
        ProducerPolicy = ProducerPolicy.Mft,
        MftProducer = (_, _) => throw new UnauthorizedAccessException("elevation declined")
    }, TestContext.CancellationTokenSource.Token);

    Assert.AreEqual(DriveState.Failed, index.Drives.Single().State);
    Assert.AreEqual(0, index.Search(new SearchQuery(null)).Count);
}

[TestMethod]
public async Task Mft_WithNoProducerConfiguredIsAConfigurationError()
{
    using var root = new TemporaryDirectory();
    await Assert.ThrowsExceptionAsync<InvalidOperationException>(() => FileIndex.OpenAsync(new FileIndexOptions
    {
        Drives = [new IndexedDrive('T', root.Path, 1)],
        CacheDirectory = root.Path,
        ProducerPolicy = ProducerPolicy.Mft
    }, TestContext.CancellationTokenSource.Token));
}

[TestMethod]
public async Task Enumeration_IgnoresAConfiguredMftProducerEntirely()
{
    using var root = new TemporaryDirectory();
    File.WriteAllText(Path.Combine(root.Path, "indexed.txt"), "x");
    await using var index = await FileIndex.OpenAsync(new FileIndexOptions
    {
        Drives = [new IndexedDrive('T', root.Path, 1)],
        CacheDirectory = root.Path,
        ProducerPolicy = ProducerPolicy.Enumeration,
        MftProducer = (_, _) => throw new InvalidOperationException("the MFT producer must not be called")
    }, TestContext.CancellationTokenSource.Token);

    var status = index.Drives.Single();
    Assert.AreEqual(DriveState.Ready, status.State);
    Assert.AreEqual(ProducerKind.Enumeration, status.ProducerKind);
    Assert.IsNull(status.MftProducerFailureMessage);
    Assert.IsNotNull(index.Find(@"T:\indexed.txt"));
}

[TestMethod]
public async Task Mft_StillPropagatesCancellation()
{
    using var root = new TemporaryDirectory();
    using var cancellation = new CancellationTokenSource();
    await Assert.ThrowsExceptionAsync<OperationCanceledException>(() => FileIndex.OpenAsync(new FileIndexOptions
    {
        Drives = [new IndexedDrive('T', root.Path, 1)],
        CacheDirectory = root.Path,
        ProducerPolicy = ProducerPolicy.Mft,
        MftProducer = (_, token) => { cancellation.Cancel(); token.ThrowIfCancellationRequested(); return null!; }
    }, cancellation.Token));
}
```

`FakeMftBlocks.Produce` is the existing test helper that builds a completed MFT-kind block for a request; if the file does not name it that, use whatever `FileIndexProducerSelectionTests` already uses to satisfy a successful producer.

- [ ] **Step 2: Run to verify they fail**

```bash
dotnet test MFTLib.Tests/MFTLib.Tests.csproj -p:Platform=x64 --filter "FullyQualifiedName~FileIndexProducerSelectionTests"
```

Expected: FAIL to compile on `DriveState.Failed`, `ProducerPolicy.Mft`, and `ProducerPolicy.Enumeration`.

- [ ] **Step 3: Implement**

Replace `ProducerPolicy`'s three members with the two shown in the design notes, doc comments included. Add `Failed` to `DriveState` with a doc comment pointing at `DriveStatus.MftProducerFailureMessage`.

Rewrite `ProduceDriveBlockAsync` to return `Task<ScanDriveResult?>`:

```csharp
async Task<ScanDriveResult?> ProduceDriveBlockAsync(IndexedDrive drive, ushort driveOrdinal, string blockPath,
    CancellationToken cancellationToken)
{
    if (_options.ProducerPolicy == ProducerPolicy.Enumeration)
    {
        return await Task
            .Run(() => ScanDrive(drive, driveOrdinal, blockPath, _options.NoCache, cancellationToken),
                cancellationToken)
            .ConfigureAwait(false);
    }

    var producer = _options.MftProducer ?? throw new InvalidOperationException(
        $"{nameof(ProducerPolicy)}.{nameof(ProducerPolicy.Mft)} requires " +
        $"{nameof(FileIndexOptions)}.{nameof(FileIndexOptions.MftProducer)} to be set.");

    try
    {
        var mftScanResult = await RunMftProducerAsync(drive, driveOrdinal, blockPath, producer,
            cancellationToken).ConfigureAwait(false);
        lock (_stateLock)
        {
            _mftProducerFailureMessagesByOrdinal.Remove(driveOrdinal);
        }

        return mftScanResult;
    }
    catch (Exception exception) when (exception is not OperationCanceledException)
    {
        lock (_stateLock)
        {
            _mftProducerFailureMessagesByOrdinal[driveOrdinal] = exception.Message;
        }

        return null;
    }
}
```

There is exactly one `ScanDrive` call site left in the method and the MFT arm cannot reach it, which is the shape that makes the no-fallback rule checkable by reading rather than only by testing. Keep `ProduceDriveBlockAsync` under 80 lines.

In `AddDriveAsync`, a null result records the failed drive and returns without adding a block:

```csharp
var scanResult = await ProduceDriveBlockAsync(drive, driveOrdinal, ComputeScanBlockPath(drive),
    cancellationToken).ConfigureAwait(false);
if (scanResult is null)
{
    lock (_stateLock)
    {
        _blocklessDriveStatuses.Add(new DriveStatus
        {
            DriveLetter = driveLetter,
            ProducerKind = ProducerKind.Mft,
            State = DriveState.Failed,
            RowCount = 0,
            LiveRowCount = 0,
            ScanTimestamp = DateTime.MinValue,
            CompactionNeeded = false,
            WatchSupported = false,
            MftProducerFailureMessage = _mftProducerFailureMessagesByOrdinal.GetValueOrDefault(driveOrdinal)
        });
    }

    return;
}
```

A failed drive consumed the ordinal it was assigned, so do not reuse it; `driveOrdinal` is derived from `_driveBlocks.Count`, which the early return leaves unchanged, meaning the next drive takes the same ordinal. That is correct, and it is why the failure message must be read out of the dictionary into the status here rather than left keyed by an ordinal a later drive will claim. Remove the ordinal's entry from `_mftProducerFailureMessagesByOrdinal` after copying it.

`RescanAsync` calls the same method; a null result there leaves the previous block in place and records the message. Add that branch explicitly rather than letting a null dereference.

- [ ] **Step 4: Run to verify they pass, then the full suite**

```bash
dotnet test MFTLib.Tests/MFTLib.Tests.csproj -p:Platform=x64 --filter "TestCategory!=RequiresAdmin"
bash scripts/coverage-linux.sh
```

Expected: PASS, including the existing `RescanAsync` producer-selection regression test from plan 2's task 15.

- [ ] **Step 5: Commit**

```bash
git add MFTLib/Index/ProducerPolicy.cs MFTLib/Index/DriveState.cs MFTLib/Index/FileIndex.Scanning.cs MFTLib.Tests/Index
git commit -m "feat(index): a failed MFT producer fails one drive, not the whole index"
```

---

## Phase 3: Sequence numbers from the substrate

### Task 4: Native record sequence number

**Files:**
- Modify: `MFTLibNative/mft_api.h`, `MFTLibNative/mft/mft.records.cpp`, `MFTLibNative/test/linux_smoke_test.cpp`
- Modify: `MFTLib/Mft/MftRecord.cs`, `MFTLib/Internal/MFTLibNative.cs` (the `ExpectedMftNativeAbiVersion` constant)
- Test: `MFTLibNative/test/linux_smoke_test.cpp`, `MFTLib.Tests/MftRecordTests.cs`

**Interfaces:**
- Consumes: `RowColumns.SequenceNumber` from Task 2.
- Produces:
  - `constexpr uint32_t MFT_NATIVE_ABI_VERSION = 1;`, and `ExpectedMftNativeAbiVersion` set to `1` to match
  - `uint16_t sequenceNumber;` appended to `MftCompactEntry`, taking the packed stride from 48 to 50
  - `public ushort SequenceNumber { get; }` on `MftRecord`

**Design notes:** `OpenFileById` needs the sequence number (decision 2) and `MFTLibNative/ntfs.h:79` already declares `FILE_RECORD_SEGMENT_HEADER.SequenceNumber`, so the parser reads it and drops it today. `MftCompactEntry` is inside `#pragma pack(push, 1)` and currently sums to exactly 48 bytes, so appending a `uint16_t` at the end leaves every existing offset alone and takes the stride to 50. That is an interface break, and there is no consumer of this interface outside the repository, so `MFT_NATIVE_ABI_VERSION` and its managed counterpart are simply set to `1`. A stale DLL then mismatches and is rejected by the existing check, which is the whole purpose of the constant.

The synthetic fixture already writes `SequenceNumber = recordIndex + 1` (`MFTLibNative/mft/mft_synthetic.fixture.cpp:111`), so the expected values are deterministic and need no new fixture.

**This lane cannot run its own native test on Windows.** The dispatch must carry the llamabox loop: copy the touched native files into the helper worktree, run the CMake build and `linux_smoke_test`, and quote both the red run (pre-change source, the new assertion failing) and the green run. A native test the author never watched fail is a guess; plan 2's task 4 shipped exactly that mistake.

- [ ] **Step 1: Write the failing tests**

In `MFTLibNative/test/linux_smoke_test.cpp`, inside the existing synthetic-parse case, add:

```cpp
// The fixture writes SequenceNumber = recordIndex + 1 for every record.
for (uint64_t index = 0; index < result->usedRecords; ++index) {
    const MftCompactEntry& entry = result->entries[index];
    expect(entry.sequenceNumber == static_cast<uint16_t>(entry.recordNumber + 1),
           "sequence number matches the fixture");
}
```

Use the file's own assertion helper rather than `expect` if it is named differently.

In `MFTLib.Tests/MftRecordTests.cs` add a Windows-only test that parses the generated synthetic MFT through `MftVolume.ParseMFTFromFile` and asserts `record.SequenceNumber == record.RecordNumber + 1` for the first ten used records, guarded with `if (!OperatingSystem.IsWindows()) { Assert.Inconclusive("Windows-only: managed parse entry point"); }`.

- [ ] **Step 2: Run to verify they fail**

```bash
cmake --build build/linux && LD_LIBRARY_PATH=build/linux ./build/linux/test/linux_smoke_test
```

Expected: FAIL to compile, `MftCompactEntry` has no `sequenceNumber`.

- [ ] **Step 3: Implement**

In `MFTLibNative/mft_api.h`, set `MFT_NATIVE_ABI_VERSION` to `1` and append to `MftCompactEntry`:

```cpp
    uint16_t sequenceNumber;  // NTFS record sequence; combined with recordNumber it is the file reference
```

In `MFTLibNative/mft/mft.records.cpp`, where `outEntry->fileAttributes` is assigned (line 220), also assign `outEntry->sequenceNumber = header->SequenceNumber;` from the `FILE_RECORD_SEGMENT_HEADER` already in hand. Use the existing local name for that header rather than introducing a new one, and do not rename `nameAttr` (out of scope).

In `MFTLib/Mft/MftRecord.cs` add a `readonly ushort _sequenceNumber` field, a `public ushort SequenceNumber => _sequenceNumber;` property, wire it through the native read path and the materialized factory, and add `SequenceNumber` to `MftRecordTestValues`. Set `ExpectedMftNativeAbiVersion` to `1`, matching the native constant exactly; the two are compared for equality, so they move together or the check fires on every parse.

In `MFTLib/Broker/Host/MftBlockRowWriter.cs:98`, pass `record.SequenceNumber` as the sixth `RowColumns` component instead of the `0` Task 2 left there.

- [ ] **Step 4: Run to verify they pass**

```bash
cmake --build build/linux && LD_LIBRARY_PATH=build/linux ./build/linux/test/linux_smoke_test
```

Expected: PASS on Linux. Then on Windows, rebuild native with the `SolutionDir` recipe, confirm the DLL timestamp, and run:

```bash
dotnet test MFTLib.Tests/MFTLib.Tests.csproj -p:Platform=x64 --filter "TestCategory!=RequiresAdmin"
```

Expected: PASS. An interface-version mismatch failure here means the stale-DLL trap from the build notes; check the timestamp before debugging anything else.

- [ ] **Step 5: Commit**

```bash
git add MFTLibNative MFTLib/Mft/MftRecord.cs MFTLib/Internal MFTLib/Broker/Host/MftBlockRowWriter.cs MFTLib.Tests
git commit -m "feat(native): carry the record sequence number"
```

---

### Task 5: USN journal sequence number

**Files:**
- Modify: `MFTLibNative/usn/usn_journal.cpp`, and the native USN entry struct whose `recordNumber` and `parentRecordNumber` fields are assigned at `MFTLibNative/usn/usn_journal.cpp:68-69`
- Modify: `MFTLib/Journal/UsnJournalEntry.cs`, `MFTLib/Journal/UsnJournalEntryOptions.cs`
- Create test: `MFTLib.Tests/UsnJournalEntryTests.cs` (the existing UsnJournal test files are already over the 400-line cap)

**Interfaces:**
- Consumes: nothing from Task 4; this is the second half of the same idea.
- Produces: `public ushort SequenceNumber { get; }` on `UsnJournalEntry`, and the matching `init` property on `UsnJournalEntryOptions`.

**Design notes:** `MFTLibNative/usn/usn_journal.cpp:66-69` masks the sequence number off the USN record's `FileReferenceNumber` and throws it away. It is the same 16 bits `OpenFileById` needs, and a journal-created row is otherwise unopenable. `UsnJournalEntry.RecordNumber`'s doc comment currently advertises the stripping as a feature ("48-bit, sequence number stripped"); rewrite it to say the stripped identifier is the row index and the sequence number is carried separately.

The interface version constant does not change again: Task 4 already set it to `1`.

- [ ] **Step 1: Write the failing test**

Create `MFTLib.Tests/UsnJournalEntryTests.cs` (MSTest class `UsnJournalEntryTests`, same usings as `UsnJournalTests.cs`) containing:

```csharp
[TestMethod]
public void SequenceNumber_RoundTripsThroughCreate()
{
    var entry = UsnJournalEntry.Create(new UsnJournalEntryOptions
    {
        RecordNumber = 1197729,
        ParentRecordNumber = 5,
        SequenceNumber = 169,
        Usn = 42,
        Timestamp = new DateTime(2026, 9, 9, 0, 0, 0, DateTimeKind.Utc),
        Reason = UsnReason.FileCreate,
        FileAttributes = FileAttributes.Normal,
        FileName = "probe.txt"
    });

    Assert.AreEqual((ushort)169, entry.SequenceNumber);
    Assert.AreEqual(1197729ul, entry.RecordNumber);
}
```

Add the native round-trip assertion to whichever existing test drives `NativeUsnJournalEntryData` through the marshaling path, asserting that a `FileReferenceNumber` of `0x00A90000001246A1` yields `RecordNumber == 0x1246A1` and `SequenceNumber == 0xA9`. Those are the values measured on chonkers on 2026-09-09 for a real temp file, so they are a realistic pairing rather than an invented one.

- [ ] **Step 2: Run to verify it fails**

```bash
dotnet test MFTLib.Tests/MFTLib.Tests.csproj -p:Platform=x64 --filter "FullyQualifiedName~UsnJournalEntryTests"
```

Expected: FAIL to compile, `UsnJournalEntryOptions` has no `SequenceNumber`.

- [ ] **Step 3: Implement**

In `MFTLibNative/usn/usn_journal.cpp`, beside the two existing masked assignments:

```cpp
    entry.sequenceNumber = static_cast<uint16_t>(usnRecord->FileReferenceNumber >> 48);
```

and add `uint16_t sequenceNumber;` to the native entry struct. Add the matching `public required ushort SequenceNumber { get; init; }` to `NativeUsnJournalEntryData`, `public ushort SequenceNumber { get; }` to `UsnJournalEntry` (assigned from both constructors), and `public ushort SequenceNumber { get; init; }` to `UsnJournalEntryOptions`. Rewrite the `RecordNumber` doc comment so it no longer sells the stripping as the contract.

- [ ] **Step 4: Run to verify it passes**

```bash
dotnet test MFTLib.Tests/MFTLib.Tests.csproj -p:Platform=x64 --filter "TestCategory!=RequiresAdmin"
```

Plus the Linux native build and smoke test, per Task 4's dispatch note; this lane touches native code and must watch its own test go red and green.

- [ ] **Step 5: Commit**

```bash
git add MFTLibNative/usn MFTLib/Journal MFTLib.Tests
git commit -m "feat(journal): carry the USN record sequence number instead of stripping it"
```

---

## Phase 4: The live watch bridge

### Task 6: The index watch pump

**Files:**
- Create: `MFTLib/Index/IndexWatchSource.cs`, `MFTLib/Index/JournalBatch.cs`, `MFTLib/Index/WatchFault.cs`
- Modify: `MFTLib/Index/FileIndex.Watch.cs`, `MFTLib/Index/FileIndexOptions.cs`, `MFTLib/Index/FileIndex.cs`
- Test: `MFTLib.Tests/Index/FileIndexWatchTests.cs`

**Interfaces:**
- Consumes: `FileIndex.ApplyJournalEntries`, `DriveStatus.WatchSupported`, `BlockHeader.UsnJournalId` and `UsnNextUsn`.
- Produces:
  - `public delegate IAsyncEnumerable<JournalBatch> IndexWatchSource(IReadOnlyList<IndexWatchTarget> targets, CancellationToken cancellationToken);`
  - `public sealed record IndexWatchTarget(char DriveLetter, ulong JournalId, long NextUsn);`
  - `public sealed record JournalBatch(char DriveLetter, IReadOnlyList<UsnJournalEntry> Entries, ulong JournalId, long NextUsn);`
  - `public IndexWatchSource? WatchSource { get; init; }` on `FileIndexOptions`
  - `public Task StopWatchingAsync();` on `FileIndex`
  - `public enum WatchFaultKind { Subscriber, Source }`
  - `public sealed record WatchFault(WatchFaultKind Kind, char? DriveLetter, Exception Exception);`
  - `public event Action<WatchFault>? WatchFaulted;` on `FileIndex`

**Design notes:** `StartWatchingAsync` is a no-op stub today (`MFTLib/Index/FileIndex.Watch.cs:29-34`) and its doc comment says so. The index now owns the watch: no consumer runs a broker watch, which is what makes the file-wizard gap report's cursor request unnecessary (decision 14).

The seam takes every drive at once and returns one merged stream because the broker protocol arms a watch for all drives with one frame (decision 5). Each drive's cursor is read out of its own block header, which is where the producer stamped the armed pre-scan cursor, so the replay covers the scan window with no catch-up plumbing (decision 6).

One pump task reads the merged stream and calls `ApplyJournalEntries` per batch. `ApplyJournalEntries` already takes the swap gate, so a rescan and a batch cannot write the same block. `RaiseChanged` can throw when a subscriber throws; the pump must not die on that, so it catches and keeps pumping.

**A fault is announced immediately, not only at stop time.** `WatchFaulted` fires the moment a fault is seen, so a consumer learns about a broken subscriber or a dead broker without having to stop the watch to find out. It fires for two things and nothing else:

- `WatchFaultKind.Subscriber`, once per watch session, on the **first** subscriber exception `RaiseChanged` surfaces. Later subscriber exceptions are counted by nobody and re-raise nothing; one notification per session is a signal, and one per change would be a flood from a handler that throws on everything. `DriveLetter` is the drive whose batch was being delivered.
- `WatchFaultKind.Source`, when the merged stream itself faults, raised immediately before the pump ends. `DriveLetter` is null, because the source delegate merges every drive and a faulted stream does not say which reader died.

`StopWatchingAsync` still rethrows the first fault of either kind, so a caller that never subscribed to the event is not left guessing. The event is the early warning; the stop call is the guarantee. A handler on `WatchFaulted` that throws is swallowed outright, with no second `WatchFaulted` raised for it, because a fault reporter that can itself fault recursively is worse than a lost notification.

Cancellation is not a fault: a stop or a dispose ends the stream through `OperationCanceledException` and raises nothing.

`DisposeAsync` stops the watch before releasing blocks, ignoring a pump fault there, because a dispose that throws would leak the blocks it was disposing.

`FileIndex.Watch.cs` is 152 lines; this task takes it near 300. If it crosses 400, split the pump into `MFTLib/Index/FileIndex.WatchPump.cs` rather than trimming the doc comments.

- [ ] **Step 1: Write the failing tests**

Create `MFTLib.Tests/Index/FileIndexWatchTests.cs`:

```csharp
[TestMethod]
public async Task StartWatchingAsync_PumpsBatchesIntoTheIndexAndRaisesChanged()
{
    using var harness = new WatchHarness();
    var changes = new List<FileChange>();
    harness.Index.Changed += change => changes.Add(change);

    await harness.Index.StartWatchingAsync(TestContext.CancellationTokenSource.Token);
    await harness.PublishAsync(new JournalBatch('T',
        [WatchHarness.Create(recordNumber: 9, "new.txt", parentRecordNumber: 5)], journalId: 7, nextUsn: 100));
    await harness.Index.StopWatchingAsync();

    Assert.AreEqual(1, changes.Count);
    Assert.AreEqual(FileChangeKind.Created, changes[0].Kind);
    Assert.AreEqual("new.txt", changes[0].Entry.Name);
}

[TestMethod]
public async Task StartWatchingAsync_ResumesEachDriveFromItsHeaderCursor()
{
    using var harness = new WatchHarness(journalId: 11, nextUsn: 4242);
    await harness.Index.StartWatchingAsync(TestContext.CancellationTokenSource.Token);

    var target = harness.RequestedTargets.Single();
    Assert.AreEqual('T', target.DriveLetter);
    Assert.AreEqual(11ul, target.JournalId);
    Assert.AreEqual(4242L, target.NextUsn);

    await harness.Index.StopWatchingAsync();
}

[TestMethod]
public async Task StartWatchingAsync_ThrowsWhenADriveSupportsWatchAndNoSourceIsConfigured()
{
    using var harness = new WatchHarness(watchSource: null);
    await Assert.ThrowsExceptionAsync<InvalidOperationException>(
        () => harness.Index.StartWatchingAsync(TestContext.CancellationTokenSource.Token));
}

[TestMethod]
public async Task StopWatchingAsync_SurfacesASubscriberFaultAndKeepsPumpingUntilThen()
{
    using var harness = new WatchHarness();
    harness.Index.Changed += _ => throw new InvalidOperationException("subscriber failed");
    var delivered = 0;
    harness.Index.Changed += _ => delivered++;

    await harness.Index.StartWatchingAsync(TestContext.CancellationTokenSource.Token);
    await harness.PublishAsync(WatchHarness.Batch('T', recordNumber: 9, "one.txt"));
    await harness.PublishAsync(WatchHarness.Batch('T', recordNumber: 10, "two.txt"));

    var fault = await Assert.ThrowsExceptionAsync<InvalidOperationException>(
        () => harness.Index.StopWatchingAsync());
    Assert.AreEqual("subscriber failed", fault.Message);
    Assert.AreEqual(2, delivered);
}

[TestMethod]
public async Task WatchFaulted_AnnouncesTheFirstSubscriberFaultImmediatelyAndOnlyOnce()
{
    using var harness = new WatchHarness();
    var faults = new List<WatchFault>();
    harness.Index.WatchFaulted += fault => faults.Add(fault);
    harness.Index.Changed += _ => throw new InvalidOperationException("subscriber failed");

    await harness.Index.StartWatchingAsync(TestContext.CancellationTokenSource.Token);
    await harness.PublishAsync(WatchHarness.Batch('T', recordNumber: 9, "one.txt"));

    Assert.AreEqual(1, faults.Count);
    Assert.AreEqual(WatchFaultKind.Subscriber, faults[0].Kind);
    Assert.AreEqual('T', faults[0].DriveLetter);
    Assert.AreEqual("subscriber failed", faults[0].Exception.Message);

    await harness.PublishAsync(WatchHarness.Batch('T', recordNumber: 10, "two.txt"));
    Assert.AreEqual(1, faults.Count);

    await Assert.ThrowsExceptionAsync<InvalidOperationException>(() => harness.Index.StopWatchingAsync());
}

[TestMethod]
public async Task WatchFaulted_AnnouncesASourceFaultWithNoDriveLetter()
{
    using var harness = new WatchHarness();
    var faults = new List<WatchFault>();
    harness.Index.WatchFaulted += fault => faults.Add(fault);

    await harness.Index.StartWatchingAsync(TestContext.CancellationTokenSource.Token);
    await harness.FaultSourceAsync(new IOException("the broker died"));

    var fault = await Assert.ThrowsExceptionAsync<IOException>(() => harness.Index.StopWatchingAsync());
    Assert.AreEqual("the broker died", fault.Message);
    Assert.AreEqual(1, faults.Count);
    Assert.AreEqual(WatchFaultKind.Source, faults[0].Kind);
    Assert.IsNull(faults[0].DriveLetter);
}

[TestMethod]
public async Task WatchFaulted_DoesNotFireForAnOrdinaryStop()
{
    using var harness = new WatchHarness();
    var faults = new List<WatchFault>();
    harness.Index.WatchFaulted += fault => faults.Add(fault);

    await harness.Index.StartWatchingAsync(TestContext.CancellationTokenSource.Token);
    await harness.PublishAsync(WatchHarness.Batch('T', recordNumber: 9, "one.txt"));
    await harness.Index.StopWatchingAsync();

    Assert.AreEqual(0, faults.Count);
}

[TestMethod]
public async Task WatchFaulted_SwallowsAThrowingFaultHandlerAndStillRethrowsTheOriginal()
{
    using var harness = new WatchHarness();
    harness.Index.WatchFaulted += _ => throw new NotSupportedException("fault handler failed");
    harness.Index.Changed += _ => throw new InvalidOperationException("subscriber failed");

    await harness.Index.StartWatchingAsync(TestContext.CancellationTokenSource.Token);
    await harness.PublishAsync(WatchHarness.Batch('T', recordNumber: 9, "one.txt"));

    var fault = await Assert.ThrowsExceptionAsync<InvalidOperationException>(
        () => harness.Index.StopWatchingAsync());
    Assert.AreEqual("subscriber failed", fault.Message);
}

[TestMethod]
public async Task DisposeAsync_StopsTheWatchBeforeReleasingBlocks()
{
    var harness = new WatchHarness();
    await harness.Index.StartWatchingAsync(TestContext.CancellationTokenSource.Token);
    await harness.Index.DisposeAsync();

    Assert.IsTrue(harness.SourceCancelled);
    harness.Dispose();
}
```

`WatchHarness` is a new test-support type in `MFTLib.Tests/TestSupport/WatchHarness.cs`: it builds a `SyntheticBlockBuilder.MftShaped()` block on drive `T` with the supplied header cursor, opens a `FileIndex` over it with `ProducerPolicy.Mft` and a producer that hands back that block, records the targets the source was called with, and exposes `PublishAsync` writing into an unbounded `Channel<JournalBatch>` the fake source enumerates, `FaultSourceAsync(Exception)` completing that channel with a fault, and a `Batch(char, uint, string)` factory for a one-entry create. Everything in it runs on Linux, including every `WatchFaulted` test: the faults are injected through the fake source and a throwing subscriber, so none of this needs a broker, a volume, or Windows.

`PublishAsync` must not return until the pump has applied the batch it wrote, or the fault assertions race the pump. Have it await a completion the harness signals from a `Changed` handler it installs first, or expose the pump's applied count and await a change in it. Do not sleep and do not assert on elapsed time.

- [ ] **Step 2: Run to verify they fail**

```bash
dotnet test MFTLib.Tests/MFTLib.Tests.csproj -p:Platform=x64 --filter "FullyQualifiedName~FileIndexWatchTests"
```

Expected: FAIL to compile, there is no `IndexWatchSource` and no `StopWatchingAsync`.

- [ ] **Step 3: Implement**

Create `MFTLib/Index/JournalBatch.cs`:

```csharp
namespace MFTLib.Index;

/// <summary>
///     One drive's journal batch as a watch source delivers it. The cursor travels with the
///     batch rather than being tracked by the index, so a source that resumes, reconnects, or
///     skips ahead after a journal wrap reports where it actually is.
/// </summary>
public sealed record JournalBatch(char DriveLetter, IReadOnlyList<UsnJournalEntry> Entries,
    ulong JournalId, long NextUsn);
```

Create `MFTLib/Index/IndexWatchSource.cs` with the delegate and `IndexWatchTarget`, documenting that the source is called once with every watchable drive and returns one merged stream, and that cancelling the token is how the index stops it.

Add `public IndexWatchSource? WatchSource { get; init; }` to `FileIndexOptions`.

Rewrite `FileIndex.Watch.cs`'s `StartWatchingAsync`:

```csharp
public async Task StartWatchingAsync(CancellationToken cancellationToken)
{
    ObjectDisposedException.ThrowIf(_disposed, this);
    cancellationToken.ThrowIfCancellationRequested();

    var targets = BuildWatchTargets();
    if (targets.Count == 0)
    {
        return;
    }

    var source = _options.WatchSource ?? throw new InvalidOperationException(
        $"{targets.Count} drive(s) support a live watch but " +
        $"{nameof(FileIndexOptions)}.{nameof(FileIndexOptions.WatchSource)} is not set.");

    var cancellation = new CancellationTokenSource();
    if (Interlocked.CompareExchange(ref _watchCancellation, cancellation, null) is not null)
    {
        cancellation.Dispose();
        throw new InvalidOperationException("This index is already watching.");
    }

    _watchPump = PumpAsync(source, targets, cancellation.Token);
    await Task.Yield();
}
```

`BuildWatchTargets` walks the current snapshot under `_stateLock`, takes each drive whose `ProducerKind` is `Mft`, and reads `UsnJournalId` and `UsnNextUsn` from its header. `PumpAsync` is:

```csharp
async Task PumpAsync(IndexWatchSource source, IReadOnlyList<IndexWatchTarget> targets,
    CancellationToken cancellationToken)
{
    Exception? firstFault = null;
    try
    {
        await foreach (var batch in source(targets, cancellationToken).ConfigureAwait(false))
        {
            try
            {
                ApplyJournalEntries(batch.DriveLetter, batch.Entries, batch.JournalId, batch.NextUsn);
            }
            catch (Exception exception) when (exception is not OperationCanceledException)
            {
                // A throwing subscriber must not stop the index from tracking the volume, so
                // this keeps pumping. The first fault is announced now and rethrown from
                // StopWatchingAsync, which is the caller's guarantee if nobody subscribed.
                if (firstFault is null)
                {
                    firstFault = exception;
                    RaiseWatchFaulted(new WatchFault(WatchFaultKind.Subscriber, batch.DriveLetter, exception));
                }
            }
        }
    }
    catch (Exception exception) when (exception is not OperationCanceledException)
    {
        firstFault ??= exception;
        RaiseWatchFaulted(new WatchFault(WatchFaultKind.Source, null, exception));
    }

    if (firstFault is not null)
    {
        ExceptionDispatchInfo.Capture(firstFault).Throw();
    }
}

void RaiseWatchFaulted(WatchFault fault)
{
    try
    {
        WatchFaulted?.Invoke(fault);
    }
    catch (Exception)
    {
        // A fault reporter that can itself raise a fault would recurse. There is nowhere
        // left to send this, and the fault it was reporting is still rethrown from
        // StopWatchingAsync, so the caller does not lose the original.
    }
}
```

`StopWatchingAsync` cancels `_watchCancellation`, awaits `_watchPump` (swallowing `OperationCanceledException` only), disposes and nulls both fields, and returns. `DisposeAsync` calls it first inside a `try`/`catch` that discards everything, then proceeds to its existing block release.

Create `MFTLib/Index/WatchFault.cs` with the enum and the record, documenting per member which of the two situations raises it and that `DriveLetter` is null for a source fault.

- [ ] **Step 4: Run to verify they pass, then the full suite on both platforms**

```bash
dotnet test MFTLib.Tests/MFTLib.Tests.csproj -p:Platform=x64 --filter "TestCategory!=RequiresAdmin"
bash scripts/coverage-linux.sh
```

Expected: PASS on both. The pump is entirely Linux-runnable; nothing here needs Windows.

- [ ] **Step 5: Commit**

```bash
git add MFTLib/Index MFTLib.Tests/Index MFTLib.Tests/TestSupport
git commit -m "feat(index): StartWatchingAsync pumps a watch source into ApplyJournalEntries"
```

---

### Task 7: The broker watch source

**Files:**
- Create: `MFTLib/Broker/Client/BrokerIndexWatchSource.cs`
- Modify: `MFTLib/Broker/Client/BrokerMftBlockProducer.cs`
- Test: `MFTLib.Tests/BrokerIndexWatchSourceTests.cs`

**Interfaces:**
- Consumes: `IndexWatchSource`, `IndexWatchTarget`, `JournalBatch` from Task 6; `JournalBrokerClient.SendStartWatchAsync`, `CreateBatchSource`, `StopLiveWatchAsync`.
- Produces: `public IndexWatchSource CreateWatchSource()` on `BrokerMftBlockProducer`.

**Design notes:** `BrokerMftBlockProducer` already borrows a caller-owned client through `Func<CancellationToken, Task<JournalBrokerClient>> connectAsync` and does not own it; the watch source borrows the same one, so a consumer keeps a single elevated connection across discovery, scan, and watch, which is the deferred-session behavior the git-wizard gap report needs.

The producer path uses `client.ArmScanAndCatchUpAsync` directly rather than a `JournalBrokerScanSession`, and the watch source does the same: `SendStartWatchAsync` with every target's cursor, then `CreateBatchSource()` once, then one reader task per drive feeding an unbounded channel. Going through the session instead would drag in the session state machine and its block-ownership rules for no gain, and the session's client is private anyway (`MFTLib/Broker/Client/JournalBrokerScanSession.cs:16`).

A per-drive reader that faults writes the fault to the channel's completion; the first fault completes the whole stream, because a half-dead multi-drive watch that silently keeps reporting three of four drives is worse than a visible stop. Cancellation completes the channel normally and calls `StopLiveWatchAsync`.

- [ ] **Step 1: Write the failing tests**

Create `MFTLib.Tests/BrokerIndexWatchSourceTests.cs`:

```csharp
[TestMethod]
public async Task WatchSource_StartsOneWatchForEveryTargetAndTagsEachBatch()
{
    await using var harness = new InProcessBlockBrokerHarness();
    var producer = new BrokerMftBlockProducer(harness.ConnectAsync);
    var source = producer.CreateWatchSource();

    var batches = new List<JournalBatch>();
    await foreach (var batch in source(
        [new IndexWatchTarget('C', JournalId: 7, NextUsn: 100)], harness.CancellationToken))
    {
        batches.Add(batch);
        break;
    }

    Assert.AreEqual('C', batches.Single().DriveLetter);
    Assert.AreEqual(7ul, batches.Single().JournalId);
}

[TestMethod]
public async Task WatchSource_CompletesWhenTheTokenIsCancelled()
{
    await using var harness = new InProcessBlockBrokerHarness();
    using var cancellation = new CancellationTokenSource();
    var source = new BrokerMftBlockProducer(harness.ConnectAsync).CreateWatchSource();

    var pump = Task.Run(async () =>
    {
        await foreach (var _ in source([new IndexWatchTarget('C', 7, 100)], cancellation.Token))
        {
        }
    });

    await cancellation.CancelAsync();
    await Assert.ThrowsExceptionAsync<OperationCanceledException>(() => pump);
}

[TestMethod]
public async Task WatchSource_SurfacesAPerDriveWatchFault()
{
    await using var harness = new InProcessBlockBrokerHarness(sourceFails: true);
    var source = new BrokerMftBlockProducer(harness.ConnectAsync).CreateWatchSource();

    await Assert.ThrowsExceptionAsync<InvalidOperationException>(async () =>
    {
        await foreach (var _ in source([new IndexWatchTarget('C', 7, 100)], harness.CancellationToken))
        {
        }
    });
}
```

`InProcessBlockBrokerHarness` (`MFTLib.Tests/TestSupport/InProcessBlockBrokerHarness.cs`) already wires a client and host over a `DuplexStream` pair with fake sources; extend its constructor with a journal-batch fake if it does not already accept one, keeping the existing parameters intact. All three tests run on Linux.

- [ ] **Step 2: Run to verify they fail**

```bash
dotnet test MFTLib.Tests/MFTLib.Tests.csproj -p:Platform=x64 --filter "FullyQualifiedName~BrokerIndexWatchSourceTests"
```

Expected: FAIL to compile, `BrokerMftBlockProducer` has no `CreateWatchSource`.

- [ ] **Step 3: Implement**

Create `MFTLib/Broker/Client/BrokerIndexWatchSource.cs`:

```csharp
using System.Threading.Channels;
using MFTLib.Index;

namespace MFTLib;

/// <summary>
///     Bridges the broker's per-drive live-watch enumerables onto the single merged stream
///     <see cref="IndexWatchSource" /> declares. The client is borrowed, never owned: the same
///     elevated connection that produced the blocks watches them.
/// </summary>
public sealed class BrokerIndexWatchSource(Func<CancellationToken, Task<JournalBrokerClient>> connectAsync)
{
    public IndexWatchSource CreateSource()
    {
        return WatchAsync;
    }

    async IAsyncEnumerable<JournalBatch> WatchAsync(IReadOnlyList<IndexWatchTarget> targets,
        [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(targets);
        var client = await connectAsync(cancellationToken).ConfigureAwait(false);
        var cursorsByDrive = targets.ToDictionary(
            target => JournalBrokerClient.NormalizeDriveLetter(target.DriveLetter.ToString()),
            target => new UsnJournalCursor(target.JournalId, target.NextUsn));

        await client.SendStartWatchAsync(cursorsByDrive, cancellationToken).ConfigureAwait(false);
        var batchSource = client.CreateBatchSource();
        var channel = Channel.CreateUnbounded<JournalBatch>();
        var readers = targets.Select(target =>
            ReadDriveAsync(batchSource, target, channel.Writer, cancellationToken)).ToArray();
        _ = CompleteWhenAllFinishAsync(readers, channel.Writer);

        try
        {
            await foreach (var batch in channel.Reader.ReadAllAsync(cancellationToken).ConfigureAwait(false))
            {
                yield return batch;
            }
        }
        finally
        {
            await client.StopLiveWatchAsync().ConfigureAwait(false);
        }
    }
}
```

`ReadDriveAsync` enumerates `batchSource(normalizedDrive, cursor, cancellationToken)` and writes `new JournalBatch(target.DriveLetter, entries, cursor.JournalId, cursor.NextUsn)` per yielded tuple, using the cursor the tuple carries rather than the one it started from. `CompleteWhenAllFinishAsync` awaits all readers and calls `writer.Complete(exception)` with the first fault, or `writer.Complete()` on clean completion; that is what turns a per-drive fault into a stream fault at the consumer. Use the real names on `UsnJournalCursor` for its journal id and next USN members rather than the placeholders above if they differ.

Add to `BrokerMftBlockProducer`:

```csharp
/// <summary>
///     The live-watch half of this producer. The index calls it once with every drive it
///     wants watched; the broker connection is the same borrowed one the producer used.
/// </summary>
public IndexWatchSource CreateWatchSource()
{
    return new BrokerIndexWatchSource(connectAsync).CreateSource();
}
```

- [ ] **Step 4: Run to verify they pass, then the full suite on both platforms**

```bash
dotnet test MFTLib.Tests/MFTLib.Tests.csproj -p:Platform=x64 --filter "TestCategory!=RequiresAdmin"
bash scripts/coverage-linux.sh
```

Expected: PASS on both. `MFTLib/Broker/Client/JournalBrokerClient.LiveWatch.cs` is 347 lines; if anything here has to be added to it instead of the new file, split it first.

- [ ] **Step 5: Commit**

```bash
git add MFTLib/Broker/Client MFTLib.Tests
git commit -m "feat(broker): a watch source that feeds the index's live watch"
```

---

### Task 7b: Per-drive watch arm and disarm

**Files:**
- Create: `MFTLib/Broker/Client/JournalBrokerClient.LiveWatchDemux.cs`, `MFTLib.Tests/BrokerProtocolTests.DisarmDriveFrame.cs`, `MFTLib.Tests/JournalBrokerHostTests.WatchArming.cs`, `MFTLib.Tests/BrokerPerDriveArmTests.cs`
- Modify: `MFTLib/Broker/Protocol/BrokerFrame.cs`, `MFTLib/Broker/Protocol/BrokerProtocol.cs`, `MFTLib/Broker/Protocol/BrokerProtocol.Write.cs`, `MFTLib/Broker/Host/JournalBrokerHost.cs`, `MFTLib/Broker/Host/JournalBrokerHost.Session.cs`, `MFTLib/Broker/Client/JournalBrokerClient.cs`, `MFTLib/Broker/Client/JournalBrokerClient.LiveWatch.cs`, `MFTLib/Broker/Client/JournalBrokerScanSession.cs`
- Test: `MFTLib.Tests/JournalBrokerHostTests.Watch.cs`, `MFTLib.Tests/JournalBrokerHostTests.WatchRecovery.cs`, `MFTLib.Tests/JournalBrokerClientTests.ConnectionAndWatchFailures.cs`, `MFTLib.Tests/BrokerLiveWatchErrorTests.cs`, `MFTLib.Tests/JournalBrokerScanSessionTests.Watch.cs`, `MFTLib.Tests/JournalBrokerScanSessionTests.Connection.cs`

**Interfaces:**
- Consumes: `BrokerProtocol.ReadFrame`, `BrokerProtocol.WriteStartWatch`, `BrokerProtocol.WriteError`, `BrokerProtocol.WriteJournalBatch`, `BrokerProtocol.WriteEndWatchAck`, `BrokerFrame.RequireDrive`, `BrokerFrame.RequireMessage`, `JournalBrokerHost.ParseScanSpec`, `JournalBrokerHost.WriteFrameAsync`, `JournalBrokerClient.NormalizeDriveLetter`, `JournalBrokerClient.WriteFrameAsync`, `JournalBrokerClient.CreateBatchSource`, `JournalBrokerClient.StopLiveWatchAsync`.
- Produces:
  - `DisarmDrive = 15` on `BrokerFrameKind`
  - `public static BrokerFrame DisarmDrive(string drive)`
  - `public static void WriteDisarmDrive(IBufferWriter<byte> writer, string drive)`
  - `public Task SendStartWatchAsync(` / `    IReadOnlyDictionary<string, UsnJournalCursor> cursorsByDrive,` / `    CancellationToken cancellationToken = default)` on `JournalBrokerClient`, the signature unchanged and the contract replaced: it arms every drive it names into this client's live watch generation, starting that generation on the first call and re-arming an already-armed drive on any later one
  - `internal Task SendStartWatchAsync(` / `    IReadOnlyDictionary<string, UsnJournalCursor> cursorsByDrive,` / `    Action transmissionStarted,` / `    CancellationToken cancellationToken)` on `JournalBrokerClient`, unchanged, and now carrying the same new contract
  - `public Task SendDisarmDriveAsync(string driveLetter, CancellationToken cancellationToken = default)` on `JournalBrokerClient`

Deleted by this task, with every caller and test rewritten in the same commit: `JournalBrokerClient._watchStartGuard` and the `InvalidOperationException("Live watch has already been started for this client")` it raised; `JournalBrokerHost.StartWatchTasks`; `JournalBrokerHost.IsNonRetryableStartupException`; the cached-cursor degrade block inside `JournalBrokerHost.StreamWatchAsync` and the `Warning` frame it wrote; the host's refusal to act on a second `StartWatch` frame; `public event Action<string, string>? WarningReceived` on `JournalBrokerClient`, the demux branch that raised it, and `public event Action<string, string>? WarningReceived` on `JournalBrokerScanSession`, which forwarded it.

**Design notes:**

Today the broker arms one generation of the watch at a time. `SendStartWatchAsync` sends every drive's cursor in one frame, `_watchStartGuard` admits exactly one start per client, and the host ignores a second `StartWatch` outright, so the only way to re-arm one drive is to re-arm all of them. Task 7c's recovery contract needs the opposite, and so do file-wizard and git-wizard, which integrate after this plan and must never see a whole-generation re-arm (decision 15). This task is ordered before the index work for that reason.

**The frame design: `StartWatch` arms a set and may be sent again, plus a new per-drive `DisarmDrive`.** The alternative considered was a pair of new `ArmDrive` and `DisarmDrive` frames leaving `StartWatch` untouched. This shape wins on three counts. The host already turns a drives spec into per-drive requests through `ParseScanSpec`, so a re-arm of one drive is a one-token spec and needs no second parse path. The client already takes an `IReadOnlyDictionary<string, UsnJournalCursor>`, so a re-arm of one drive is a one-entry dictionary through the call that is already there, and both "arm every drive" and "re-arm this drive" reach the wire through one code path. And it adds one frame kind rather than two. What it costs is the host's refusal to act on a duplicate `StartWatch`, which existed because tearing a running generation down mid-write raced its in-flight frames against the new generation's; the replacement is stronger, because arming a drive that is already armed now cancels that drive's task and **awaits it to a stop** before the fresh one starts, so one drive never has two tasks writing frames at once, and the drives that were not named are never touched at all.

`DisarmDrive` rather than `StopWatch` for the new frame: `EndWatch` already names the generation-wide stop and is acknowledged with `EndWatchAck`, and a kind called `StopWatch` beside it would read as the same thing. `DisarmDrive` says what it does and to how many drives, and it pairs with the arming language the rest of this task uses.

**There is no acknowledgement for a disarm, and none is needed.** The host reads request frames in order on one loop, so a `DisarmDrive` followed by a `StartWatch` for the same drive is fully quiesced before it is re-armed. On the client, the disarm completes that drive's channel synchronously before the frame is even written, so its subscriber ends immediately rather than waiting on the wire.

**The client tracks which drives are armed, and the demux drops a batch for a drive that is not.** A batch already in flight when a disarm is sent would otherwise either resurrect the channel the disarm just completed, through `GetOrAddLiveChannel`'s create-on-demand, or land in the channel a re-arm has just created and be applied as if it were fresh. `_armedDrives` beside `_liveChannels` closes both: the demux routes a `JournalBatch` only for an armed drive and logs the drop otherwise. Subscribers keep using `GetOrAddLiveChannel`, which does not consult the set, so an `Error` frame that arrives before its drive's first subscriber still leaves a faulted channel for that late subscriber to find.

**Arming replaces the drive's channel; disarming completes it normally.** A re-armed drive is usually coming back from an `Error` frame that faulted its channel, or from a disarm that completed it; either way the channel it had cannot be read again, so arming drops it and lets the next subscriber create a fresh one. A disarm completes rather than faults, because a deliberate disarm is not a failure and its subscriber should end its `await foreach` rather than throw.

**An `Error` frame disarms its drive.** The host ends a drive's stream when it reports that drive's `Error` (see below), so nothing further will arrive for it until it is armed again. Removing it from the armed set in the same place the channel is faulted keeps the client's picture equal to the host's.

**A stale journal cursor is that drive's failure, and its stream ends.** The host's live-watch degrade path re-queries the cursor, writes a `Warning` saying the replay gap was lost and a rescan is recommended, and then keeps streaming from the current journal position. Under per-drive freezing that is the worst possible outcome: the index would apply batches from the far side of a gap and advance that drive's cursor past USN records nothing will ever replay, diverging from the volume in silence. The whole block goes, and with it the re-query that only existed to decide between two flavours of degradation, and `IsNonRetryableStartupException`, whose only job was to keep genuine failures out of that path. What is left is one rule: any failure ends that drive's stream and is reported as that drive's `Error` frame. `IsJournalCursorException` survives to choose the message, so a stale cursor names the cursor and the rescan while every other failure carries the exception's own message. A `(0, 0)` sentinel start had no cached cursor to be stale and always gets the plain message.

**A `Warning` belongs to the scan path, and one on a live channel is a protocol violation.** `MFTLib/Broker/Host/JournalBrokerHost.Scan.cs:225` writes a `Warning` during an arm-and-scan and `JournalBrokerClient.ScanCollector.cs:80` records it into `BrokerScanResult.Warnings`, which is the whole of that frame kind's contract: a per-drive degradation of a scan, read by the scan's own foreground reader, delivered on the scan result. It never reaches the demux.

`JournalBrokerClient.WarningReceived` is therefore deleted, along with the demux branch that raised it and `JournalBrokerScanSession.WarningReceived`, which forwarded it. Nothing writes a `Warning` on a live channel once the cached-cursor degrade is gone, and an event no producer can raise is dead surface (decision 1 and the global constraint above it).

What the demux does with a `Warning` frame is now stated rather than left to a default: it faults that drive's channel with an `InvalidOperationException` naming the unexpected frame, and disarms the drive as it does for any `Error`. A frame the live channel has no contract for means the host and the client disagree about what the session is doing, and the drive that frame names is the one whose stream can no longer be trusted. Failing it loudly is what turns a protocol disagreement into a per-drive fault the index can act on, instead of a silently ignored frame.

**File sizes.** `MFTLib/Broker/Client/JournalBrokerClient.LiveWatch.cs` is 347 lines and the armed-drive set, the arm and disarm helpers, and `SendDisarmDriveAsync` take it over 400, so it splits by responsibility first: `MFTLib/Broker/Client/JournalBrokerClient.LiveWatchDemux.cs` takes the demux loop and everything that guards `_liveChannelsLock`, and `JournalBrokerClient.LiveWatch.cs` keeps the arm, disarm, and stop lifecycle plus `CreateBatchSource`. `MFTLib.Tests/JournalBrokerHostTests.Watch.cs` is 411 lines and already over the cap; the three tests this task deletes from it bring it back under, and the new host arming tests go in their own file rather than growing it again. `MFTLib.Tests/BrokerProtocolTests.Frames.cs` is 396 lines, so the two `DisarmDrive` frame tests go in a new partial `MFTLib.Tests/BrokerProtocolTests.DisarmDriveFrame.cs` (the class is already `partial` and `AssertWireBytes` lives in `BrokerProtocolTests.cs`, so the new file needs only the two tests).

- [ ] **Step 1: Write the failing tests**

Every test in this step runs on Linux: all of them drive scripted frames over the `DuplexStream` pair or a fake watch source, and none needs Windows, a volume, or a real journal.

Create `MFTLib.Tests/BrokerProtocolTests.DisarmDriveFrame.cs` as a `public partial class BrokerProtocolTests` holding exactly these two tests:

```csharp
[TestMethod]
public void DisarmDriveFrame_RoundTripsItsDrive()
{
    var buffer = new ArrayBufferWriter<byte>();
    BrokerProtocol.WriteDisarmDrive(buffer, "D:\\");

    var frame = BrokerProtocol.ReadFrame(buffer.WrittenSpan, out var consumed);

    Assert.AreEqual(BrokerFrameKind.DisarmDrive, frame.Kind);
    Assert.AreEqual("D:\\", frame.RequireDrive());
    Assert.AreEqual(buffer.WrittenCount, consumed);
}

[TestMethod]
public void WireBytes_Golden_DisarmDriveFrame()
{
    AssertWireBytes(w => BrokerProtocol.WriteDisarmDrive(w, "C"),
        [0x07, 0x00, 0x00, 0x00, 0x0F, 0x02, 0x00, 0x00, 0x00, 0x43, 0x00]);
}
```

Create `MFTLib.Tests/JournalBrokerHostTests.WatchArming.cs` as another partial of `JournalBrokerHostTests`, using that class's existing `CreateHost`, `CreateSectionWriter`, `ReadOneFrameAsync`, `FakeWatch`, and `SampleEntry` helpers. Every one of these uses the `EndWatch` and `EndWatchAck` handshake as its ordering barrier the way `StartWatch_DuplicateWithoutEndWatch_IsIgnored` does today, because the host reads request frames in order and its ack is the only proof that an earlier request was read and acted on. Where a test needs to know that one drive's task actually stopped, the fake watch source completes a `TaskCompletionSource` from its `finally`. Never sleep and never assert on elapsed time.

```csharp
[TestMethod]
public async Task StartWatch_ForADriveThatIsNotArmed_AddsItToTheLiveGenerationAndLeavesTheOthersRunning()
```
Arm `"C:7:100"`, read C's batch, then send `StartWatch` for `"D:1:0"` alone. Assert a batch arrives for `D`, that the fake source was asked for `C` exactly once, so `C` was never restarted, and that `EndWatch` still acks.

```csharp
[TestMethod]
public async Task StartWatch_ForAnArmedDrive_StopsItsTaskAndRestartsItFromTheSuppliedCursor()
```
Arm `"C:7:100"` against a fake source that records every `(drive, since)` it is asked for and completes a per-invocation `TaskCompletionSource` from its `finally`. Read C's batch, send `StartWatch` for `"C:7:500"`, and await the first invocation's stop signal. Assert the recorded cursors are `(7, 100)` then `(7, 500)` in that order, which is what proves the old task was stopped before the fresh one started rather than both running.

```csharp
[TestMethod]
public async Task DisarmDrive_StopsOnlyThatDrivesTaskAndTheOtherDriveKeepsStreaming()
```
Arm `"C:7:100,D:2:200"`. Send `DisarmDrive` for `"C"`, await C's stop signal, and assert D's watch token is still uncancelled and that a further batch written by D's fake source still arrives. Finish with `EndWatch` and its ack.

```csharp
[TestMethod]
public async Task DisarmDrive_ForADriveThatWasNeverArmed_IsIgnoredAndTheSessionKeepsServing()
```
Arm `"C:7:100"`, send `DisarmDrive` for `"D"`, then `EndWatch`. Assert the ack arrives, no `Error` frame was written, and C's batch was delivered.

```csharp
[TestMethod]
public async Task EndWatch_AfterAPerDriveDisarm_StopsEveryRemainingDriveAndStillAcks()
```
Arm `"C:7:100,D:2:200"`, disarm `"C"`, then `EndWatch`. Assert both drives' stop signals completed and the ack arrived exactly once.

Rewrite `MFTLib.Tests/JournalBrokerHostTests.WatchRecovery.cs` to the stale-cursor contract, keeping the file's existing fake-source and frame-reading style:

```csharp
[TestMethod]
public async Task StartWatch_StaleCachedCursor_EndsThatDrivesStreamWithARescanErrorAndTheOtherDriveKeepsStreaming()
```
The rewrite of `StartWatch_CachedCursorThrowsAtStart_OneDriveDegrades_OtherDriveStreamsNormally`, with the same two-drive fake source. Assert exactly one `Error` frame, for `"C"`, whose message contains `"7:100"`, the exception's own text, and `"rescan"`; assert no `Warning` frame at all; assert the only `JournalBatch` frames are `D`'s.

```csharp
[TestMethod]
public async Task StartWatch_StaleCachedCursor_NeverWatchesFromTheCurrentJournalPosition()
```
Single drive whose fake source throws a wrapped-journal exception for the cached cursor and would yield batches for any other cursor. Assert one `Error` frame for that drive and that no `JournalBatch` frame for it ever arrives, then `EndWatch` and read the ack as the barrier proving the host had nothing further to write. The fake `QueryCursor` throws if called, which pins that the re-query is gone rather than merely unused.

```csharp
[TestMethod]
public async Task StartWatch_JournalIdMismatchOnTheCachedCursor_IsAlsoAStaleCursorError()
```
The rewrite of `StartWatch_CachedCursorJournalIdMismatch_DegradesToWarningAndStreams`: the cached cursor names a journal id the volume no longer has. Assert the same rescan-worded `Error` and no `Warning`.

```csharp
[TestMethod]
public async Task StartWatch_StartupFailureThatIsNotAboutTheCursor_CarriesThePlainExceptionMessage()
```
The rewrite of `StartWatch_GenericStartupFailure_WithSuccessfulQueryCursor_EmitsErrorFrame_DoesNotEmitWarning`. The fake source throws `UnauthorizedAccessException("Access is denied")`. Assert the `Error` message equals that text exactly, with no cursor and no rescan wording.

```csharp
[TestMethod]
public async Task StartWatch_ZeroCursorSentinel_StartupFailure_CarriesThePlainExceptionMessage()
```
`"C:0:0"` with a fake source that throws a wrapped-journal exception. Assert the `Error` message equals the exception's text: a sentinel start had no cached cursor to be stale, so it must never claim one was.

`StartWatch_MidStreamThrows_EmitsErrorFrame_SessionContinues` stays as it is; a failure after batches have flowed was already a plain per-drive `Error` and still is. Delete `StartWatch_CachedCursorSameAsCurrentCursor_Fails_EmitsErrorFrame_DoesNotEmitWarning` and `StartWatch_CachedCursorFails_RequeryThrows_EmitsErrorFrame_DoesNotEmitWarning` outright: both pin decisions the re-query made, and there is no re-query. Do not adapt them; the behaviour they pinned is gone on purpose.

Delete three tests from `MFTLib.Tests/JournalBrokerHostTests.Watch.cs`, which also brings that file back under the 400-line cap: `StartWatch_DuplicateWithoutEndWatch_IsIgnored`, whose contract this task replaces and whose replacement lives in `JournalBrokerHostTests.WatchArming.cs`, and `StartWatch_CachedCursorThrowsAtStart_EmitsWarning_AndStreamsFromFreshCursor` and `StartWatch_CachedCursorThrowsAndRequeryBothThrow_EmitsErrorFrameInstead`, both rewritten into `JournalBrokerHostTests.WatchRecovery.cs` above.

Create `MFTLib.Tests/BrokerPerDriveArmTests.cs` for the client half, over the `DuplexStream` pair in the style of `MFTLib.Tests/BrokerLiveWatchErrorTests.cs` and deriving from `BrokerBlockTestBase` for its `CreateBlock` seam:

```csharp
[TestMethod]
public async Task SendStartWatchAsync_ForASecondDrive_ArmsItWithoutStartingASecondDemux()
```
Start with `"C"`, then call again with `"D"` alone. Read both `StartWatch` frames off the server side, write a `JournalBatch` for each drive, and assert both subscribers receive their batch. Read `_demuxTask` by reflection with the same `GetField(..., BindingFlags.NonPublic | BindingFlags.Instance)` pattern `JournalBrokerClientTests.ConnectionAndWatchFailures.cs` already uses for `_demuxCts` (there is no existing `_demuxTask` precedent; the field name is the only change) and assert it is the same instance across both calls, which is what proves one reader still owns the pipe.

```csharp
[TestMethod]
public async Task SendStartWatchAsync_ForAnArmedDrive_ReplacesItsChannelSoTheEarlierSubscriberEnds()
```
Arm `"C"`, subscribe, re-arm `"C"`, and assert the first subscriber's enumeration ends without throwing while a batch written afterwards reaches a subscriber created after the re-arm.

```csharp
[TestMethod]
public async Task SendStartWatchAsync_ForADriveWhoseChannelWasFaulted_GivesTheNextSubscriberALiveChannel()
```
Arm `"C"`, write an `Error` frame for `"C"`, drain the fault, re-arm `"C"`, write a `JournalBatch` for `"C"`, and assert the new subscriber receives it instead of the old fault.

```csharp
[TestMethod]
public async Task SendDisarmDriveAsync_CompletesOnlyThatDrivesChannelAndWritesTheFrame()
```
Arm `"C"` and `"D"`, subscribe to both, disarm `"C"`. Assert the frame read on the server side is `DisarmDrive` carrying `"C"`, that C's enumeration ends normally with no exception, and that a batch written for `"D"` afterwards still reaches D's subscriber.

```csharp
[TestMethod]
public async Task SendDisarmDriveAsync_WithNoWatchRunning_ThrowsInvalidOperationException()
```
No `SendStartWatchAsync` at all. Assert the throw and that nothing was written to the pipe.

```csharp
[TestMethod]
public async Task Demux_DropsAJournalBatchForADriveThatIsNotArmed()
```
Arm `"C"`, disarm `"C"`, write a `JournalBatch` for `"C"` and then one for `"D"` after arming `"D"`. Assert D's subscriber receives its batch, which is the ordering barrier proving the demux read C's frame first, and that a subscriber created for `"C"` afterwards receives nothing before the `EndWatchAck` completes it.

```csharp
[TestMethod]
public async Task Demux_ErrorFrameDisarmsThatDriveSoALaterBatchForItIsDropped()
```
Arm `"C"` and `"D"`, write an `Error` for `"C"`, then a `JournalBatch` for `"C"`, then a `JournalBatch` for `"D"`. Assert C's subscriber sees only the fault and D's sees its batch.

```csharp
[TestMethod]
public async Task StopLiveWatchAsync_ClearsEveryArmedDriveSoTheNextGenerationStartsClean()
```
Arm `"C"` and `"D"`, stop through the `EndWatch` and ack handshake, then arm `"C"` alone and write a `JournalBatch` for `"D"`. Assert D's batch is dropped, because the previous generation's arming must not survive its stop.

Rewrite `SendStartWatchAsync_CalledTwiceWithoutStop_ThrowsInvalidOperationException` in `MFTLib.Tests/JournalBrokerClientTests.ConnectionAndWatchFailures.cs` into `SendStartWatchAsync_CalledTwiceWithoutStop_ArmsTheSecondCallsDrives`, asserting the second call returns normally and writes its own `StartWatch` frame.

Rewrite `WatchDrive_WarningFrame_SubscriberExceptionDoesNotFaultSession` in `MFTLib.Tests/JournalBrokerScanSessionTests.Watch.cs` into `WatchDrive_StaleCursorError_FaultsThatDriveWithTheRescanMessage`, scripting the `Error` frame a stale cursor now produces and asserting `WatchDriveAsync("C")` throws an `InvalidOperationException` whose message names the cursor and the rescan, that no batch was delivered first, and that `session.IsFaulted` stays false because one drive's failure is not the session's.

Delete `WatchDrive_WarningFrame_FiresSessionWarningReceivedEvent` from `MFTLib.Tests/JournalBrokerScanSessionTests.Connection.cs`: it pins the forwarding event, and there is no event to forward. Nothing replaces it at the session level, because the session's remaining contract for an unexpected live frame is the same per-drive fault `WatchDrive_JournalInvalidatedMidWatch_ThrowsInvalidOperation` already pins.

Rewrite the two `Warning` tests in `MFTLib.Tests/BrokerLiveWatchErrorTests.cs` into one that pins the new contract, and delete the other, since a subscriber-isolation test has no subscriber left to isolate:

```csharp
[TestMethod]
public async Task LiveWatch_WarningFrameForDrive_FaultsThatDrivesBatchSourceAndLeavesTheOthers()
```
Arm `"C"` and `"D"`, write a `Warning` frame for `"D"` followed by a `JournalBatch` for `"C"` and an `EndWatchAck`. Assert D's enumeration throws an `InvalidOperationException` whose message names the `Warning` frame and the drive, and that C's enumeration still yields its batch and ends cleanly. A `Warning` has no meaning on a live channel, so the drive it names is the one whose stream can no longer be trusted.

```csharp
[TestMethod]
public async Task LiveWatch_WarningFrameForDrive_DisarmsThatDriveSoALaterBatchForItIsDropped()
```
Arm `"C"`, write a `Warning` for `"C"`, then a `JournalBatch` for `"C"`, then arm `"D"` and write a `JournalBatch` for `"D"`. Assert D's batch arrives, which is the ordering barrier proving C's batch was read and dropped, and that a subscriber created for `"C"` afterwards sees only the fault.

- [ ] **Step 2: Run to verify they fail**

```bash
dotnet test MFTLib.Tests/MFTLib.Tests.csproj -p:Platform=x64 --filter "FullyQualifiedName~BrokerProtocolTests|FullyQualifiedName~WatchArming|FullyQualifiedName~BrokerPerDriveArmTests"
```

Expected: FAIL to compile the whole test project, because `BrokerFrameKind.DisarmDrive`, `BrokerFrame.DisarmDrive`, `BrokerProtocol.WriteDisarmDrive`, and `JournalBrokerClient.SendDisarmDriveAsync` do not exist. That single compile failure is the stated RED reason for `DisarmDriveFrame_RoundTripsItsDrive`, `WireBytes_Golden_DisarmDriveFrame`, `DisarmDrive_StopsOnlyThatDrivesTaskAndTheOtherDriveKeepsStreaming`, `DisarmDrive_ForADriveThatWasNeverArmed_IsIgnoredAndTheSessionKeepsServing`, `EndWatch_AfterAPerDriveDisarm_StopsEveryRemainingDriveAndStillAcks`, `SendDisarmDriveAsync_CompletesOnlyThatDrivesChannelAndWritesTheFrame`, `SendDisarmDriveAsync_WithNoWatchRunning_ThrowsInvalidOperationException`, `Demux_DropsAJournalBatchForADriveThatIsNotArmed`, and `StopLiveWatchAsync_ClearsEveryArmedDriveSoTheNextGenerationStartsClean`.

Then comment out only the calls to the members that do not exist yet and re-run, so each remaining test fails for its own behavioural reason. Record for each:

- `StartWatch_ForADriveThatIsNotArmed_AddsItToTheLiveGenerationAndLeavesTheOthersRunning` and `StartWatch_ForAnArmedDrive_StopsItsTaskAndRestartsItFromTheSuppliedCursor` fail because the host ignores a second `StartWatch` outright, so drive `D` is never watched and drive `C` is never re-armed.
- `StartWatch_StaleCachedCursor_EndsThatDrivesStreamWithARescanErrorAndTheOtherDriveKeepsStreaming`, `StartWatch_StaleCachedCursor_NeverWatchesFromTheCurrentJournalPosition`, and `StartWatch_JournalIdMismatchOnTheCachedCursor_IsAlsoAStaleCursorError` fail because the host writes a `Warning` frame and streams from the fresh cursor instead of writing an `Error` and ending the drive.
- `StartWatch_StartupFailureThatIsNotAboutTheCursor_CarriesThePlainExceptionMessage` and `StartWatch_ZeroCursorSentinel_StartupFailure_CarriesThePlainExceptionMessage` pass today for the wrong reason, through `IsNonRetryableStartupException` and the `since.JournalId != 0` guard rather than through the one rule that replaces them; assert the exact message so they fail once the message is composed by `DescribeWatchFailure` and record that they are green-before-and-after regression pins rather than red ones.
- `SendStartWatchAsync_ForASecondDrive_ArmsItWithoutStartingASecondDemux`, `SendStartWatchAsync_ForAnArmedDrive_ReplacesItsChannelSoTheEarlierSubscriberEnds`, `SendStartWatchAsync_ForADriveWhoseChannelWasFaulted_GivesTheNextSubscriberALiveChannel`, and `SendStartWatchAsync_CalledTwiceWithoutStop_ArmsTheSecondCallsDrives` fail because the second call throws `InvalidOperationException("Live watch has already been started for this client")`.
- `Demux_ErrorFrameDisarmsThatDriveSoALaterBatchForItIsDropped` fails because the demux routes a batch for a faulted drive into its faulted channel and reports no drop.
- `WatchDrive_StaleCursorError_FaultsThatDriveWithTheRescanMessage` fails because the host sends a `Warning` and further batches rather than an `Error`, so the enumeration yields a batch instead of throwing.
- `LiveWatch_WarningFrameForDrive_FaultsThatDrivesBatchSourceAndLeavesTheOthers` and `LiveWatch_WarningFrameForDrive_DisarmsThatDriveSoALaterBatchForItIsDropped` fail because the demux swallows a `Warning` frame after raising `WarningReceived`, so the drive keeps streaming and neither enumeration throws.

- [ ] **Step 3: Implement**

In `MFTLib/Broker/Protocol/BrokerFrame.cs`, add the kind and the factory:

```csharp
    DisarmDrive = 15
```

```csharp
    // Retires one drive from the live watch generation and leaves every other drive
    // running. EndWatch stays the generation-wide stop and keeps its acknowledgement;
    // this one needs none, because the host reads request frames in order and the client
    // completes the drive's channel itself before writing this.
    public static BrokerFrame DisarmDrive(string drive)
    {
        return new BrokerFrame
        {
            Kind = BrokerFrameKind.DisarmDrive,
            Entries = Array.Empty<UsnJournalEntry>(),
            Drive = drive,
            KeepFileNames = Array.Empty<string>()
        };
    }
```

In `MFTLib/Broker/Protocol/BrokerProtocol.cs` add the read case beside `StartWatch`, which has the same single length-prefixed-string payload:

```csharp
            BrokerFrameKind.DisarmDrive => BrokerFrame.DisarmDrive(ReadString(payload, 0, out _)),
```

In `MFTLib/Broker/Protocol/BrokerProtocol.Write.cs`:

```csharp
    public static void WriteDisarmDrive(IBufferWriter<byte> writer, string drive)
    {
        WriteFrameWithString(writer, BrokerFrameKind.DisarmDrive, drive);
    }
```

In `MFTLib/Broker/Host/JournalBrokerHost.Session.cs`, replace `WatchGeneration`'s task list with a per-drive map and rewrite `StartWatch` as an awaiting arm:

```csharp
    // Holds the live watch generation's CTS and one task per armed drive, keyed by the
    // drive token the spec carried. A StartWatch arms the drives it names, a DisarmDrive
    // retires one, and an EndWatch (or session end) tears the whole generation down.
    sealed class WatchGeneration
    {
        public readonly Dictionary<string, DriveWatch> DriveWatches = new(StringComparer.OrdinalIgnoreCase);
        public CancellationTokenSource? Cancellation;
    }

    sealed class DriveWatch(CancellationTokenSource cancellation, Task task)
    {
        public CancellationTokenSource Cancellation { get; } = cancellation;

        public Task Task { get; } = task;
    }
```

```csharp
    async Task ArmWatchDrivesAsync(
        Stream stream,
        SemaphoreSlim writeLock,
        WatchGeneration watch,
        string watchSpec,
        CancellationToken cancellationToken)
    {
        // Arming a drive that is already armed stops its task and awaits it to a stop
        // before the fresh one starts, so one drive never has two tasks writing frames at
        // once. That is what replaces the old refusal to act on a second StartWatch: the
        // refusal existed only because the frame loop had no way to retire a running
        // generation safely, and awaiting one drive to a stop is that way. Drives this
        // spec does not name are not touched.
        watch.Cancellation ??= CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        foreach (var request in ParseScanSpec(watchSpec)) // watch tokens omit the map name
        {
            await DisarmWatchDriveAsync(watch, request.Letter).ConfigureAwait(false);
            var driveCancellation = CancellationTokenSource.CreateLinkedTokenSource(watch.Cancellation.Token);
            watch.DriveWatches[request.Letter] = new DriveWatch(driveCancellation,
                StreamWatchAsync(stream, request.Letter,
                    new UsnJournalCursor(request.JournalId, request.NextUsn), writeLock, driveCancellation.Token));
        }
    }

    // Cancel one drive's task, await its quiescence, and forget it. StreamWatchAsync
    // catches OperationCanceledException internally and always returns normally, so this
    // await cannot fault. A drive that is not armed is not an error: a client may disarm
    // a drive whose stream the host already ended with its Error frame.
    static async Task DisarmWatchDriveAsync(WatchGeneration watch, string drive)
    {
        if (!watch.DriveWatches.Remove(drive, out var driveWatch))
        {
            return;
        }

        await driveWatch.Cancellation.CancelAsync().ConfigureAwait(false);
        await driveWatch.Task.ConfigureAwait(false);
        driveWatch.Cancellation.Dispose();
    }

    static async Task StopWatchGenerationAsync(WatchGeneration watch)
    {
        if (watch.Cancellation == null)
        {
            return;
        }

        await watch.Cancellation.CancelAsync().ConfigureAwait(false);
        await Task.WhenAll(watch.DriveWatches.Values.Select(driveWatch => driveWatch.Task)).ConfigureAwait(false);
        foreach (var driveWatch in watch.DriveWatches.Values)
        {
            driveWatch.Cancellation.Dispose();
        }

        watch.DriveWatches.Clear();
        watch.Cancellation.Dispose();
        watch.Cancellation = null;
    }
```

In `ServeFramesAsync`, the `StartWatch` case awaits the arm and the new case sits beside `EndWatch`:

```csharp
                case BrokerFrameKind.StartWatch:
                    if (frame.Value.DrivesSpec is { } watchSpec)
                    {
                        await ArmWatchDrivesAsync(stream, writeLock, watch, watchSpec, cancellationToken)
                            .ConfigureAwait(false);
                    }

                    break;

                case BrokerFrameKind.DisarmDrive:
                    await DisarmWatchDriveAsync(watch, frame.Value.RequireDrive()).ConfigureAwait(false);
                    break;
```

Delete the old `StartWatch` method with its duplicate-refusal comment, and delete `StartWatchTasks` from `MFTLib/Broker/Host/JournalBrokerHost.cs`; `ArmWatchDrivesAsync` is its replacement and is the only caller of `StreamWatchAsync`. Update `ServeAsync`'s XML doc comment so it describes arming and disarming one drive at a time and says that a per-drive failure ends that drive's stream with an `Error` frame.

In `MFTLib/Broker/Host/JournalBrokerHost.cs`, `StreamWatchAsync` loses its inner degrade block entirely and gains one message helper:

```csharp
    async Task StreamWatchAsync(Stream stream, string drive, UsnJournalCursor since,
        SemaphoreSlim writeLock, CancellationToken cancellationToken)
    {
        if (_watchDrive == null)
        {
            await WriteFrameAsync(stream, writeLock,
                    writer => BrokerProtocol.WriteError(writer, drive, "Broker has no watch source"), cancellationToken)
                .ConfigureAwait(false);
            return;
        }

        var yieldedAny = false;
        try
        {
            // A (0,0) cursor means the caller had no cached cursor for this drive (a warm
            // start with an unknown cursor). Resolve the current cursor and watch from
            // now so the live watch still works; only the pre-launch gap is lost, and
            // there is no cached cursor that could have gone stale.
            var effectiveSince = since.JournalId == 0 ? _queryCursor(drive) : since;

            // No `.WithCancellation(cancellationToken)` here: cancellationToken is
            // already passed as the explicit third argument above, which the
            // production implementation's `[EnumeratorCancellation]` parameter binds
            // directly - adding it again on the same token is redundant.
            await foreach (var (entries, cursor) in _watchDrive(drive, effectiveSince, cancellationToken)
                               .ConfigureAwait(false))
            {
                yieldedAny = true;
                await WriteFrameAsync(stream, writeLock,
                        writer => BrokerProtocol.WriteJournalBatch(writer, drive, cursor, entries), cancellationToken)
                    .ConfigureAwait(false);
            }
        }
        catch (OperationCanceledException)
        {
            // Normal stop: this drive was disarmed, or the whole session was cancelled.
        }
        // Every other failure ends this drive's stream and travels as this drive's Error
        // frame; the other drives keep watching. There is no resume from the current
        // journal position, because a consumer that applied batches from the far side of
        // a lost replay gap would advance past USN records nothing will ever replay and
        // diverge from the volume in silence. A rescan is the only recovery.
        catch (Exception exception)
        {
            await WriteFrameAsync(stream, writeLock,
                    writer => BrokerProtocol.WriteError(writer, drive,
                        DescribeWatchFailure(drive, since, yieldedAny, exception)), CancellationToken.None)
                .ConfigureAwait(false);
        }
    }

    // A cached cursor can fall outside the journal's live window before StartWatch is
    // called: a default 32 MB journal wrapping within minutes on a busy system drive, or
    // the journal being recreated with a new id. That failure names the cursor and the
    // rescan, so a consumer can tell "this drive needs rebuilding" from "this drive hit
    // an access or volume error". A failure after batches have flowed, or from a (0,0)
    // sentinel start that had no cached cursor to be stale, carries its own message.
    static string DescribeWatchFailure(string drive, UsnJournalCursor since, bool yieldedAny, Exception exception)
    {
        if (yieldedAny || since.JournalId == 0 || !IsJournalCursorException(exception))
        {
            return exception.Message;
        }

        var cursorText = FormattableString.Invariant($"{since.JournalId}:{since.NextUsn}");
        return $"Drive {drive} cannot resume its live watch from journal cursor {cursorText}: " +
               $"{exception.Message}. The records between that cursor and the current journal position " +
               "are gone, so this drive needs a rescan before it can be watched again.";
    }
```

Delete `IsNonRetryableStartupException` and its now-absent call site. `IsJournalCursorException` stays exactly as it is and gains this one caller.

Split `MFTLib/Broker/Client/JournalBrokerClient.LiveWatch.cs`. Move to a new `MFTLib/Broker/Client/JournalBrokerClient.LiveWatchDemux.cs`, unchanged except where noted: `_liveChannels`, `_liveChannelsLock`, `_liveEndError`, `_liveEnded`, `DemuxLoopAsync`, `GetOrAddLiveChannel`, `FaultLiveChannel`, and `CompleteAllLiveChannels`. Add beside the lock the two fields it now also guards:

```csharp
    // Which drives this client is currently watching. The demux routes a JournalBatch
    // only for an armed drive: a batch already in flight when a drive was disarmed would
    // otherwise resurrect the channel the disarm completed, or land in the channel a
    // re-arm has just created and be applied as though it were fresh.
    readonly HashSet<string> _armedDrives = new(StringComparer.OrdinalIgnoreCase);

    // True from the first arm until StopLiveWatchAsync. Claimed under _liveChannelsLock
    // in the same critical section that arms the drives, which is what stops two
    // concurrent callers from both starting a demux loop: two readers racing one pipe
    // corrupt frames. It replaces the interlocked start guard, which could only refuse a
    // second start rather than let it arm another drive.
    bool _liveWatchGenerationStarted;
```

Add the armed-drive helpers to the demux file:

```csharp
    // Arming replaces the drive's channel rather than reusing it. A re-armed drive is
    // usually coming back from an Error frame that faulted its channel or from a disarm
    // that completed it, and neither channel can be read again.
    void ArmDriveLocked(string normalizedDrive)
    {
        if (_liveChannels.Remove(normalizedDrive, out var previous))
        {
            previous.Writer.TryComplete();
        }

        _armedDrives.Add(normalizedDrive);
    }

    // Completing normally rather than with an error: a deliberate disarm is not a
    // failure, so this drive's subscriber ends its await-foreach instead of throwing.
    void DisarmDriveLocked(string normalizedDrive)
    {
        _armedDrives.Remove(normalizedDrive);
        if (_liveChannels.Remove(normalizedDrive, out var channel))
        {
            channel.Writer.TryComplete();
        }
    }

    Channel<(UsnJournalEntry[] Entries, UsnJournalCursor Cursor)>? TryGetArmedLiveChannel(string normalizedDrive)
    {
        lock (_liveChannelsLock)
        {
            return _armedDrives.Contains(normalizedDrive) ? GetOrAddLiveChannelLocked(normalizedDrive) : null;
        }
    }
```

Factor `GetOrAddLiveChannel`'s body into `GetOrAddLiveChannelLocked` so both callers share it, leaving `GetOrAddLiveChannel` as the locking wrapper subscribers use. Subscribers deliberately do not consult `_armedDrives`, so an `Error` frame that arrives before its drive's first subscriber still leaves a faulted channel for that late subscriber to find, which is what `LiveWatch_ErrorFrameBeforeSubscribe_LateSubscriberGetsFault` pins.

In `DemuxLoopAsync`, route batches through the armed set and disarm on an error:

```csharp
                    case BrokerFrameKind.JournalBatch:
                    {
                        var batchDrive = NormalizeDriveLetter(value.RequireDrive());
                        if (TryGetArmedLiveChannel(batchDrive) is { } batchChannel)
                        {
                            batchChannel.Writer.TryWrite((value.Entries, value.Cursor));
                        }
                        else
                        {
                            BrokerDiagnostics.Log($"Dropped a JournalBatch frame for unarmed drive {batchDrive}.");
                        }

                        break;
                    }
```

Replace the whole `BrokerFrameKind.Warning` branch, its `BrokerDiagnostics.Log` line, and its subscriber-invocation loop with:

```csharp
                    case BrokerFrameKind.Warning:
                        // A Warning belongs to the arm-and-scan path, which its own foreground
                        // reader collects into BrokerScanResult.Warnings. One on a live channel
                        // means the host and this client disagree about what the session is
                        // doing, so the drive it names is the one whose stream can no longer be
                        // trusted: fault it rather than ignore a frame with no contract here.
                        FaultLiveChannel(NormalizeDriveLetter(value.RequireDrive()),
                            new InvalidOperationException(
                                $"Unexpected {BrokerFrameKind.Warning} frame on the live watch channel for drive " +
                                $"{value.RequireDrive()}: {value.RequireMessage()}"));
                        break;
```

`FaultLiveChannel` gains `_armedDrives.Remove(normalizedDrive);` as its first statement inside the lock, with a comment saying a drive whose stream the host has ended, or whose frames can no longer be trusted, arrives with nothing further until it is armed again. `CompleteAllLiveChannels` gains `_armedDrives.Clear();`, because a generation that has ended arms nothing.

In `MFTLib/Broker/Client/JournalBrokerClient.cs`, delete `public event Action<string, string>? WarningReceived;` and its doc comment. Nothing raises it once the demux branch above stops doing so, and the scan path has never raised it: it collects warnings into `BrokerScanResult.Warnings` instead.

In `MFTLib/Broker/Client/JournalBrokerClient.LiveWatch.cs`, delete `_watchStartGuard` and rewrite the core:

```csharp
    async Task SendStartWatchCoreAsync(
        IReadOnlyDictionary<string, UsnJournalCursor> cursorsByDrive,
        Action? transmissionStarted,
        CancellationToken cancellationToken)
    {
        var normalizedDrives = cursorsByDrive.Keys.Select(NormalizeDriveLetter).ToArray();
        bool startsGeneration;
        lock (_liveChannelsLock)
        {
            startsGeneration = !_liveWatchGenerationStarted;
            _liveWatchGenerationStarted = true;
            foreach (var drive in normalizedDrives)
            {
                ArmDriveLocked(drive);
            }
        }

        // Watch spec tokens omit the map name (three fields): letter:journalId:nextUsn.
        var specTokens = cursorsByDrive.Select(pair => FormattableString.Invariant(
            $"{NormalizeDriveLetter(pair.Key)}:{pair.Value.JournalId}:{pair.Value.NextUsn}"));
        var watchSpec = string.Join(",", specTokens);

        var writeStarted = false;
        try
        {
            await WriteFrameAsync(
                writer => BrokerProtocol.WriteStartWatch(writer, watchSpec),
                () =>
                {
                    writeStarted = true;
                    transmissionStarted?.Invoke();
                }, cancellationToken).ConfigureAwait(false);
        }
        catch when (!writeStarted)
        {
            // Nothing reached the wire, so undo the local arming rather than leave drives
            // armed against a host that was never told about them.
            lock (_liveChannelsLock)
            {
                foreach (var drive in normalizedDrives)
                {
                    DisarmDriveLocked(drive);
                }

                if (startsGeneration)
                {
                    _liveWatchGenerationStarted = false;
                }
            }

            throw;
        }

        if (!startsGeneration)
        {
            // The demux already owns this pipe; the drives this call armed simply joined it.
            return;
        }

        // ... the existing demux CTS and Task.Run block, with its comment, unchanged
    }
```

Add the disarm call beside it:

```csharp
    /// <summary>
    ///     Retire one drive from the live watch generation, leaving every other drive
    ///     streaming. That drive's channel is completed normally, so its subscriber's
    ///     enumeration ends rather than throwing, and any batch for it still in flight is
    ///     dropped by the demux. Arming it again with
    ///     <see cref="SendStartWatchAsync(IReadOnlyDictionary{string,UsnJournalCursor},CancellationToken)" />
    ///     gives it a fresh channel and a fresh subscriber.
    /// </summary>
    public Task SendDisarmDriveAsync(string driveLetter, CancellationToken cancellationToken = default)
    {
        var normalizedDrive = NormalizeDriveLetter(driveLetter);
        lock (_liveChannelsLock)
        {
            if (!_liveWatchGenerationStarted)
            {
                throw new InvalidOperationException(
                    "No live watch is running for this client, so there is nothing to disarm.");
            }

            // Completing the channel before the frame reaches the wire is what makes the
            // disarm observable to this drive's subscriber immediately, rather than after
            // a round trip the protocol does not acknowledge.
            DisarmDriveLocked(normalizedDrive);
        }

        return WriteFrameAsync(writer => BrokerProtocol.WriteDisarmDrive(writer, normalizedDrive), cancellationToken);
    }
```

In `StopLiveWatchAsync`, replace `Interlocked.Exchange(ref _watchStartGuard, 0);` and extend the reset block:

```csharp
        lock (_liveChannelsLock)
        {
            _liveChannels.Clear();
            _armedDrives.Clear();
            _liveEnded = false;
            _liveEndError = null;
            // Release the generation so a rescan can begin a fresh watch on this client.
            _liveWatchGenerationStarted = false;
        }
```

Both entry points keep their signatures exactly as they are, and only the XML doc comment on the public one is rewritten:

```csharp
    public Task SendStartWatchAsync(
        IReadOnlyDictionary<string, UsnJournalCursor> cursorsByDrive,
        CancellationToken cancellationToken = default)
    {
        return SendStartWatchCoreAsync(cursorsByDrive, null, cancellationToken);
    }

    internal Task SendStartWatchAsync(
        IReadOnlyDictionary<string, UsnJournalCursor> cursorsByDrive,
        Action transmissionStarted,
        CancellationToken cancellationToken)
    {
        return SendStartWatchCoreAsync(cursorsByDrive, transmissionStarted, cancellationToken);
    }
```

The new doc comment states the contract: it arms every drive it names, starts the live watch generation on the first call, re-arms an already-armed drive on a later one by replacing that drive's channel, and leaves the drives it does not name alone. It no longer says the call may be made exactly once.

In `MFTLib/Broker/Client/JournalBrokerScanSession.cs`, delete the `public event Action<string, string>? WarningReceived` property-style event and its doc comment. It did nothing but add and remove handlers on the client event that is now gone. A session consumer reads scan-time warnings from `LatestScan`'s `BrokerScanResult.Warnings`, and learns about a per-drive live failure from `WatchDriveAsync` throwing, which is what `WatchDrive_JournalInvalidatedMidWatch_ThrowsInvalidOperation` already pins.

`MFTLib/Broker/Client/BrokerIndexWatchSource.cs`, `MFTLib/Broker/Client/BrokerMftBlockProducer.cs`, and `MFTLibTestExtensions/ScanSessionTestHarness.cs` need no change: the first calls `SendStartWatchAsync` and `StopLiveWatchAsync` with unchanged signatures, and the other two never name the watch surface at all. Task 7c is what teaches the seam to arm and disarm one drive.

- [ ] **Step 4: Run to verify they pass, then the full suite on both platforms**

```bash
dotnet test MFTLib.Tests/MFTLib.Tests.csproj -p:Platform=x64 --filter "TestCategory!=RequiresAdmin"
bash scripts/coverage-linux.sh
aislop scan .
```

Expected: PASS on both. Everything this task touches is Linux-runnable; nothing here needs Windows, a volume, or a real journal.

- [ ] **Step 5: Commit**

```bash
git add MFTLib/Broker MFTLib.Tests
git commit -m "feat(broker): arm and disarm one drive at a time in a live watch"
```

---

### Task 7b2: Per-drive arm epoch on the wire

**Files:**
- Create: `MFTLib.Tests/BrokerProtocolTests.ArmEpochFrames.cs`, `MFTLib.Tests/JournalBrokerHostTests.WatchArmEpoch.cs`, `MFTLib.Tests/BrokerArmEpochDemuxTests.cs`, `MFTLib.Tests/BrokerArmOrderingTests.cs`, `MFTLib.Tests/TestSupport/WatchSpecArmEpochs.cs`, `MFTLib.Tests/JournalBrokerClientTests.LiveWatchChannels.cs`
- Modify: `MFTLib/Broker/Protocol/BrokerFrame.cs`, `MFTLib/Broker/Protocol/BrokerProtocol.cs`, `MFTLib/Broker/Protocol/BrokerProtocol.Write.cs`, `MFTLib/Broker/Host/JournalBrokerHost.cs`, `MFTLib/Broker/Host/JournalBrokerHost.Scan.cs`, `MFTLib/Broker/Host/JournalBrokerHost.Session.cs`, `MFTLib/Broker/Host/JournalBrokerHost.VolumeQuery.cs`, `MFTLib/Broker/Client/JournalBrokerClient.cs`, `MFTLib/Broker/Client/JournalBrokerClient.Transport.cs`, `MFTLib/Broker/Client/JournalBrokerClient.LiveWatch.cs`, `MFTLib/Broker/Client/JournalBrokerClient.LiveWatchDemux.cs`, `docs/broker-integration.md`, `CHANGELOG.md`
- Test: `MFTLib.Tests/BrokerProtocolTests.cs`, `MFTLib.Tests/BrokerProtocolTests.Frames.cs`, `MFTLib.Tests/BrokerProtocolTests.Scan.cs`, `MFTLib.Tests/JournalBrokerHostTests.Watch.cs`, `MFTLib.Tests/JournalBrokerHostTests.WatchArming.cs`, `MFTLib.Tests/JournalBrokerHostTests.WatchRecovery.cs`, `MFTLib.Tests/JournalBrokerHostRealSeamsTests.Operations.cs`, `MFTLib.Tests/BrokerPerDriveArmTests.cs`, `MFTLib.Tests/BrokerLiveWatchErrorTests.cs`, `MFTLib.Tests/BrokerIndexWatchSourceTests.cs`, `MFTLib.Tests/BrokerMftBlockProducerProtocolTests.cs`, `MFTLib.Tests/BrokerMftBlockProducerTests.cs`, `MFTLib.Tests/JournalBrokerClientTests.ConnectionAndWatchFailures.cs`, `MFTLib.Tests/JournalBrokerClientTests.ScanAndWatch.cs`, `MFTLib.Tests/JournalBrokerClientTests.BlockSectionsAndProgress.cs`, `MFTLib.Tests/JournalBrokerScanSessionTests.cs`, `MFTLib.Tests/JournalBrokerScanSessionTests.Connection.cs`, `MFTLib.Tests/JournalBrokerScanSessionTests.CursorReplacement.cs`, `MFTLib.Tests/JournalBrokerScanSessionTests.Rescan.cs`, `MFTLib.Tests/JournalBrokerScanSessionTests.Start.cs`, `MFTLib.Tests/JournalBrokerScanSessionTests.WarmStart.cs`, `MFTLib.Tests/JournalBrokerScanSessionTests.Watch.cs`, `MFTLib.Tests/JournalBrokerScanSessionTests.WatchTransitions.cs`, `MFTLib.Tests/VolumeQueryClientTests.cs`

**Interfaces:**
- Consumes: `BrokerProtocol.ReadFrame`, `BrokerProtocol.WriteStartWatch`, `BrokerProtocol.WriteDisarmDrive`, `BrokerProtocol.WriteEndWatchAck`, `BrokerFrame.RequireDrive`, `BrokerFrame.RequireMessage`, `JournalBrokerHost.ParseScanSpec`, `JournalBrokerHost.WriteFrameAsync`, `JournalBrokerHost.Bare`, `JournalBrokerHost.DescribeWatchFailure`, `JournalBrokerClient.NormalizeDriveLetter`, `JournalBrokerClient.WriteFrameAsync`, `JournalBrokerClient.CreateBatchSource`, and from Task 7b `JournalBrokerClient.SendStartWatchAsync`, `SendDisarmDriveAsync`, `StopLiveWatchAsync`, `ArmDriveLocked`, `DisarmDriveLocked`, `GetOrAddLiveChannelLocked`, `JournalBrokerHost.ArmWatchDrivesAsync`, `DisarmWatchDriveAsync`, `StopWatchGenerationAsync`.
- Produces:
  - `public uint ArmEpoch { get; private init; }` on `BrokerFrame`
  - `public const uint NoArmEpoch = 0;` on `BrokerFrame`
  - `public static BrokerFrame JournalBatch(string drive, uint armEpoch, UsnJournalCursor cursor, UsnJournalEntry[] entries)`
  - `public static BrokerFrame Error(string drive, uint armEpoch, string message)`
  - `public static void WriteJournalBatch(IBufferWriter<byte> writer, string drive, uint armEpoch,` / `    UsnJournalCursor cursor, UsnJournalEntry[] entries)`
  - `public static void WriteError(IBufferWriter<byte> writer, string drive, uint armEpoch, string message)`
  - `internal readonly record struct WatchDriveRequest(string Letter, ulong JournalId, long NextUsn, uint ArmEpoch)` on `JournalBrokerHost`
  - `static IEnumerable<WatchDriveRequest> ParseWatchSpec(string spec)` on `JournalBrokerHost`, with `internal static WatchDriveRequest[] ParseWatchSpecForTest(string spec)` beside `ParseScanSpecForTest`
  - `internal static bool TryNormalizeDriveLetter(string drive, out string normalizedDrive)` on `JournalBrokerClient`, which `NormalizeDriveLetter` now calls and throws on
  - `uint ArmDriveLocked(string normalizedDrive)` on `JournalBrokerClient`, returning the epoch it issued
  - `Channel<(UsnJournalEntry[] Entries, UsnJournalCursor Cursor)>? TryGetArmedLiveChannel(string normalizedDrive, uint armEpoch)` on `JournalBrokerClient`
  - `bool TryFaultArmedLiveChannel(string normalizedDrive, uint armEpoch, Exception error)` on `JournalBrokerClient`

Deleted by this task, with every caller and test rewritten in the same commit: the three-field watch token `letter:journalId:nextUsn` and the use of `ParseScanSpec` to read a `StartWatch` spec at all; `JournalBrokerClient._armedDrives`, the `HashSet<string>` Task 7b added, replaced by a drive-to-epoch map; the one-argument `TryGetArmedLiveChannel(string)`; the unconditional `FaultLiveChannel(string, Exception)` call on the demux's `Error` branch, replaced by the epoch-checked `TryFaultArmedLiveChannel`; and the `docs/broker-integration.md` paragraph stating that the per-drive controls have no generation tag and that a buffered batch can cross a re-arm, which stops being true in this commit.

**Design notes:**

Task 7b's review ruled on this in its Concern 1. `_armedDrives` closes the case where a batch arrives while a drive is disarmed, and cannot close the case where a batch was already on the wire when the drive was disarmed and then re-armed. The brief's justification, "the host reads request frames in order", is a guarantee about the host and it holds: the host really is quiesced for that drive before it re-arms it. It says nothing about the `JournalBatch` and `Error` frames the host had already written, which are travelling the other way and are unaffected by the order in which the host consumes requests. Once the client re-arms the drive, those frames land in the fresh channel indistinguishable from post-re-arm ones. Task 7c's recovery sequence is disarm, rescan, re-arm from a fresh cursor, so a stale pre-rescan batch applied to a freshly rebuilt block would drag that drive's header cursor backwards behind the scan that just rebuilt it. No consumer-side filter can fix it, because the stale frame reaches the new reader through the new channel.

**The wire carries a per-drive arm epoch, and the demux drops any frame that is not tagged with the drive's current one.** The client assigns each drive a monotonically increasing epoch every time it arms that drive; the `StartWatch` spec carries the epoch beside that drive's cursor; the host tags every `JournalBatch` and every per-drive `Error` it writes for that drive with the epoch of the arm that produced it; the demux delivers a frame only when the drive is armed and the frame's epoch equals the epoch it is armed under. A frame from before a disarm, or from an arm a later arm superseded, therefore never enters a channel. There is still no acknowledgement for `DisarmDrive` and no whole-generation barrier, and `EndWatch` and `EndWatchAck` are exactly what they were: the epoch makes the barrier unnecessary rather than replacing one.

**Why an epoch rather than an acknowledgement or an `EndWatch` barrier.** An acknowledged disarm makes every re-arm a round trip and still leaves the client guessing about frames written between the host's read and its reply. Reusing `EndWatch` and `EndWatchAck` as the barrier tears down every drive to recover one, which is the whole-generation re-arm decision 15 exists to delete. The epoch costs four bytes on the two frame kinds that already carry a drive, needs no reply, and answers the question directly at the only place that has to decide: the demux, holding the client's own arming state.

**Epoch width and the zero value.** `uint`. A client that re-armed one drive once a second would need 136 years to exhaust it, and an arm is a session start, a rescan, or a recovery, not a per-batch event, so a 64-bit counter would buy nothing and would double the field on `JournalBatch`, the highest-volume frame on the wire. Zero is never issued: `ArmDriveLocked` pre-increments, so the first arm is epoch 1, and `BrokerFrame.NoArmEpoch` is the zero a scan-path or volume-query frame carries to say that no live arm produced it. That makes the sentinel unforgeable rather than conventional: a frame tagged zero can never match a drive's armed epoch.

**The counter is client-wide and is never reset.** One `uint _lastArmEpoch` on the client, incremented under `_liveChannelsLock` for each drive an arm names, so it is monotone per drive by construction and never reissues a value for the life of the client. Per-drive counters reset by `StopLiveWatchAsync` would be simpler and wrong: that stop has a timeout path that forces the demux down without reading `EndWatchAck`, which is the last frame the host writes for a generation, so the pipe can still hold the previous generation's frames when the next generation's demux starts reading. A never-reset counter makes those unmatchable too, which is how one mechanism covers both the per-drive re-arm and the generation boundary.

**The `StartWatch` spec token gains a fourth field, and watch specs stop sharing the scan parser.** A watch token becomes `letter:journalId:nextUsn:armEpoch`. The frame keeps the single length-prefixed string it has, because the epoch is a property of the drive's arm and belongs beside that drive's cursor rather than in a parallel structured payload that would rewrite every scripted `StartWatch` in the suite for no protocol gain. What does have to change is the parse: `ParseScanSpec`'s fourth field is the section name and its fifth is the scan profile, so a watch token with four fields read through it would arm a drive with a section name of `"4"`. `ParseWatchSpec` is its own function returning its own `WatchDriveRequest`, `ParseScanSpec` keeps the arm-and-scan grammar untouched, and `QueryVolumes` keeps using `ParseScanSpec` with its three-field `letter:0:0` tokens, which is what the comment on `BrokerFrame.QueryVolumes` is corrected to say now that "the same shape as `StartWatch`" is no longer true.

A watch token with fewer than four fields is a protocol violation, not a legacy shape to tolerate: `ParseWatchSpec` throws `InvalidDataException` naming the token, exactly as `ReadFrame` throws for an unknown frame kind. Nothing has ever shipped, so there is no client that sends three fields.

**The host never interprets the epoch.** It parses it, stores it on the drive's arm, and writes it back on the frames that arm produces. It does not compare epochs, does not reject a lower one, and keeps no history of them. Ordering is the client's problem because the client is the assigner, and pushing the comparison into the host would give two places an opinion about which arm is current.

**Arms and disarms reach the wire in the order their epochs were issued.** This is the hazard the epoch creates and it has to be closed in the same commit. Task 7b claims the generation and mutates the armed set under `_liveChannelsLock`, then writes the frame outside it, and the pipe's own `_writeLock` orders the writes independently. Two arms of one drive can therefore issue epochs 5 and 6 and reach the host as 6 then 5, after which the host tags that drive's batches with 5 forever while the client is armed at 6 and drops all of them: a silent permanent stall of exactly the kind the review's Important 1 was about. The same reordering between a disarm and a re-arm leaves the host with no task for a drive the client believes is armed. `_armOrderingGate`, a `SemaphoreSlim(1, 1)` held across the epoch assignment and the frame write in both `SendStartWatchCoreAsync` and the disarm, makes wire order equal issue order. It cannot be `_liveChannelsLock`: that is a plain lock and the write is an await.

`SendDisarmDriveAsync` keeps validating and throwing synchronously before it takes the gate, so the review's Minor 9 is left exactly as it is rather than silently changed: the throw for a client with no live watch is still observable without awaiting, and the test that pins it still passes.

**Two fields Task 7b left outside the lock come inside it.** The review's Minor 4: `_demuxCts` and `_demuxTask` are plain non-volatile references assigned in `SendStartWatchCoreAsync` and read in `StopLiveWatchAsync` and `DisposeAsync`, coordinated only by the generation flag they are not stored beside. This task rewrites those lines for the epoch anyway, so every read, write, and clear of both fields moves under `_liveChannelsLock`, with the awaits on the demux task staying outside it. That is the same rule the rest of the live-watch state already follows, and the new contract explicitly invites concurrent callers.

**A `Warning` on a live channel stays epoch-free.** `Warning` belongs to the arm-and-scan path and carries no drive arm, so there is no epoch to check and none is added to the frame. The demux keeps Task 7b's contract for it exactly: fault the drive it names and disarm that drive, whatever epoch anything else is at. A frame the live channel has no contract for means the host and the client disagree about what the session is doing, and a disagreement is not attributable to one arm.

**The host normalizes a `DisarmDrive` frame's drive the same way it keys an arm.** The review's Minor 7: `ServeFramesAsync` passes `RequireDrive()` straight into a map keyed by the spec token's letter, so a client that spells the drive `C:\` on the disarm is silently ignored. `JournalBrokerClient.NormalizeDriveLetter` gains a `TryNormalizeDriveLetter` sibling that does not throw, and one host helper `NormalizeWatchDrive` runs both the arming letter and the disarming letter through it, falling back to the raw string when it is not a drive letter at all. A string that cannot be normalized can never have been armed either, so it keys itself and the lookup simply misses, which is the same "not armed is not an error" rule the disarm already has. The helper is not moved out of `JournalBrokerClient`: relocating an internal used by a dozen call sites for no behaviour change is churn, and host and client are one assembly speaking one protocol.

**One documentation sentence stops litigating an absence.** The review's Minor 8: `docs/broker-integration.md` says "Live watch warnings have no event surface". Per the repository convention it states the contract that exists instead, which is that scan warnings arrive on `BrokerScanResult.Warnings` and a per-drive live failure surfaces as a throw from `WatchDriveAsync`. The per-drive-control paragraph further down is rewritten for the same reason and for a stronger one: its statement that a buffered batch can be read after a re-arm is false once this commit lands.

**File sizes.** One test file splits. `MFTLib.Tests/JournalBrokerClientTests.ConnectionAndWatchFailures.cs` is 396 lines and its two live-watch scenarios that gain the epoch read (`SendStartWatchAsync_CalledTwiceWithoutStop_ArmsTheSecondCallsDrives` and the `CreateBatchSource_*` group from line 238 on) move to a new partial `MFTLib.Tests/JournalBrokerClientTests.LiveWatchChannels.cs`, leaving the connection and stop tests where they are; the class is already `partial`. `MFTLib.Tests/BrokerProtocolTests.Frames.cs` is 396 lines and shrinks, because every golden whose bytes now carry an arm epoch moves to the new `MFTLib.Tests/BrokerProtocolTests.ArmEpochFrames.cs` partial: `WireBytes_Golden_ErrorFrame`, `WireBytes_Golden_JournalBatchFrame_EmptyEntries`, and the `WriteStartWatch` assertion carved out of `WireBytes_Golden_StringFrames`, which keeps its `ArmAndScan` half. `MFTLib.Tests/BrokerPerDriveArmTests.cs` is 327 lines and `MFTLib.Tests/JournalBrokerHostTests.WatchArming.cs` is 315, so the new client and host epoch tests go in their own files rather than growing either, and the client's split by responsibility, filtering in `BrokerArmEpochDemuxTests.cs` and issue order in `BrokerArmOrderingTests.cs`, is what keeps both near 200. On the production side the largest file this task touches is `MFTLib/Broker/Host/JournalBrokerHost.Scan.cs` at 276 lines, which gains `ParseWatchSpec` and `WatchDriveRequest` and lands near 305.

- [ ] **Step 1: Write the failing tests**

Every test in this step runs on Linux: all of them drive scripted frames over the `DuplexStream` pair or a fake watch source, and none needs Windows, a volume, or a real journal.

Create `MFTLib.Tests/TestSupport/WatchSpecArmEpochs.cs` first, because every scripted live frame in the suite needs it:

```csharp
// The arm epoch a StartWatch frame issued for one drive. A test that scripts the host side
// has to echo the epoch the client just sent, because the host tags the frames it writes for
// a drive with the epoch of the arm that produced them and the demux drops any other.
static class WatchSpecArmEpochs
{
    public static uint ForDrive(BrokerFrame startWatch, string driveLetter)
}
```
It splits `startWatch.DrivesSpec` on commas, finds the token whose first field matches the drive case-insensitively, and returns its fourth field, failing the test with `Assert.Fail` naming the drive and the spec when there is no such token.

Create `MFTLib.Tests/BrokerProtocolTests.ArmEpochFrames.cs` as another `public partial class BrokerProtocolTests`, holding the two moved goldens rewritten for the epoch plus the new round trips:

```csharp
[TestMethod]
public void WireBytes_Golden_StartWatchFrame_WithArmEpoch()
{
    AssertWireBytes(w => BrokerProtocol.WriteStartWatch(w, "C:7:100:1"),
    [
        0x17, 0x00, 0x00, 0x00, // totalLength = 23
        0x02, // kind = StartWatch
        0x12, 0x00, 0x00, 0x00, // drivesSpec length = 18
        0x43, 0x00, 0x3A, 0x00, 0x37, 0x00, 0x3A, 0x00, 0x31, 0x00,
        0x30, 0x00, 0x30, 0x00, 0x3A, 0x00, 0x31, 0x00 // "C:7:100:1"
    ]);
}

[TestMethod]
public void WireBytes_Golden_JournalBatchFrame_WithArmEpoch()
{
    AssertWireBytes(
        w => BrokerProtocol.WriteJournalBatch(w, "C", 3U, new UsnJournalCursor(1UL, 2L),
            Array.Empty<UsnJournalEntry>()),
        [
            0x1F, 0x00, 0x00, 0x00, // totalLength = 31
            0x06, // kind = JournalBatch
            0x02, 0x00, 0x00, 0x00, 0x43, 0x00, // drive "C"
            0x03, 0x00, 0x00, 0x00, // armEpoch = 3
            0x01, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, // journalId = 1
            0x02, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, // nextUsn = 2
            0x00, 0x00, 0x00, 0x00 // entryCount = 0
        ]);
}

[TestMethod]
public void WireBytes_Golden_ErrorFrame_WithArmEpoch()
{
    AssertWireBytes(w => BrokerProtocol.WriteError(w, "C", 42U, "D"),
    [
        0x11, 0x00, 0x00, 0x00, // totalLength = 17
        0x07, // kind = Error
        0x02, 0x00, 0x00, 0x00, 0x43, 0x00, // drive "C"
        0x2A, 0x00, 0x00, 0x00, // armEpoch = 42
        0x02, 0x00, 0x00, 0x00, 0x44, 0x00 // message "D"
    ]);
}

[TestMethod]
public void JournalBatchFrame_RoundTripsItsArmEpoch()
```
Write a batch for `"D:\\"` with epoch `uint.MaxValue` and one entry, read it back, and assert the drive, the epoch, the cursor, the entry, and that `consumed` equals `WrittenCount`. `uint.MaxValue` rather than a small number, so a narrowing read would fail rather than pass by luck.

```csharp
[TestMethod]
public void ErrorFrame_RoundTripsItsArmEpoch()
```
Same for an `Error` carrying a message with a non-ASCII character, asserting the epoch survives beside it.

```csharp
[TestMethod]
public void NoArmEpoch_IsZeroSoAScanFrameCanNeverMatchALiveArm()
```
Assert `BrokerFrame.NoArmEpoch` is `0U`, write an `Error` with it, and assert it round-trips as zero. The other half of this contract, that the client never issues zero, is pinned in `BrokerArmOrderingTests.cs`.

Create `MFTLib.Tests/JournalBrokerHostTests.WatchArmEpoch.cs` as another partial of `JournalBrokerHostTests`, using that class's `CreateHost`, `ReadOneFrameAsync`, `FakeWatch`, and `SampleEntry` helpers and the `EndWatch` and `EndWatchAck` handshake as its ordering barrier:

```csharp
[TestMethod]
public void ParseWatchSpec_ReadsTheArmEpochAsTheFourthField()
```
`ParseWatchSpecForTest("C:7:100:1,D:2:200:9")` yields two requests carrying `("C", 7, 100, 1)` and `("D", 2, 200, 9)`. Assert the letters, cursors, and epochs, which is what pins that the fourth field is the epoch rather than `ParseScanSpec`'s section name.

```csharp
[TestMethod]
public void ParseWatchSpec_RejectsATokenWithNoArmEpoch()
```
`ParseWatchSpecForTest("C:7:100")` throws `InvalidDataException` whose message contains the token. A three-field watch token is the deleted grammar, and this is what makes its deletion loud instead of silent.

```csharp
[TestMethod]
public void ParseWatchSpec_NormalizesTheDriveLetterItArmsUnder()
```
`ParseWatchSpecForTest("c:\\:7:100:1")` is not the shape this parser accepts, so use the shape a foreign client would send: assert `ParseWatchSpecForTest("c:7:100:1")` yields letter `"C"`, which is what makes the arm key and the disarm key one rule.

```csharp
[TestMethod]
public async Task StartWatch_TagsEveryJournalBatchForADriveWithThatArmsEpoch()
```
Arm `"C:7:100:4"`, have the fake source yield two batches, and assert both `JournalBatch` frames carry `ArmEpoch` 4.

```csharp
[TestMethod]
public async Task StartWatch_ReArmingADriveTagsItsBatchesWithTheNewEpochOnly()
```
Arm `"C:7:100:4"`, read C's batch, send `StartWatch` for `"C:7:500:5"`, await the first invocation's stop signal, and assert every batch read after that carries epoch 5 and that no frame carrying 4 appears after the re-arm. That is the host half of the whole contract: the epoch a frame carries is the epoch of the arm that produced it.

```csharp
[TestMethod]
public async Task StartWatch_TagsAPerDriveErrorWithTheArmEpochThatProducedIt()
```
The stale-cursor path from `JournalBrokerHostTests.WatchRecovery.cs`, asserted for its epoch: a fake source that throws a wrapped-journal exception for `"C:7:100:4"` produces exactly one `Error` frame whose `ArmEpoch` is 4 and whose message still names the cursor and the rescan.

```csharp
[TestMethod]
public async Task StartWatch_WithNoWatchSource_TagsItsErrorWithTheArmEpoch()
```
A host built with no watch source, armed with `"C:0:0:2"`. Assert the `Error` frame says `"Broker has no watch source"` and carries epoch 2, which pins that the early return was not left behind when the rest of the path was tagged.

```csharp
[TestMethod]
public async Task DisarmDrive_ForADriveSpelledAsAPath_RetiresTheDriveThatWasArmed()
```
Arm `"C:7:100:1,D:2:200:2"`, send `DisarmDrive` carrying `"C:\\"`, await C's stop signal, and assert D still streams and `EndWatch` still acks. This is the review's Minor 7, settled as a test rather than a comment.

```csharp
[TestMethod]
public async Task ArmAndScan_WritesItsPerDriveErrorWithNoArmEpoch()
```
Drive a per-drive scan failure through `HandleArmAndScanAsync` and assert the `Error` frame's `ArmEpoch` is `BrokerFrame.NoArmEpoch`. A scan frame belongs to no arm, and this is what stops one from ever matching a live channel.

Create `MFTLib.Tests/BrokerArmEpochDemuxTests.cs` for the client's filtering half, over the `DuplexStream` pair in the style of `MFTLib.Tests/BrokerPerDriveArmTests.cs` and deriving from `BrokerBlockTestBase`. Every test reads the `StartWatch` frame off the server side and takes the epoch it scripts from `WatchSpecArmEpochs.ForDrive`, never from a literal:

```csharp
[TestMethod]
public async Task Demux_DeliversAJournalBatchTaggedWithTheDrivesCurrentArmEpoch()
```
Arm `"C"`, read the epoch out of the `StartWatch` frame, write a batch tagged with it, and assert the subscriber receives it. The green baseline the three drop tests below are measured against.

```csharp
[TestMethod]
public async Task Demux_DropsAJournalBatchTaggedWithASupersededArmEpoch()
```
Arm `"C"` and capture epoch one, re-arm `"C"` and capture epoch two, then write a batch tagged with epoch one followed by a batch tagged with epoch two. Assert the subscriber created after the re-arm receives only the second batch, whose arrival is the ordering barrier proving the first was read and dropped. This is the client-side proof of the gap Task 7b's review found, at the layer that closes it.

```csharp
[TestMethod]
public async Task Demux_DropsAnErrorTaggedWithASupersededArmEpochAndKeepsTheFreshChannelStreaming()
```
Arm `"C"`, re-arm `"C"`, then write an `Error` for `"C"` tagged with the first epoch followed by a batch tagged with the second. Assert the subscriber receives the batch and never faults. Without the epoch this is the worst case of all: the failure of a stream the client already retired would fail a drive that is watching perfectly well, which is exactly what a rescan's re-arm would walk into.

```csharp
[TestMethod]
public async Task Demux_DropsAFrameTaggedWithTheNoArmEpochSentinel()
```
Arm `"C"`, write a batch for `"C"` tagged `BrokerFrame.NoArmEpoch`, then a batch tagged with the real epoch. Assert only the second arrives. A scan-path frame can never enter a live channel, and the sentinel is what makes that structural rather than conventional.

```csharp
[TestMethod]
public async Task Demux_DropsAJournalBatchForADriveThatIsNotArmedWhateverEpochItCarries()
```
Arm `"C"`, disarm `"C"`, write a batch for `"C"` tagged with the epoch that arm issued, then arm `"D"` and write a batch for it. Assert D's batch arrives and a subscriber created for `"C"` afterwards receives nothing before the `EndWatchAck` completes it. Task 7b's armed-set rule survives the epoch: an unarmed drive has no current epoch, so nothing can match.

```csharp
[TestMethod]
public async Task Demux_FaultsTheDriveAWarningNamesWhateverEpochIsCurrent()
```
Arm `"C"`, re-arm `"C"`, then write a `Warning` for `"C"`. Assert the current subscriber faults with the `InvalidOperationException` naming the unexpected frame. A `Warning` carries no epoch, so it cannot be filtered by one, and a protocol disagreement about a drive is not attributable to an arm.

Create `MFTLib.Tests/BrokerArmOrderingTests.cs` for epoch issue and wire order, in the same harness style:

```csharp
[TestMethod]
public async Task SendStartWatchAsync_IssuesEpochOneForTheFirstArmSoZeroIsNeverALiveEpoch()
```
Arm `"C"` and assert the spec token is `"C:7:100:1"`. The other half of `NoArmEpoch`'s contract.

```csharp
[TestMethod]
public async Task SendStartWatchAsync_IssuesAHigherEpochForEveryLaterArmOfTheSameDrive()
```
Arm `"C"` three times and assert the three spec epochs are strictly increasing.

```csharp
[TestMethod]
public async Task SendStartWatchAsync_IssuesDistinctEpochsAcrossDrivesInOneCall()
```
Arm `"C"` and `"D"` in one call and assert their tokens carry different epochs, since the counter is client-wide.

```csharp
[TestMethod]
public async Task SendStartWatchAsync_AfterAStopThatTimedOut_NeverReissuesAnEarlierEpoch()
```
Arm `"C"`, shrink `_endWatchAckTimeout` the way `JournalBrokerClientTests.ConnectionAndWatchFailures.cs` already does, run `StopLiveWatchAsync` with no ack scripted so `LastStopTimedOut` is true, then arm `"C"` again. Assert the second epoch is greater than the first. A stop that never read `EndWatchAck` can leave the previous generation's frames unread on the pipe, and a counter reset there would let one of them match the fresh arm.

```csharp
[TestMethod]
public async Task SendStartWatchAsync_ConcurrentArmsOfOneDrive_PutTheHigherEpochLastOnTheWire()
```
Two concurrent `SendStartWatchAsync` calls for `"C"` over `MFTLib.Tests/TestSupport/GateFrameWriteStream.cs`, which holds the first write open while the second call runs. Release the gate, read both `StartWatch` frames, and assert their epochs ascend in wire order. Then write a batch tagged with the first frame's epoch and one tagged with the second's, and assert only the second is delivered, which is the assertion that actually matters: the epoch the host ends up tagging with is the epoch the client is armed under. Assert on the gate, never on elapsed time.

```csharp
[TestMethod]
public async Task SendDisarmDriveAsync_WhileAnArmHoldsTheOrderingGate_ReachesTheWireAfterThatArm()
```
Hold an arm's write open on the gate, start a disarm for the same drive, release, and assert the frames read off the server side are `StartWatch` then `DisarmDrive`. Reordered, the host would retire a drive the client believes is armed and that drive would stream nothing with nothing to say why.

```csharp
[TestMethod]
public async Task SendDisarmDriveAsync_WithNoWatchRunning_StillThrowsWithoutAwaiting()
```
Assert `SendDisarmDriveAsync` throws `InvalidOperationException` from the call itself, without the result being awaited, and that nothing reached the pipe. The ordering gate is an await, and this pins that the validation still runs in front of it, leaving the review's Minor 9 exactly as it was rather than changing it silently.

Rewrite the scripted frames across the rest of the suite by one rule, in this same step, since the signature change breaks the compile everywhere: **a frame scripted for a drive the client armed carries that drive's current arm epoch, read from the `StartWatch` frame the test already takes off the server side through `WatchSpecArmEpochs.ForDrive`; every frame scripted on the scan, catch-up, or volume-query path carries `BrokerFrame.NoArmEpoch`.** The rule is applied per call site, not per file: a `WriteJournalBatch` or `WriteError` call takes the epoch read only when the test it sits in has sent a `StartWatch` for that drive; every other call takes the sentinel, and a file may hold both kinds. Files whose live-watch call sites need the read: `BrokerPerDriveArmTests.cs`, `BrokerLiveWatchErrorTests.cs`, `BrokerIndexWatchSourceTests.cs`, `JournalBrokerClientTests.ConnectionAndWatchFailures.cs`, `JournalBrokerClientTests.ScanAndWatch.cs` (only the two scenarios that call `SendStartWatchAsync`, around lines 209 and 318; the `ArmScanAndCatchUpAsync` scenarios at lines 55, 90, 124, and 151 answer scan requests and take the sentinel), `JournalBrokerScanSessionTests.Watch.cs`, `JournalBrokerScanSessionTests.WatchTransitions.cs`, and `JournalBrokerScanSessionTests.Connection.cs` (only `WatchDrive_HappyPath_YieldsBatchesFromAdvancedCursor` around line 279; the `Error` in `StartWatch_NoDriveArmed_Throws` at line 249 answers the `ArmAndScan` request and takes the sentinel). The rest take the sentinel. Every host test that writes a watch spec gains the fourth field: `JournalBrokerHostTests.Watch.cs`, `JournalBrokerHostTests.WatchArming.cs`, `JournalBrokerHostTests.WatchRecovery.cs`, and `JournalBrokerHostRealSeamsTests.Operations.cs`.

- [ ] **Step 2: Run to verify they fail**

```bash
dotnet test MFTLib.Tests/MFTLib.Tests.csproj -p:Platform=x64 --filter "FullyQualifiedName~ArmEpoch|FullyQualifiedName~BrokerArmOrderingTests"
```

Expected: FAIL to compile the whole test project, because `BrokerFrame.ArmEpoch`, `BrokerFrame.NoArmEpoch`, the epoch parameters on `WriteJournalBatch` and `WriteError`, `ParseWatchSpecForTest`, and `WatchDriveRequest` do not exist. That single compile failure is the stated RED reason for every test named in Step 1.

Then add the members with throwing or trivially wrong bodies and re-run, so each remaining test fails for its own behavioural reason. Record for each:

- The three `WireBytes_Golden_` tests and the two round-trip tests fail because the frames carry no epoch field at all, so the byte arrays differ in length and the read side has nothing to return.
- `ParseWatchSpec_ReadsTheArmEpochAsTheFourthField` and `ParseWatchSpec_RejectsATokenWithNoArmEpoch` fail because there is no watch parser: `ParseScanSpec` accepts three fields happily and reads a fourth as a section name.
- `ParseWatchSpec_NormalizesTheDriveLetterItArmsUnder` fails for the same absence, and would fail on the letter even once the parser exists, because `ParseScanSpec` does not normalize.
- `StartWatch_TagsEveryJournalBatchForADriveWithThatArmsEpoch`, `StartWatch_ReArmingADriveTagsItsBatchesWithTheNewEpochOnly`, `StartWatch_TagsAPerDriveErrorWithTheArmEpochThatProducedIt`, and `StartWatch_WithNoWatchSource_TagsItsErrorWithTheArmEpoch` fail because the host has no epoch to write and tags every frame with the sentinel.
- `DisarmDrive_ForADriveSpelledAsAPath_RetiresTheDriveThatWasArmed` fails because the host looks the raw `"C:\"` up in a map keyed `"C"`, finds nothing, and leaves C streaming.
- `ArmAndScan_WritesItsPerDriveErrorWithNoArmEpoch` is green before and after; record it as a regression pin rather than a red, because it exists to stop the scan path being tagged with a live epoch by a later change.
- `Demux_DeliversAJournalBatchTaggedWithTheDrivesCurrentArmEpoch` and `Demux_DropsAJournalBatchForADriveThatIsNotArmedWhateverEpochItCarries` are green before and after for the same reason: they pin behaviour Task 7b already has, against the filter this task adds in front of it.
- `Demux_DropsAJournalBatchTaggedWithASupersededArmEpoch`, `Demux_DropsAnErrorTaggedWithASupersededArmEpochAndKeepsTheFreshChannelStreaming`, and `Demux_DropsAFrameTaggedWithTheNoArmEpochSentinel` fail because the demux consults only the armed set, so a re-armed drive accepts every frame that names it and the stale one is delivered or the fresh channel is faulted.
- `Demux_FaultsTheDriveAWarningNamesWhateverEpochIsCurrent` is green before and after; record it as the pin that stops the `Warning` branch being folded into the epoch filter by mistake.
- The four `SendStartWatchAsync_Issues` tests fail because no epoch is issued and the spec token has three fields.
- `SendStartWatchAsync_ConcurrentArmsOfOneDrive_PutTheHigherEpochLastOnTheWire` and `SendDisarmDriveAsync_WhileAnArmHoldsTheOrderingGate_ReachesTheWireAfterThatArm` fail because nothing serializes the assignment against the write, so the frames can leave in either order.
- `SendDisarmDriveAsync_WithNoWatchRunning_StillThrowsWithoutAwaiting` is green before and after; it exists so the ordering gate cannot quietly turn the synchronous throw into a faulted task.

- [ ] **Step 3: Implement**

In `MFTLib/Broker/Protocol/BrokerFrame.cs`, add the field and the sentinel, and rewrite the two factories:

```csharp
    // The arm this frame belongs to. A JournalBatch or a per-drive Error written by a live
    // watch carries the epoch of the arm that produced it, and the client's demux delivers
    // it only while that is still the epoch the drive is armed under. Every other frame
    // carries NoArmEpoch.
    public uint ArmEpoch { get; private init; }

    // No arm ever issues zero: the client pre-increments, so the first arm is 1. A scan,
    // catch-up, or volume-query frame therefore cannot collide with a live arm however
    // long a session runs.
    public const uint NoArmEpoch = 0;
```

```csharp
    public static BrokerFrame JournalBatch(string drive, uint armEpoch, UsnJournalCursor cursor,
        UsnJournalEntry[] entries)
    {
        return new BrokerFrame
        {
            Kind = BrokerFrameKind.JournalBatch,
            Entries = entries,
            Drive = drive,
            ArmEpoch = armEpoch,
            Cursor = cursor,
            KeepFileNames = Array.Empty<string>()
        };
    }

    public static BrokerFrame Error(string drive, uint armEpoch, string message)
    {
        return new BrokerFrame
        {
            Kind = BrokerFrameKind.Error,
            Entries = Array.Empty<UsnJournalEntry>(),
            Drive = drive,
            ArmEpoch = armEpoch,
            KeepFileNames = Array.Empty<string>(),
            Message = message
        };
    }
```

Correct the comment on `BrokerFrame.QueryVolumes` in the same file: its `drivesSpec` uses the three-field arm-and-scan token shape with the journal fields unused and no section or profile, which is what lets the host read it with `ParseScanSpec`. It no longer says "the same watch-token shape as `StartWatch`", because a watch token now carries a fourth field this frame has no meaning for.

In `MFTLib/Broker/Protocol/BrokerProtocol.Write.cs`, both writers gain the field immediately after the drive, so the epoch sits with the drive it qualifies rather than behind a variable-length entry list:

```csharp
    public static void WriteError(IBufferWriter<byte> writer, string drive, uint armEpoch, string message)
```
with `// payload: [driveLen int32][driveBytes][armEpoch uint32][messageLen int32][messageBytes]`, `payloadLength = 4 + driveBytes.Length + 4 + 4 + messageBytes.Length`, and a `BinaryPrimitives.WriteUInt32LittleEndian` between the drive and the message length.

```csharp
    public static void WriteJournalBatch(IBufferWriter<byte> writer, string drive, uint armEpoch,
        UsnJournalCursor cursor, UsnJournalEntry[] entries)
```
with `// payload: [driveLen int32][driveBytes][armEpoch uint32][journalId ulong][nextUsn long][entryCount int32][entryBytes]`, `payloadLength` gaining the same 4, and the epoch written between the drive and the cursor.

`WriteWarning` is untouched. In `MFTLib/Broker/Protocol/BrokerProtocol.cs`, `ReadErrorFrame` and `ReadJournalBatchFrame` read the epoch in the same position and pass it to the factories.

In `MFTLib/Broker/Host/JournalBrokerHost.Scan.cs`, add the watch parser and its request beside the scan pair:

```csharp
    internal static WatchDriveRequest[] ParseWatchSpecForTest(string spec) => ParseWatchSpec(spec).ToArray();

    // Watch tokens are comma-joined "letter:journalId:nextUsn:armEpoch". They no longer share
    // ParseScanSpec: that grammar's fourth field is the section name, so a watch token read
    // through it would arm a drive with a section named after its epoch. Nothing has ever
    // shipped a three-field watch token, so a token with fewer than four fields is a protocol
    // violation and says so, rather than defaulting an epoch nothing issued.
    static IEnumerable<WatchDriveRequest> ParseWatchSpec(string spec)
    {
        foreach (var token in spec.Split(',', StringSplitOptions.RemoveEmptyEntries))
        {
            var parts = token.Split(':');
            if (parts.Length != 4)
            {
                throw new InvalidDataException(
                    $"Watch spec token '{token}' must be letter:journalId:nextUsn:armEpoch.");
            }

            yield return new WatchDriveRequest(
                NormalizeWatchDrive(parts[0]),
                ulong.Parse(parts[1], CultureInfo.InvariantCulture),
                long.Parse(parts[2], CultureInfo.InvariantCulture),
                uint.Parse(parts[3], CultureInfo.InvariantCulture));
        }
    }

    // One rule for the key a drive is armed and disarmed under, so a client that spells the
    // drive "C:\" on a DisarmDrive frame retires the drive its "C:7:100:4" token armed. A
    // string that is not a drive letter at all can never have named an armed drive, so it
    // keys itself and the lookup simply misses, which is the same "not armed is not an
    // error" rule the disarm already has.
    static string NormalizeWatchDrive(string drive)
    {
        return JournalBrokerClient.TryNormalizeDriveLetter(drive, out var normalized) ? normalized : drive;
    }

    // One drive's live watch request: the normalized drive letter, the cursor to resume
    // from, and the client-issued epoch every frame this arm produces is tagged with.
    internal readonly record struct WatchDriveRequest(
        string Letter,
        ulong JournalId,
        long NextUsn,
        uint ArmEpoch);
```

`ParseScanSpec`, `ScanDriveRequest`, and `ParseScanSpecForTest` are untouched.

In `MFTLib/Broker/Host/JournalBrokerHost.Session.cs`, arm from the watch parser and normalize the disarm:

```csharp
        watch.Cancellation ??= CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        foreach (var request in ParseWatchSpec(watchSpec))
        {
            await DisarmWatchDriveAsync(watch, request.Letter).ConfigureAwait(false);
            watch.DriveWatches[request.Letter] = new DriveWatch(
                driveCancellationToken => StreamWatchAsync(stream, request, writeLock, driveCancellationToken),
                watch.Cancellation.Token);
        }
```

```csharp
                case BrokerFrameKind.DisarmDrive:
                    await DisarmWatchDriveAsync(watch, NormalizeWatchDrive(frame.Value.RequireDrive()))
                        .ConfigureAwait(false);
                    break;
```

Update `ServeAsync`'s XML doc comment to say that a `StartWatch` token carries the client-issued arm epoch for its drive and that every `JournalBatch` and `Error` the host writes for that drive carries it back, so the client can tell a frame from the current arm from one an earlier arm produced.

In `MFTLib/Broker/Host/JournalBrokerHost.cs`, `StreamWatchAsync` takes the request rather than a drive and a cursor, which keeps it at four parameters, and tags everything it writes:

```csharp
    async Task StreamWatchAsync(Stream stream, WatchDriveRequest request, SemaphoreSlim writeLock,
        CancellationToken cancellationToken)
    {
        var drive = request.Letter;
        var since = new UsnJournalCursor(request.JournalId, request.NextUsn);
        if (_watchDrive == null)
        {
            await WriteFrameAsync(stream, writeLock,
                    writer => BrokerProtocol.WriteError(writer, drive, request.ArmEpoch,
                        "Broker has no watch source"), cancellationToken)
                .ConfigureAwait(false);
            return;
        }
        // ... unchanged, with the two remaining writes tagged:
        //   BrokerProtocol.WriteJournalBatch(writer, drive, request.ArmEpoch, cursor, entries)
        //   BrokerProtocol.WriteError(writer, drive, request.ArmEpoch,
        //       DescribeWatchFailure(drive, since, yieldedAny, exception))
    }
```

The host stores the epoch and writes it back. It never compares one epoch to another, never rejects a lower one, and keeps no history: ordering belongs to the client, which is the only side that issues them.

The scan and volume-query writers pass the sentinel, since no arm produced them: `JournalBrokerHost.Scan.cs`'s per-drive catch and its catch-up `JournalBatch`, and both `Error` writes in `JournalBrokerHost.VolumeQuery.cs`, take `BrokerFrame.NoArmEpoch`.

In `MFTLib/Broker/Client/JournalBrokerClient.Transport.cs`, split the normalizer so a caller that must not throw has one:

```csharp
    // Normalize a drive path ("C:\\", "C:", "C", @"\\.\C:") to the bare single uppercase
    // letter ("C"). The broker spec tokens and frame Drive fields use the bare letter.
    internal static string NormalizeDriveLetter(string drive)
    {
        return TryNormalizeDriveLetter(drive, out var normalized)
            ? normalized
            : throw new ArgumentException($"'{drive}' is not a valid drive letter.", nameof(drive));
    }

    // The same rule without the throw, for a caller normalizing a drive that arrived on the
    // wire: a frame from a client this library did not write can name anything at all, and
    // ending the session over it would be a worse answer than not finding it armed.
    internal static bool TryNormalizeDriveLetter(string drive, out string normalizedDrive)
```
with the existing body moved into the `Try` form, `ArgumentNullException.ThrowIfNull` kept there (a null drive is a caller defect on either path), and `normalizedDrive` set to `string.Empty` on the false return.

In the same file, `DisposeAsync` snapshots `_demuxCts` and `_demuxTask` under `_liveChannelsLock` before it cancels and awaits them, and disposes `_armOrderingGate` beside `_writeLock.Dispose()`.

In `MFTLib/Broker/Client/JournalBrokerClient.cs`, add the gate beside `_writeLock`:

```csharp
    // Held across an arm's epoch assignment and its frame write, so the order two arms of one
    // drive reach the host is the order their epochs were issued. Reordered, the host would
    // tag that drive's frames with an epoch this client has already superseded and the demux
    // would drop every one of them, silently and permanently. It cannot be _liveChannelsLock:
    // that is a plain lock and the frame write is an await.
    readonly SemaphoreSlim _armOrderingGate = new(1, 1);
```

In `MFTLib/Broker/Client/JournalBrokerClient.LiveWatchDemux.cs`, the armed set becomes a drive-to-epoch map and gains the counter:

```csharp
    // Which drives this client is currently watching, and the arm epoch each is armed under.
    // The demux routes a JournalBatch or a per-drive Error only when the frame carries that
    // drive's current epoch, which is what keeps a frame the host wrote before it processed a
    // disarm out of the channel a later arm created.
    readonly Dictionary<string, uint> _armedEpochsByDrive = new(StringComparer.OrdinalIgnoreCase);

    // Client-wide and never reset, so no epoch is ever reissued for the life of this client.
    // Per-drive counters cleared by StopLiveWatchAsync would be simpler and wrong: that stop
    // has a timeout path that forces the demux down without reading EndWatchAck, which is the
    // last frame the host writes for a generation, so the pipe can still hold the previous
    // generation's frames when the next one starts reading.
    uint _lastArmEpoch;
```

```csharp
    // Arming replaces the drive's channel and issues the epoch this arm's frames must carry.
    // Pre-incremented, so the first arm is 1 and BrokerFrame.NoArmEpoch can never match.
    uint ArmDriveLocked(string normalizedDrive)
    {
        if (_liveChannels.Remove(normalizedDrive, out var previous))
        {
            previous.Writer.TryComplete();
        }

        _lastArmEpoch++;
        _armedEpochsByDrive[normalizedDrive] = _lastArmEpoch;
        return _lastArmEpoch;
    }

    void DisarmDriveLocked(string normalizedDrive)
    {
        _armedEpochsByDrive.Remove(normalizedDrive);
        if (_liveChannels.Remove(normalizedDrive, out var channel))
        {
            channel.Writer.TryComplete();
        }
    }

    Channel<(UsnJournalEntry[] Entries, UsnJournalCursor Cursor)>? TryGetArmedLiveChannel(
        string normalizedDrive, uint armEpoch)
    {
        lock (_liveChannelsLock)
        {
            return _armedEpochsByDrive.TryGetValue(normalizedDrive, out var armedEpoch) && armedEpoch == armEpoch
                ? GetOrAddLiveChannelLocked(normalizedDrive)
                : null;
        }
    }

    // An Error from a superseded arm describes a stream this client has already retired.
    // Faulting the fresh channel with it would fail a drive that is watching perfectly well,
    // which is precisely what a rescan's re-arm would walk into, so the same epoch rule that
    // guards a batch guards a failure.
    bool TryFaultArmedLiveChannel(string normalizedDrive, uint armEpoch, Exception error)
    {
        lock (_liveChannelsLock)
        {
            if (!_armedEpochsByDrive.TryGetValue(normalizedDrive, out var armedEpoch) || armedEpoch != armEpoch)
            {
                return false;
            }

            FaultLiveChannelLocked(normalizedDrive, error);
            return true;
        }
    }
```

Factor `FaultLiveChannel`'s body into `FaultLiveChannelLocked`, which keeps the `_armedEpochsByDrive.Remove` and its comment, leaving `FaultLiveChannel` as the locking wrapper the `Warning` branch still uses. `CompleteAllLiveChannels` clears `_armedEpochsByDrive`.

`DemuxLoopAsync` routes both drive-carrying live kinds through the epoch:

```csharp
                    case BrokerFrameKind.JournalBatch:
                    {
                        var batchDrive = NormalizeDriveLetter(value.RequireDrive());
                        if (TryGetArmedLiveChannel(batchDrive, value.ArmEpoch) is { } batchChannel)
                        {
                            batchChannel.Writer.TryWrite((value.Entries, value.Cursor));
                        }
                        else
                        {
                            BrokerDiagnostics.Log(
                                $"Dropped a JournalBatch frame for drive {batchDrive} at arm epoch {value.ArmEpoch}.");
                        }

                        break;
                    }

                    case BrokerFrameKind.Error:
                    {
                        var errorDrive = NormalizeDriveLetter(value.RequireDrive());
                        if (!TryFaultArmedLiveChannel(errorDrive, value.ArmEpoch,
                                new InvalidOperationException(value.RequireMessage())))
                        {
                            BrokerDiagnostics.Log(
                                $"Dropped an Error frame for drive {errorDrive} at arm epoch {value.ArmEpoch}.");
                        }

                        break;
                    }
```

The `Warning` branch is unchanged, including its unconditional `FaultLiveChannel`, and gains one sentence to its comment saying a `Warning` carries no arm epoch because it belongs to the scan path, so the epoch filter has nothing to test and a protocol disagreement about a drive is not attributable to one arm.

In `MFTLib/Broker/Client/JournalBrokerClient.LiveWatch.cs`, the spec is built from the epochs the arm issued, and the whole assignment and write runs under the ordering gate. Task 7b's review fix round reshaped the generation claim and its compensation path (its Important 1); this task does not restate that shape and changes exactly these things inside it: the arm loop captures each drive's issued epoch, the spec token gains the fourth field, the body is wrapped in the gate, and `_demuxCts` and `_demuxTask` are published under `_liveChannelsLock`.

```csharp
        await _armOrderingGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var armEpochsByDrive = new Dictionary<string, uint>(StringComparer.OrdinalIgnoreCase);
            lock (_liveChannelsLock)
            {
                // ... the generation claim exactly as the fix round left it ...
                foreach (var pair in cursorsByDrive)
                {
                    armEpochsByDrive[NormalizeDriveLetter(pair.Key)] = ArmDriveLocked(NormalizeDriveLetter(pair.Key));
                }
            }

            // Watch spec tokens are four fields: letter:journalId:nextUsn:armEpoch.
            var specTokens = cursorsByDrive.Select(pair =>
            {
                var drive = NormalizeDriveLetter(pair.Key);
                return FormattableString.Invariant(
                    $"{drive}:{pair.Value.JournalId}:{pair.Value.NextUsn}:{armEpochsByDrive[drive]}");
            });
            var watchSpec = string.Join(",", specTokens);

            // ... the write, its compensation, and the demux start, unchanged apart from
            // publishing _demuxCts and _demuxTask under _liveChannelsLock ...
        }
        finally
        {
            _armOrderingGate.Release();
        }
```

`SendDisarmDriveAsync` keeps its synchronous validation and moves its mutation and its write inside the gate:

```csharp
    public Task SendDisarmDriveAsync(string driveLetter, CancellationToken cancellationToken = default)
    {
        var normalizedDrive = NormalizeDriveLetter(driveLetter);
        lock (_liveChannelsLock)
        {
            if (!_liveWatchGenerationStarted)
            {
                throw new InvalidOperationException(
                    "No live watch is running for this client, so there is nothing to disarm.");
            }
        }

        return SendDisarmDriveCoreAsync(normalizedDrive, cancellationToken);
    }

    async Task SendDisarmDriveCoreAsync(string normalizedDrive, CancellationToken cancellationToken)
    {
        // The mutation belongs inside the gate with the write. Outside it, a disarm that
        // removed the drive locally and then waited behind a concurrent arm would reach the
        // host after that arm, retiring a drive this client believes is armed.
        await _armOrderingGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            lock (_liveChannelsLock)
            {
                // Completing the channel before the frame reaches the wire is what makes the
                // disarm observable to this drive's subscriber immediately, rather than after
                // a round trip the protocol does not acknowledge.
                DisarmDriveLocked(normalizedDrive);
            }

            await WriteFrameAsync(writer => BrokerProtocol.WriteDisarmDrive(writer, normalizedDrive),
                cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            _armOrderingGate.Release();
        }
    }
```

In `StopLiveWatchAsync`, `_armedDrives.Clear()` becomes `_armedEpochsByDrive.Clear()`, `_lastArmEpoch` is deliberately not reset with a comment saying why, and every read and clear of `_demuxCts` and `_demuxTask` happens under `_liveChannelsLock`:

```csharp
        CancellationTokenSource? demuxCancellation;
        Task? task;
        lock (_liveChannelsLock)
        {
            task = _demuxTask;
            demuxCancellation = _demuxCts;
        }
```

Rewrite the doc comment on the public `SendStartWatchAsync` to add the epoch rule in the caller's terms: each call arms the drives it names under a fresh per-drive epoch, and the client discards anything the broker had already written for an earlier arm of those drives, so a re-arm from a fresh cursor never sees a batch produced before it. The epoch itself is never a parameter and never a return value: a caller that had to pass one could pass a stale one.

`MFTLib/Broker/Client/BrokerIndexWatchSource.cs`, `MFTLib/Broker/Client/JournalBrokerScanSession.Watch.cs`, `MFTLib/Broker/Client/BrokerMftBlockProducer.cs`, and `MFTLibTestExtensions/ScanSessionTestHarness.cs` need no change: no public or internal signature they call moves, and no consumer of a batch source ever sees an epoch.

- [ ] **Step 4: Update the integration guide and the changelog**

In `docs/broker-integration.md`, replace the sentence "Live watch warnings have no event surface" with the contract that exists: scan warnings arrive on `BrokerScanResult.Warnings`, and a per-drive live failure surfaces as a throw from `WatchDriveAsync` for that drive while the others keep streaming.

Replace the per-drive-control paragraph's last two sentences, which say the controls have no generation tag and that a buffered batch can be read after a re-arm. What is true now: each `SendStartWatchAsync` arms every drive it names under a fresh per-drive arm epoch carried in the `StartWatch` spec; the broker tags every `JournalBatch` and per-drive `Error` it writes for a drive with the epoch of the arm that produced it; and the client delivers a frame only while that is still the drive's current epoch, so a batch or a failure produced before a disarm or before a re-arm is discarded rather than delivered into the replacement channel. `DisarmDrive` still has no acknowledgement and needs none.

In `CHANGELOG.md` under `## Unreleased`, add to `### Breaking Changes` that `BrokerProtocol.WriteJournalBatch` and `WriteError` and the matching `BrokerFrame.JournalBatch` and `BrokerFrame.Error` factories take an arm epoch after the drive, that the `JournalBatch` and `Error` wire payloads carry a `uint32` arm epoch after the drive field, and that a `StartWatch` spec token is now four fields, `letter:journalId:nextUsn:armEpoch`. Add to `### Features` `BrokerFrame.ArmEpoch` and `BrokerFrame.NoArmEpoch`, and one line saying the client drops any live frame that is not tagged with the drive's current arm epoch.

- [ ] **Step 5: Run to verify they pass, then the full suite on both platforms**

```bash
dotnet test MFTLib.Tests/MFTLib.Tests.csproj -p:Platform=x64 --filter "TestCategory!=RequiresAdmin"
bash scripts/coverage-linux.sh
aislop scan .
```

Expected: PASS on both. Everything this task touches is Linux-runnable; nothing here needs Windows, a volume, or a real journal.

- [ ] **Step 6: Commit**

```bash
git add MFTLib/Broker MFTLib.Tests docs/broker-integration.md CHANGELOG.md
git commit -m "feat(broker): tag live watch frames with a per-drive arm epoch"
```

---

### Task 7c: Per-drive watch fault isolation

This task supersedes the fault semantics of Tasks 6 and 7: those sections stay as written because their commits are history, but where they say a fault ends the whole watch, this task is the current contract.

**Files:**
- Create: `MFTLib/Index/WatchStreamItem.cs`, `MFTLib/Index/IIndexWatchSource.cs`, `MFTLib/Index/IndexWatchTarget.cs`, `MFTLib/Index/FileIndex.WatchPump.cs`, `MFTLib.Tests/Index/FileIndexWatchFaultTests.cs`, `MFTLib.Tests/BrokerIndexWatchSourceFaultTests.cs`, `MFTLib.Tests/TestSupport/ScriptedWatchBrokerHarness.cs`, `MFTLib.Tests/TestSupport/FakeIndexWatchSource.cs`
- Delete: `MFTLib/Index/IndexWatchSource.cs`
- Modify: `MFTLib/Index/JournalBatch.cs`, `MFTLib/Index/WatchFault.cs`, `MFTLib/Index/DriveStatus.cs`, `MFTLib/Index/FileIndexOptions.cs`, `MFTLib/Index/FileIndex.cs`, `MFTLib/Index/FileIndex.Watch.cs`, `MFTLib/Index/FileIndex.Rescan.cs`, `MFTLib/Broker/Client/BrokerIndexWatchSource.cs`, `MFTLib/Broker/Client/BrokerMftBlockProducer.cs`
- Test: `MFTLib.Tests/TestSupport/WatchHarness.cs`, `MFTLib.Tests/Index/FileIndexWatchPumpTests.cs`, `MFTLib.Tests/Index/FileIndexWatchTests.cs`, `MFTLib.Tests/Index/QueryContractTests.cs`, `MFTLib.Tests/BrokerIndexWatchSourceTests.cs`

**Interfaces:**
- Consumes: `FileIndex.ApplyJournalEntries`, `FileIndex.RaiseChanged`, `FileIndex.TryGetDriveOrdinal`, `FileIndex.RescanAsync`, `BlockHeader.UsnJournalId` and `UsnNextUsn`, and from Tasks 7b and 7b2 `JournalBrokerClient.SendStartWatchAsync` (signature unchanged: the arm epoch is issued inside the client and no caller passes or sees one), `JournalBrokerClient.SendDisarmDriveAsync`, `JournalBrokerClient.CreateBatchSource`, `JournalBrokerClient.StopLiveWatchAsync`, `BrokerProtocol.WriteError` and `BrokerProtocol.WriteJournalBatch` (both now take the arm epoch after the drive), `BrokerProtocol.WriteEndWatchAck`, `BrokerProtocol.WriteDisarmDrive`, and `MFTLib.Tests.TestSupport.WatchSpecArmEpochs.ForDrive`, which is how a scripted frame gets the epoch the client just issued.
- Produces:
  - `public abstract record WatchStreamItem` with a `private protected WatchStreamItem()` constructor, so the hierarchy is closed to the two records below
  - `public sealed record JournalBatch(char DriveLetter, IReadOnlyList<UsnJournalEntry> Entries,` / `    ulong JournalId, long NextUsn) : WatchStreamItem;`
  - `public sealed record DriveWatchFailure(char DriveLetter, Exception Exception) : WatchStreamItem;`
  - `public interface IIndexWatchSource` declaring exactly:
    - `IAsyncEnumerable<WatchStreamItem> StartWatching(` / `    IReadOnlyList<IndexWatchTarget> targets, CancellationToken cancellationToken);`
    - `Task ArmDriveAsync(IndexWatchTarget target, CancellationToken cancellationToken);`
    - `Task DisarmDriveAsync(char driveLetter, CancellationToken cancellationToken);`
  - `public enum WatchFaultKind` gaining a third member `Apply` beside `Subscriber` and `Source`
  - `public string? WatchFailureMessage { get; init; }` on `DriveStatus`
  - `public IIndexWatchSource? WatchSource { get; init; }` on `FileIndexOptions`
  - `public IIndexWatchSource CreateWatchSource()` on `BrokerMftBlockProducer`
  - `public sealed class BrokerIndexWatchSource : IIndexWatchSource`, whose `CreateSource` is deleted because the class is now the seam rather than a factory for a delegate
  - `public Task StopWatchingAsync(CancellationToken cancellationToken)` on `FileIndex`, the token required rather than defaulted, matching `StartWatchingAsync`

The `public delegate IAsyncEnumerable<JournalBatch> IndexWatchSource(...)` Task 6 declared is deleted outright, along with the file that held it. `IndexWatchTarget` keeps its declaration verbatim and moves to its own file. Every other public member of the watch surface keeps the signature Task 6 gave it: `WatchFault`, `FileIndex.StartWatchingAsync`, `FileIndex.ApplyJournalEntries`, and `FileIndex.WatchFaulted`.

**Design notes:**

Task 6's review found three problems that share one root: the watch has no per-drive failure lane, so every failure is either mislabelled or fatal to the whole session. The layers underneath already isolate. `JournalBrokerHost` runs one task per drive and reports one drive's failure as that drive's `Error` frame. `JournalBrokerClient.FaultLiveChannel` completes only that drive's channel and leaves the others reading. Two places throw the isolation away, and this task fixes both.

**The seam becomes an interface, because it now has three operations over shared state.** Task 6 declared the seam as a delegate over one merged stream (decision 5). The recovery contract below has to arm and disarm one drive on a stream that is already running, and a bare delegate cannot express that without a side channel back into the object that owns the readers. `IIndexWatchSource` declares `StartWatching`, `ArmDriveAsync`, and `DisarmDriveAsync`. Decision 5's actual constraint is untouched: the interface is declared in `MFTLib.Index`, `MFTLib.Index` still names nothing under `MFTLib/Broker/`, and `BrokerIndexWatchSource` implements it from the other side exactly as it closed over the delegate before. It stays as fakeable on Linux as the delegate was, since a test fake is one small class instead of one lambda.

One `IIndexWatchSource` runs at most one stream at a time. `StartWatching` throws `InvalidOperationException` if a stream is already live, and `ArmDriveAsync` and `DisarmDriveAsync` throw the same if none is. The index holds the source instance on its watch session, so the rescan reaches the arm and disarm operations of the exact stream the pump is reading.

**The interface declares no disposal member, and none is needed.** The index ends a stream by cancelling the token it passed to `StartWatching`, which ends the drain loop and runs the source's own `finally`; in `BrokerIndexWatchSource` that `finally` is what calls `StopLiveWatchAsync` and releases the borrowed client. Adding an `IAsyncDisposable` an implementer would have nothing extra to do in would be dead surface under decision 1. What `StopWatchingAsync` disposes is the session's own `CancellationTokenSource`, exactly as it disposes `_watchCancellation` today.

**The seam carries a per-drive fault as data.** `StartWatching` yields `WatchStreamItem`, which is either a `JournalBatch` or a `DriveWatchFailure` naming the drive and the exception. In `BrokerIndexWatchSource`, a reader whose per-drive enumerable faults writes a `DriveWatchFailure` for its own drive and returns; it no longer calls `writer.TryComplete(exception)` on the shared merged channel and no longer rethrows. The merged stream faults only when the source cannot start at all, which means `connectAsync` or the first `SendStartWatchAsync` throwing before any reader exists, or on a failure that cannot be attributed to one drive. Ordering holds without extra machinery: the fault item is written into the same unbounded channel the batches use, before its reader returns.

**The merged channel completes when the generation ends, not when its readers happen to run out.** With drives arriving and leaving while the stream is live, "every reader has finished" no longer means the watch is over: a rescan's disarm ends one reader on purpose, and a `DriveWatchFailure` ends one while the pump keeps consuming the rest. So `CompleteWhenAllFinishAsync` goes, and each reader decides for itself. A reader that ends because its per-drive channel completed normally, with no stop having been requested for that drive, means the client completed every channel at once, which is the generation ending, so it completes the merged channel. A reader ending after a requested stop just returns. A reader ending with an exception writes its `DriveWatchFailure` and returns, leaving the merged channel open so the other drives keep flowing and a rescan can still re-arm this one. A stream started with an empty target list has no reader to end it, so `StartWatching` completes the merged channel itself before it drains, which is what keeps `WatchSource_EmptyTargetsCompletesNormally` green; nothing can be armed onto such a stream, because it has already ended by the time an arm could be issued. Cancellation ends the drain loop through `ReadAllAsync`, which throws for the cancelled token and runs the `finally` that stops the watch at the broker.

**A per-drive stop flag is what makes an arm safe, and it is not the same thing as the arm generation.** Per Task 7b, the client's `ArmDriveLocked` runs inside `SendStartWatchAsync` and completes the drive's previous channel *normally*, exactly as `DisarmDriveLocked` does inside `SendDisarmDriveAsync`. Two things follow, and both are load-bearing.

First, the order inside `ArmDriveAsync` is call the client, then await the previous reader, never the other way round. The previous reader can only end once its channel completes, and `SendStartWatchAsync` is the call that completes it, so awaiting the reader first would await a task nothing has yet told to finish and would hang. `DisarmDriveAsync` already has this shape for the same reason, and `ArmDriveAsync` matches it. `ArmDriveAsync` is a general operation on a running stream, not something only ever issued after a disarm, so this holds for a drive whose reader is genuinely still live.

Second, a re-armed drive's previous reader sees a *normal* channel completion, which is the same thing it would see if the whole generation had ended, and it must not read it that way: completing the merged channel there would end every other drive's watch because one drive was re-armed. So the source keeps a per-drive stop-requested set, added to by both `DisarmDriveAsync` and `ArmDriveAsync` before either calls the client, and removed by `ArmDriveAsync` only once the fresh reader is about to start. A reader that ends normally consults that set: in it, return quietly; not in it, complete the merged channel. The arm generation cannot serve this purpose, because the generation the reader captured is by then already stale by construction, which says nothing about whether the whole stream should end.

**The wire epoch and the source's arm generation drop different stale items, and neither subsumes the other.** Task 7b2 puts a per-drive arm epoch on the wire, so a `JournalBatch` or `Error` the broker had already written when a drive was disarmed is dropped by the client's demux and never reaches any channel or any reader. That closes the gap this task's recovery contract cannot close by itself, because such a frame arrives through the channel a re-arm has just created. It does not close the one this task owns. The merged channel buffers between the readers and the pump, so at the moment a drive is disarmed some of that drive's items may already be sitting in it, delivered perfectly legitimately under the epoch that was current when the demux saw them, and the old reader will drain whatever its per-drive channel still holds before that channel's completion ends it. Applying one of those to the block a rescan has just swapped in would drag that drive's header cursor backwards behind a fresh scan just as surely.

So `BrokerIndexWatchSource` keeps one integer per drive, incremented by both `DisarmDriveAsync` and `ArmDriveAsync`. Each reader captures the value it was started with and tags what it writes; the loop inside `StartWatching` that drains the merged channel drops any item whose tag is not that drive's current value. The two filters divide cleanly by where the stale item is: the epoch drops what is still on the pipe, and the arm generation drops what is already past it. The filter runs in the source's own drain loop, which is the one place that reads the channel, so it needs no additional synchronization beyond the counter itself. A `DriveWatchFailure` is tagged and filtered like any other item, so a superseded reader's parting fault is dropped too: the drive it describes has since been disarmed or re-armed, and the failure no longer describes anything true about it.

**Duplicate targets are rejected by name.** `StartWatching` builds a cursor dictionary keyed by drive, and two targets for one drive would otherwise surface as `ToDictionary`'s own duplicate-key `ArgumentException`, which names neither the parameter nor the drive. `StartWatching` checks for a repeated drive letter first and throws an `ArgumentException` naming `targets` and the drive, before it connects. `BuildWatchTargets` cannot produce a duplicate, since it emits one target per drive block, but the interface is public and a consumer's own source or a re-arm loop can, so the check belongs at the boundary rather than in a comment.

**The pump isolates per drive.** `WatchFaultKind` gains `Apply`, meaning the index could not apply a batch, the block is unchanged, and the cursor was not advanced. The pump then handles four cases:

- A `DriveWatchFailure` item: raise `WatchFaulted` with `WatchFaultKind.Source` and the drive letter set, record the failure on that drive, and drop that drive for the rest of the session.
- An exception from the index-side apply, meaning everything `ApplyJournalEntries` can throw before subscriber delivery (disposed, null entries, unknown drive, offline drive, enumeration drive, mutator failure): raise `WatchFaultKind.Apply` with the drive letter set, record the failure on that drive, and drop that drive for the rest of the session.
- An exception from `RaiseChanged`: unchanged from Task 6. `WatchFaultKind.Subscriber`, once per session, drive letter set, the drive is not dropped and the pump keeps going. The mutation and the cursor are already durable when a subscriber runs, so a broken handler is not the index's problem.
- An exception escaping the merged stream itself: `WatchFaultKind.Source` with `DriveLetter` null, and the pump ends. No drive gets a failure message, because an unattributable fault names no drive.

**Neither inner catch swallows this session's own cancellation.** Task 6's review left a minor open here: the pump's inner catch was a bare `catch (Exception)`, so an `OperationCanceledException` raised by an ordinary stop reached it and was announced as a fault. Both of the inner catches this task creates carry the filter `exception is not OperationCanceledException || !cancellationToken.IsCancellationRequested`, the same filter `ReadDriveAsync` uses on the other side of the seam. A cancellation that belongs to the session escapes to the pump's own `OperationCanceledException` handler, where a stop is not a fault and drops no drive; a cancellation from anywhere else is a genuine failure and is treated as one.

Dropping a drive means the pump never calls apply for its later batches, so its block header cursor never moves past the batch that failed. That is the whole point: the review's Important 1 showed that continuing over a gap advances the cursor past USN records nothing will ever replay, and the drive then diverges silently. A dropped drive stops at the last cursor it successfully applied, so a later rescan or re-arm resumes from a cursor that is still true.

**When every watched drive has faulted, the pump ends.** It breaks out of the `await foreach`, which disposes the enumerator and runs the broker source's `finally`, ending the watch at the broker. The session object stays in place, so `StopWatchingAsync` still finds it and still rethrows the first fault of any kind, exactly as it does after a source completes naturally today. "Every drive" is recomputed from the failure dictionary each time a drive is dropped rather than counted down, so a drive the rescan re-arms and clears counts as live again without a second structure to keep in step.

**Distinguishing an apply failure from a subscriber failure without duplicating the method.** `ApplyJournalEntries` splits into an internal `ApplyJournalEntriesCore` that does everything up to and including releasing `_swapGate` and returns the change list, and the public `ApplyJournalEntries`, which calls the core and then `RaiseChanged`. The public contract is unchanged for a direct caller. The pump calls the core inside one `try` and `RaiseChanged` inside another, which is what makes the two fault kinds distinguishable.

**Where the per-drive watch state lives.** `FileIndex` gains `readonly Dictionary<ushort, string> _watchFailureMessagesByOrdinal = [];`, read and written under `_stateLock`, in exactly the shape Task 3 used for `_mftProducerFailureMessagesByOrdinal`. `DescribeOnlineDrive` reads it with `GetValueOrDefault` and passes the value into `DescribeDrive`, which sets `DriveStatus.WatchFailureMessage`. The dictionary is also the pump's own predicate for a dropped drive: a drive is dropped exactly when its ordinal has an entry, so there is no second structure to drift out of step and an exception with an empty message still counts as a fault. `DriveState` stays `Ready` for such a drive, because its block is valid and every query still answers from it; only the tail of the journal is missing. A drive with no ordinal, meaning a configured drive that is offline and has no block, gets no message, because its `DriveStatus` comes from `_blocklessDriveStatuses` and not from the ordinal dictionary; it is still announced and still dropped.

**The one drive the ordinal dictionary cannot speak for, and what tracks it instead.** A drive letter `TryGetDriveOrdinal` cannot resolve has no key to record under, so a second fault for it would otherwise be announced a second time. `PumpAsync` owns a local `HashSet<char> droppedDriveLettersWithoutOrdinal` and threads it into `DropDrive`, `IsDriveWatchFaulted`, and `AnyWatchedDriveRemains` as an explicit parameter; each of those consults the ordinal dictionary when there is an ordinal and this set when there is not. The set is session-local on purpose, which is the same rule arming already follows: a fresh session starts with nothing dropped. It is never the dictionary's replacement, only its fallback for a letter the index cannot key.

**Clearing is arming.** One rule: arming a drive clears its watch failure entry. `StartWatchingAsync` clears the entry for every drive it arms, and the rescan clears the rescanned drive's entry immediately before it re-arms that one drive. A message about a watch failure on a block that has just been replaced is no longer true, and the drive must be applying again the moment its fresh batches can arrive.

**The session object.** The review's finding 2 showed `_watchPump` published outside the lock that claims `_watchCancellation`, so a stop racing a start can orphan a live pump. Both fields collapse into one private `WatchSession` holding the linked `CancellationTokenSource`, the pump `Task<Exception?>`, the caller token the session was linked to, and the `IIndexWatchSource` the pump is reading, which is how `RescanAsync` reaches the arm and disarm operations of the exact stream in flight. A single `WatchSession? _watchSession` field is claimed, read, and cleared under `_stateLock`. `PumpAsync`'s first statement is `await Task.Yield()`, which is what makes it safe to launch the pump inside the lock: the call returns to `StartWatchingAsync` before any source code runs, so the lock is never held across an await and the broker source's connect never runs under it.

**A stop is bounded by its caller's token.** Task 6's review left a second minor open: `StopWatchingAsync` awaited the pump with nothing to bound it, so a source that ignores its cancellation wedges the stop and, through `DisposeAsync`, the whole index. It gains a required `CancellationToken` parameter, matching `StartWatchingAsync`, and awaits `session.Pump.WaitAsync(cancellationToken)`. A hard-coded timeout is deliberately not the answer: the right wait depends on the caller, and a constant would be a wall-clock number baked into production code. Cancelling the stop abandons the wait and rethrows, and it leaves the session in place rather than clearing it, so a later stop or `DisposeAsync` can still reclaim it; clearing a session whose pump is still running is exactly the orphan the field collapse above exists to prevent. `DisposeAsync` passes `CancellationToken.None`, because disposal has no token of its own and must not abandon a pump it is about to unmap blocks underneath.

**The caller's token ends the watch.** The review's finding 3: `StartWatchingAsync` builds the session source with `CancellationTokenSource.CreateLinkedTokenSource(cancellationToken)`, so cancelling the token passed to the start call ends the watch. Cancellation stays not a fault: the pump's `OperationCanceledException` filter tests the session token, which a linked parent cancels, so nothing is raised and nothing is rethrown. The doc comment says the token both guards the start and ends the session, and that `StopWatchingAsync` or `DisposeAsync` reclaims the session afterwards, since a cancelled token ends the pump but does not clear the field.

**The recovery contract.** `RescanAsync(driveLetter)` while a watch is running touches exactly one drive. In order:

1. Disarm that drive through the session's source, before taking `_swapGate`. Its batches stop, its reader ends, and its block header cursor is frozen wherever the last successfully applied batch left it. The other drives never stop, never lose a batch, and never notice.
2. Take `_swapGate` and perform the swap exactly as today.
3. Release the gate, then clear that drive's watch failure entry under `_stateLock`.
4. Re-arm that one drive through the same source, with an `IndexWatchTarget` built from the fresh block header's `UsnJournalId` and `UsnNextUsn`.

The order of 3 and 4 is deliberate. Clearing after arming would let a fresh batch arrive while the drive still looked dropped and be discarded, losing it for good; clearing before arming is safe because the drive is disarmed at the source, so no batch for it can exist in between. The disarm must precede the gate rather than run under it, because `ApplyJournalEntriesCore` takes `_swapGate` synchronously on the pump thread. Nothing here deadlocks: the disarm awaits a broker-side reader, which never takes `_swapGate`, and the merged channel is unbounded, so a pump blocked on the gate never blocks a reader's write.

Nothing produced before the re-arm reaches the freshly swapped block, and it takes both filters to say that. A batch still travelling from the broker when the disarm was written is dropped by the client's demux, because it carries the epoch of an arm the re-arm superseded (Task 7b2). A batch already queued on the merged channel when the disarm ran is dropped by the source's arm-generation filter, because the drive's generation moved on while it sat there.

**The index's expectation of a stale journal cursor.** Task 7b made a cursor the broker cannot resume from that drive's `Error` frame, ending that drive's stream, instead of a `Warning` followed by a resume from the current journal position. Everything downstream of that already follows from the rules above with no extra code: the client faults that drive's channel, its reader writes a `DriveWatchFailure`, the pump raises `WatchFaultKind.Source` with the drive letter, records the message on that drive, and freezes its cursor, and `RescanAsync` on that drive is the recovery. That is the whole reason the resume was deleted: a resume would have advanced the drive's cursor past USN records nothing will ever replay, and the index would have diverged from the volume with no signal that it had.

The recovery cannot be re-broken by the failure it is recovering from. The `Error` frame that ended the drive belongs to the arm the rescan superseded, so Task 7b2's epoch drops it at the demux if it is still in flight when the drive is re-armed. Without that, the fresh channel would be faulted by the old arm's failure and the pump would re-drop the drive the rescan had just restored, with a message describing a stream that no longer exists.

**File sizes.** `MFTLib/Index/FileIndex.Watch.cs` is 294 lines and would cross 400, so the pump, the session type, and the failure recording move to `MFTLib/Index/FileIndex.WatchPump.cs`, leaving `FileIndex.Watch.cs` with the events, `StartWatchingAsync`, `StartWatchingCoreAsync`, `StopWatchingAsync`, `BuildWatchTargets`, `BuildWatchTarget`, `ApplyJournalEntries`, `ApplyJournalEntriesCore`, and `RaiseChanged`. `MFTLib.Tests/Index/FileIndexWatchPumpTests.cs` is 276 lines, so every new pump test goes in `MFTLib.Tests/Index/FileIndexWatchFaultTests.cs`. `MFTLib.Tests/BrokerIndexWatchSourceTests.cs` is 274 lines, so its new fault tests go in `MFTLib.Tests/BrokerIndexWatchSourceFaultTests.cs`; the scripted-frame harness nested inside the existing test class moves out to `MFTLib.Tests/TestSupport/ScriptedWatchBrokerHarness.cs` so both files share it, which also ends the name collision with the unrelated `InProcessBlockBrokerHarness` in that same directory.

`MFTLib.Tests/TestSupport/WatchHarness.cs` is 198 lines and gets the largest test-side rewrite in this task, so it splits before it grows rather than after. The fake source goes to its own `MFTLib.Tests/TestSupport/FakeIndexWatchSource.cs` as a class implementing all three `IIndexWatchSource` members, taking with it the publish channel, the per-drive arm generation and stop-requested state, the arm and disarm recording, the started and ended signals, the live-source count, and the hold-unread switch: roughly 150 to 180 lines. `WatchHarness.cs` keeps what it is actually for, which is standing an index up over synthetic blocks and tearing it down: the block builders, the producer, `FileIndex` construction, `ProducedBlocks` and `BlockFor`, the static `Batch` and `Create` entry factories, and `Dispose`. It delegates every source-facing member to the fake it owns and lands near 150 lines. Both files move from namespace `MFTLib.Tests.Index` to `MFTLib.Tests.TestSupport`, matching the folder they live in, which is the last of Task 6's deferred minors on this file; the three test files under `MFTLib.Tests/Index/` that use the harness gain `using MFTLib.Tests.TestSupport;`, and `MFTLib.Tests/BrokerIndexWatchSourceTests.cs` already has it.

- [ ] **Step 1: Write the failing tests**

Create `MFTLib.Tests/Index/FileIndexWatchFaultTests.cs` with the pump tests. Every one runs on Linux; the faults are injected through the fake source and a throwing subscriber, so none of them needs a broker, a volume, or Windows.

```csharp
[TestMethod]
public async Task ApplyFailure_IsReportedAsAnApplyFaultAndIsolatesTheDrive()
```
Open a two-drive harness (`[new IndexWatchTarget('T', 11, 4242), new IndexWatchTarget('U', 22, 8484)]`), collect `WatchFault` values, start, then publish `new JournalBatch('T', null!, JournalId: 11, NextUsn: 5000)`. Assert one fault with `WatchFaultKind.Apply` and `DriveLetter` `'T'`, and that its exception is an `ArgumentNullException`. Assert `Drives` for `'T'` has `WatchFailureMessage` non-null and `State` `DriveState.Ready`, and that `'U'` has a null `WatchFailureMessage`. Capture `harness.BlockFor('T').Header.UsnNextUsn` before publishing. Then publish a well-formed later batch for `'T'` and a batch for `'U'`, and assert the `'T'` header's `UsnNextUsn` is still the captured value while `'U'`'s header advanced and its change was raised on `Changed`. Finish with `StopWatchingAsync` throwing the `ArgumentNullException`.

```csharp
[TestMethod]
public async Task ApplyFailure_OnADriveWithNoBlock_IsAnnouncedWithoutADriveStatusMessage()
```
Single-drive harness, start, publish a batch whose `DriveLetter` is a letter the index does not know. Assert one `WatchFaultKind.Apply` fault carrying that letter and an `ArgumentException`, and that no drive's `WatchFailureMessage` is set. Publish a batch for `'T'` afterwards and assert it applied, proving one bad drive letter does not stop the pump.

```csharp
[TestMethod]
public async Task ApplyFailure_OnADriveWithNoBlock_IsAnnouncedOnceHoweverOftenItRepeats()
```
Single-drive harness, start, publish two batches in a row for the same unknown drive letter. Assert exactly one `WatchFaultKind.Apply` fault was raised, which is the assertion that pins `droppedDriveLettersWithoutOrdinal` rather than only describing it: with no ordinal to key, the ordinal dictionary cannot dedup this drive and only that set can. Publish a batch for `'T'` afterwards and assert it applied.

```csharp
[TestMethod]
public async Task DriveWatchFailure_IsReportedAsASourceFaultCarryingTheDriveLetter()
```
Two-drive harness, start, publish `new DriveWatchFailure('T', new IOException("journal wrapped"))`. Assert one fault with `WatchFaultKind.Source`, `DriveLetter` `'T'`, and that exception instance. Assert `'T'`'s `WatchFailureMessage` is `"journal wrapped"` and its `State` is `DriveState.Ready`. Publish a batch for `'T'` and assert its header cursor did not move; publish a batch for `'U'` and assert it applied. `StopWatchingAsync` rethrows the `IOException`.

```csharp
[TestMethod]
public async Task WholeStreamFault_EndsThePumpAndNamesNoDrive()
```
Two-drive harness, start, `FaultSourceAsync(new IOException("the broker died"))`. Assert one fault with `WatchFaultKind.Source` and a null `DriveLetter`, that neither drive has a `WatchFailureMessage`, and that `StopWatchingAsync` rethrows the `IOException`.

```csharp
[TestMethod]
public async Task EveryDriveFaulted_EndsThePumpAndLeavesTheFirstFaultForStop()
```
Two-drive harness, start, publish a `DriveWatchFailure` for `'T'` then one for `'U'`. Await `harness.SourceEndedAsync()`, which the harness completes from the source iterator's `finally`. Assert `harness.SourceCancelled` is false, proving the pump ended by breaking out rather than by cancellation, assert both drives carry a `WatchFailureMessage` with `State` `DriveState.Ready`, and assert `StopWatchingAsync` rethrows the `'T'` exception.

```csharp
[TestMethod]
public async Task SubscriberFault_DoesNotDropTheDrive()
```
Single-drive harness with a subscriber that throws for the first change only. Start, publish two batches. Assert exactly one `WatchFaultKind.Subscriber` fault, that `WatchFailureMessage` stays null for the drive, and that the second batch advanced the header cursor, which is what separates a subscriber fault from an apply fault.

```csharp
[TestMethod]
public async Task CallerTokenCancellation_EndsTheWatchWithNoFault()
```
Single-drive harness. Start with a token from a `CancellationTokenSource` the test owns, publish one batch, cancel that source, await `harness.SourceEndedAsync()`, assert `harness.SourceCancelled` is true, assert no `WatchFault` was raised, and assert `StopWatchingAsync` returns without throwing.

```csharp
[TestMethod]
public async Task StopWatchingAsync_WithACancelledToken_AbandonsTheWaitAndLeavesTheSessionReclaimable()
```
Single-drive harness whose fake source can be told to ignore its cancellation token, which is what a wedged source looks like from the index's side. Start, hold the source open, then call `StopWatchingAsync` with a token from an already-cancelled `CancellationTokenSource` and assert it throws `OperationCanceledException`. Release the hold, call `StopWatchingAsync(CancellationToken.None)`, and assert it returns without throwing and that a fresh `StartWatchingAsync` then succeeds, which is what proves the abandoned stop left the session reclaimable rather than orphaned or cleared. This is the bound the deferred minor asked for; assert on the token, never on elapsed time.

```csharp
[TestMethod]
public async Task StartAfterStop_LeavesNoOrphanedSessionAndClearsEveryWatchFailure()
```
Single-drive harness. Publish a `DriveWatchFailure` for `'T'`, stop (asserting the rethrow), then start again and assert `WatchFailureMessage` is null for `'T'` and that a batch published in the new session applies. Then run a back-to-back start and stop loop of twenty iterations with no publish in between, asserting after every iteration that `harness.LiveSourceCount` is zero, which is the harness's count of source iterators that entered but have not left their `finally`. State in the test's summary comment that this is the loop form rather than a deterministic interleaving, because `StartWatchingAsync` publishes the session under `_stateLock` and there is no seam to suspend it between the claim and the assignment; the loop catches an orphan on any interleaving that leaves one behind.

```csharp
[TestMethod]
public async Task RescanAsync_WhileWatching_ReArmsOnlyTheRescannedDriveFromTheFreshCursorAndClearsItsFailure()
```
Two-drive harness. Start, publish a `DriveWatchFailure` for `'T'`, assert its `WatchFailureMessage` is set, then `RescanAsync('T', token)` where the harness's producer hands back a block whose header cursor is `(13, 9000)`. Assert `harness.SourceInvocationCount` is still 1, proving the stream was never restarted; that `harness.DisarmedDrives` and `harness.ArmedDrives` each contain exactly `'T'`, in that order; that the re-arm target carries `(13, 9000)`; that `'T'`'s `WatchFailureMessage` is null; and that a batch published after the rescan applies to `'T'`.

```csharp
[TestMethod]
public async Task RescanAsync_WhileWatching_NeverStopsTheOtherDrive()
```
Two-drive harness. Start, publish a batch for `'U'`, `RescanAsync('T', token)`, then publish a second batch for `'U'`. Assert `'U'` was never disarmed or re-armed, that both of its batches applied, and that its header cursor advanced across the rescan.

```csharp
[TestMethod]
public async Task RescanAsync_WhileWatching_DropsABatchAlreadyQueuedOnTheMergedStreamWhenTheDriveWasDisarmed()
```
Two-drive harness whose source can be told to hold a batch in the merged channel unread. Queue a batch for `'T'` carrying a cursor behind the rescan's, run `RescanAsync('T', token)`, then let the pump drain. Assert `'T'`'s header cursor is the fresh `(13, 9000)` and not the queued batch's, which is the assertion that pins the arm-generation filter rather than only describing it. This test keeps its subject after Task 7b2: the wire epoch drops what the broker had already written, and this drops what the source had already queued, which is the one no wire rule can reach because it was legitimately delivered under the epoch that was current at the time. The demux half is pinned separately by `Demux_DropsAJournalBatchTaggedWithASupersededArmEpoch` in Task 7b2.

```csharp
[TestMethod]
public async Task RescanAsync_AfterEveryDriveFaulted_ReclaimsTheSessionAndStartsAFreshOne()
```
Two-drive harness. Start, publish a `DriveWatchFailure` for each drive, await `harness.SourceEndedAsync()`, then `RescanAsync('T', token)`. Assert `harness.SourceInvocationCount` is 2, that both drives' `WatchFailureMessage` values are null afterwards, that the rescan did not throw the first drive's fault, and that a batch published for `'U'` in the new session applies.

```csharp
[TestMethod]
public async Task RescanAsync_WithNoWatchRunning_ArmsNothingAndDisarmsNothing()
```
Two-drive harness with no `StartWatchingAsync` call. `RescanAsync('T', token)`, then assert `harness.SourceInvocationCount` is zero, that `harness.ArmedDrives` and `harness.DisarmedDrives` are both empty, and that the drive rescanned normally, which pins that the recovery path is inert when no watch is running.

Split `MFTLib.Tests/TestSupport/WatchHarness.cs` for these, moving its fake source into `MFTLib.Tests/TestSupport/FakeIndexWatchSource.cs` as a class implementing `IIndexWatchSource` rather than a lambda, and put both files in namespace `MFTLib.Tests.TestSupport`. `FakeIndexWatchSource` owns the publish channel and gains a `PublishAsync(WatchStreamItem item)` whose acknowledgement mechanism is the existing one, a `Task<IReadOnlyList<IndexWatchTarget>> SourceStartedAsync()` completed from the top of the source iterator, a `Task SourceEndedAsync()` completed from its `finally`, an `int LiveSourceCount` incremented on entry and decremented in that `finally`, `IReadOnlyList<char> DisarmedDrives` and `IReadOnlyList<IndexWatchTarget> ArmedDrives` recording the per-drive calls in order, a switch that holds published items in the channel unread so a test can queue a batch across a rescan, and a switch that makes the iterator ignore its cancellation token so a test can drive a wedged stop. It keeps the existing `SourceInvocationCount`, `SourceCancelled`, `FaultSourceAsync`, and `CompleteSourceAsync` unchanged in meaning. It mirrors the production seam's own per-drive state: an arm generation and a stop-requested flag per drive, so an item published for a drive that is currently disarmed is dropped rather than yielded, exactly as the real source drops it. It models no wire epoch, because the fake stands in for the seam and the epoch lives a layer below it.

`WatchHarness` keeps the block builders, the producer, `FileIndex` construction, and `Dispose`, gains `IReadOnlyDictionary<char, BlockFile> ProducedBlocks` and `BlockFile BlockFor(char driveLetter)` replacing the single `ProducedBlock`, gains a per-drive cursor the producer can be told to hand back on a rescan, and forwards every source-facing member to the `FakeIndexWatchSource` it owns. Its `SourceStoppedBeforeBlockDisposed` check reads `BlockFor(driveLetter).Header.Generation` for the first drive the harness was constructed with, since that block's mapping is released by the same `DisposeAsync` as every other one and any single block answers the question the flag asks.

Awaiting `SourceStartedAsync()` replaces reading `RequestedTargets` straight after `StartWatchingAsync`: the review's Minor 5 noted that `await Task.Yield()` says nothing about pump progress, and this task makes the pump yield before touching the source at all, so the old reads would be racy. Never sleep and never assert on elapsed time; the existing ten-second `WaitAsync` stays a hang guard, not an assertion.

Rewrite the affected existing tests in the same step, since the seam changes shape: `MFTLib.Tests/Index/FileIndexWatchPumpTests.cs` for `IAsyncEnumerable<WatchStreamItem>`, `BlockFor`, `SourceStartedAsync`, and `StopWatchingAsync`'s required token; `MFTLib.Tests/Index/FileIndexWatchTests.cs` for its `FailIfCalled` source, which becomes a small class implementing `IIndexWatchSource` whose three members all fail the test if called; `MFTLib.Tests/Index/QueryContractTests.cs` to assert `WatchFailureMessage` is null on a freshly built `DriveStatus`. All three gain `using MFTLib.Tests.TestSupport;`.

Create `MFTLib.Tests/BrokerIndexWatchSourceFaultTests.cs`, driving scripted `BrokerProtocol` frames over the `DuplexStream` harness. Every frame written for a drive carries that drive's current arm epoch, read from the `StartWatch` frame the test takes off the server side through `WatchSpecArmEpochs.ForDrive` (Task 7b2); no test writes a literal epoch, because the client issues them and a literal would pin the issue order rather than the behaviour under test:

```csharp
[TestMethod]
public async Task WatchSource_YieldsAPerDriveFaultItemAndKeepsTheOtherDriveFlowing()
```
Two targets, `'C'` and `'D'`. Read the `StartWatch` frame, then write an `Error` frame for `"D"` with message `"journal wrapped"` and a `JournalBatch` frame for `"C"` in one write. Collect two items and break. Assert one is a `DriveWatchFailure` with `DriveLetter` `'D'` and message `"journal wrapped"`, and the other a `JournalBatch` with `DriveLetter` `'C'` and the cursor the frame carried. Assert by item type and drive, never by index: two reader tasks write into one channel and their order is not deterministic. Breaking disposes the enumerator, so finish by reading the `EndWatch` frame and writing the ack.

```csharp
[TestMethod]
public async Task WatchSource_CompletesAfterEveryDriveHasFaulted()
```
Two targets. Write an `Error` frame for each drive, consume to completion, and assert the enumeration ended normally with exactly two `DriveWatchFailure` items and no exception.

```csharp
[TestMethod]
public async Task WatchSource_FaultsTheWholeStreamWhenItCannotConnect()
```
Build the source over a `connectAsync` that throws `IOException("the broker never launched")`. Enumerate it and assert it throws that exception and yielded no item first. Task 7b removed the client's single start reservation, so a second `SendStartWatchAsync` no longer throws and can no longer stand in for a start failure; a connect that fails is the honest one, and it is also what a declined elevation prompt looks like.

```csharp
[TestMethod]
public async Task WatchSource_RejectsTwoTargetsForOneDriveBeforeConnecting()
```
Two targets both naming `'C'` with different cursors. Assert an `ArgumentException` whose `ParamName` is `"targets"` and whose message names the drive, and that `harness.ConnectionCount` is zero, which is what proves the check runs at the boundary rather than surfacing later as `ToDictionary`'s duplicate-key throw. This is the deferred minor from Task 7's review, settled as a test rather than a comment.

```csharp
[TestMethod]
public async Task ArmDriveAsync_OnALiveStream_AddsThatDrivesBatchesWithoutRestartingTheOthers()
```
Start the stream over one target, read the `StartWatch` frame, then call `ArmDriveAsync` for a second drive and read the second `StartWatch` frame. Write a `JournalBatch` for each drive, each tagged with the epoch its own `StartWatch` frame carried, and assert both arrive as `JournalBatch` items on the one stream, asserting by drive and never by index.

```csharp
[TestMethod]
public async Task ArmDriveAsync_OnADriveWhoseReaderIsStillLive_ReplacesItWithoutHanging()
```
One target, `'C'`, with its reader genuinely still running: read the `StartWatch` frame, write a `JournalBatch` for `'C'` and consume it, so the reader is inside its `await foreach` rather than finished. Then call `ArmDriveAsync` for `'C'` with a later cursor, passing the harness's ten-second hang-guard token as the argument, and await it. Assert it completes, that the second `StartWatch` frame carries the later cursor and a higher arm epoch than the first, and that a batch written afterwards with the new epoch is yielded. This is the arm-ordering test: awaiting the previous reader before calling the client would await a task nothing has told to finish, and the guard token turns that hang into a failed assertion instead of a wedged run. Assert on the token, never on elapsed time.

```csharp
[TestMethod]
public async Task ArmDriveAsync_OnADriveWhoseReaderIsStillLive_LeavesTheOtherDriveStreaming()
```
Two targets. Re-arm `'C'` on a live reader as above, then write a `JournalBatch` for `'D'` and assert it is still yielded. This is the stop-flag test: without it the replaced reader reads its normal channel completion as the generation ending, completes the merged channel, and `'D'`'s batch never arrives.

```csharp
[TestMethod]
public async Task DisarmDriveAsync_OnALiveStream_StopsThatDrivesItemsAndLeavesTheOtherFlowing()
```
Two targets. Call `DisarmDriveAsync` for `'C'`, read the `DisarmDrive` frame, then write a `JournalBatch` for `'C'` followed by one for `'D'`. Assert only `'D'`'s item is yielded; `'D'`'s arrival is the ordering barrier proving `'C'`'s frame was read and dropped, so no sleep is needed.

```csharp
[TestMethod]
public async Task ArmDriveAsync_AfterADisarm_DropsAnItemTheOldReaderHadAlreadyQueuedAndYieldsTheOnesAfter()
```
One target. Write a `JournalBatch` for `'C'` tagged with the first arm's epoch while the consumer is not reading, so the client delivers it and the old reader queues it on the merged channel. Disarm `'C'`, re-arm it, then write a second `JournalBatch` for `'C'` with a later cursor tagged with the second arm's epoch. Drain and assert exactly one item, carrying the later cursor. This is the arm-generation filter's own test, and after Task 7b2 it is precisely the case the wire epoch cannot reach: the dropped item was legitimately delivered under the epoch that was current when it arrived.

```csharp
[TestMethod]
public async Task ArmDriveAsync_AfterADisarm_DropsAnErrorFrameFromTheSupersededArm()
```
One target. Disarm `'C'` and re-arm it, then write an `Error` frame for `"C"` tagged with the first arm's epoch, followed by a `JournalBatch` for `'C'` tagged with the second. Assert the batch is yielded and no `DriveWatchFailure` ever is. This is the cross-layer pin for the recovery contract: without Task 7b2's epoch the old arm's failure would fault the fresh channel and re-drop the drive the rescan had just restored.

```csharp
[TestMethod]
public async Task ArmDriveAsync_WithNoStreamRunning_ThrowsInvalidOperationException()
```
Also assert the same for `DisarmDriveAsync`, and that neither wrote anything to the pipe.

Split `WatchSource_StartsOneWatchForEveryTargetAndTagsEachBatch` in `MFTLib.Tests/BrokerIndexWatchSourceTests.cs`, which is Task 7's review's deferred minor: it currently asserts two unrelated things, the per-target watch and the borrowed client's readiness afterwards. It keeps its name and its first half, up to and including the per-drive item assertions. Its trailing block, the second `SendStartWatchAsync` and the `StopLiveWatchAsync` handshake, becomes:

```csharp
[TestMethod]
public async Task WatchSource_LeavesTheBorrowedClientReadyForAnotherWatch()
```
Run one stream to a break as the first test does, then arm `"C"` directly on `harness.Client`, read the `StartWatch` frame, and run `StopLiveWatchAsync` through its `EndWatch` and ack handshake. Assert the stop completes, which is the whole of what this half was ever pinning: the source's `finally` leaves the client usable rather than half torn down.

Rewrite `WatchSource_SurfacesAPerDriveWatchFault` in the same file into `WatchSource_DoesNotFaultTheStreamForOneDrivesError`, asserting the enumeration does not throw and that the other drive keeps yielding, and retype its `List<JournalBatch>` helpers to `List<WatchStreamItem>`. Move that file's nested scripted harness out to `MFTLib.Tests/TestSupport/ScriptedWatchBrokerHarness.cs` under the name `ScriptedWatchBrokerHarness`, keeping `Client`, `CancellationToken`, `ConnectionCount`, `ConnectAsync`, `ReadFrameAsync`, `WriteAsync`, and `DisposeAsync` as they are, and point both broker test files at it. It gains one member for Task 7b2: `uint ArmEpochForDrive(BrokerFrame startWatch, char driveLetter)`, forwarding to `WatchSpecArmEpochs.ForDrive(startWatch, driveLetter.ToString(CultureInfo.InvariantCulture))` (the shared helper takes the drive as a string), so a test that has just read a `StartWatch` frame writes its reply frames with the epoch that arm issued instead of repeating the parse.

- [ ] **Step 2: Run to verify they fail**

```bash
dotnet test MFTLib.Tests/MFTLib.Tests.csproj -p:Platform=x64 --filter "FullyQualifiedName~WatchFaultTests|FullyQualifiedName~BrokerIndexWatchSourceFaultTests"
```

Expected: FAIL to compile the whole test project, because `WatchStreamItem`, `DriveWatchFailure`, `IIndexWatchSource`, `WatchFaultKind.Apply`, `DriveStatus.WatchFailureMessage`, `StopWatchingAsync`'s required token, `WatchHarness.BlockFor`, `FakeIndexWatchSource`, `WatchHarness.SourceStartedAsync`, `WatchHarness.SourceEndedAsync`, `WatchHarness.LiveSourceCount`, `WatchHarness.ArmedDrives`, `WatchHarness.DisarmedDrives`, `ScriptedWatchBrokerHarness`, and `ScriptedWatchBrokerHarness.ArmEpochForDrive` do not exist. That single compile failure is the stated RED reason for every test named in Step 1: `ApplyFailure_IsReportedAsAnApplyFaultAndIsolatesTheDrive`, `ApplyFailure_OnADriveWithNoBlock_IsAnnouncedWithoutADriveStatusMessage`, `ApplyFailure_OnADriveWithNoBlock_IsAnnouncedOnceHoweverOftenItRepeats`, `DriveWatchFailure_IsReportedAsASourceFaultCarryingTheDriveLetter`, `WholeStreamFault_EndsThePumpAndNamesNoDrive`, `EveryDriveFaulted_EndsThePumpAndLeavesTheFirstFaultForStop`, `SubscriberFault_DoesNotDropTheDrive`, `CallerTokenCancellation_EndsTheWatchWithNoFault`, `StopWatchingAsync_WithACancelledToken_AbandonsTheWaitAndLeavesTheSessionReclaimable`, `StartAfterStop_LeavesNoOrphanedSessionAndClearsEveryWatchFailure`, `RescanAsync_WhileWatching_ReArmsOnlyTheRescannedDriveFromTheFreshCursorAndClearsItsFailure`, `RescanAsync_WhileWatching_NeverStopsTheOtherDrive`, `RescanAsync_WhileWatching_DropsABatchAlreadyQueuedOnTheMergedStreamWhenTheDriveWasDisarmed`, `RescanAsync_AfterEveryDriveFaulted_ReclaimsTheSessionAndStartsAFreshOne`, `RescanAsync_WithNoWatchRunning_ArmsNothingAndDisarmsNothing`, `WatchSource_YieldsAPerDriveFaultItemAndKeepsTheOtherDriveFlowing`, `WatchSource_CompletesAfterEveryDriveHasFaulted`, `WatchSource_FaultsTheWholeStreamWhenItCannotConnect`, `WatchSource_RejectsTwoTargetsForOneDriveBeforeConnecting`, `ArmDriveAsync_OnALiveStream_AddsThatDrivesBatchesWithoutRestartingTheOthers`, `ArmDriveAsync_OnADriveWhoseReaderIsStillLive_ReplacesItWithoutHanging`, `ArmDriveAsync_OnADriveWhoseReaderIsStillLive_LeavesTheOtherDriveStreaming`, `DisarmDriveAsync_OnALiveStream_StopsThatDrivesItemsAndLeavesTheOtherFlowing`, `ArmDriveAsync_AfterADisarm_DropsAnItemTheOldReaderHadAlreadyQueuedAndYieldsTheOnesAfter`, `ArmDriveAsync_AfterADisarm_DropsAnErrorFrameFromTheSupersededArm`, `ArmDriveAsync_WithNoStreamRunning_ThrowsInvalidOperationException`, and the rewritten `WatchSource_DoesNotFaultTheStreamForOneDrivesError` and split-out `WatchSource_LeavesTheBorrowedClientReadyForAnotherWatch`.

Then comment out the seam changes in the test files alone and re-run, so each behavioural test fails for its own reason rather than only for the compile. Record for each:

- The three `ApplyFailure_` tests fail because every apply exception is reported as `WatchFaultKind.Subscriber` and the drive keeps pumping; the repeat test additionally fails because nothing deduplicates a drive letter with no ordinal.
- `DriveWatchFailure_IsReportedAsASourceFaultCarryingTheDriveLetter` and `WatchSource_YieldsAPerDriveFaultItemAndKeepsTheOtherDriveFlowing` fail because a `DriveWatchFailure` cannot be expressed on a stream of `JournalBatch`.
- `EveryDriveFaulted_EndsThePumpAndLeavesTheFirstFaultForStop`, `WatchSource_CompletesAfterEveryDriveHasFaulted`, and `WatchSource_DoesNotFaultTheStreamForOneDrivesError` fail because `ReadDriveAsync` completes the shared channel with the exception, so the first fault ends the whole stream.
- `SubscriberFault_DoesNotDropTheDrive` and `WholeStreamFault_EndsThePumpAndNamesNoDrive` pass today for the wrong reason, since every fault is currently a subscriber fault or a source fault with no drive; assert `WatchFailureMessage` and the header cursor so they fail on the new state rather than the old labels, and record them as pins rather than reds.
- `CallerTokenCancellation_EndsTheWatchWithNoFault` fails because the session source is unlinked from the caller's token.
- `StopWatchingAsync_WithACancelledToken_AbandonsTheWaitAndLeavesTheSessionReclaimable` fails because `StopWatchingAsync` takes no token and awaits the pump with nothing to bound it, so the wedged-source arm of the test never returns; give the test itself a hang guard so the red is a failure rather than a hung run.
- `StartAfterStop_LeavesNoOrphanedSessionAndClearsEveryWatchFailure` fails because the pump is published outside the lock that claims the cancellation source.
- Every `RescanAsync_` test fails because `RescanAsync` never touches the watch: nothing is disarmed, nothing is re-armed, and no failure message is cleared.
- `WatchSource_FaultsTheWholeStreamWhenItCannotConnect` fails because the source calls `connectAsync` inside an iterator whose first `MoveNextAsync` the test's assertion shape does not currently reach; assert that no item was yielded before the throw.
- `WatchSource_RejectsTwoTargetsForOneDriveBeforeConnecting` fails because the duplicate surfaces from `ToDictionary` after `connectAsync` has already run, so both the exception's `ParamName` and the zero connection count are wrong.
- The five `ArmDriveAsync_` and `DisarmDriveAsync_` tests fail because the seam is a delegate with no arm or disarm operation at all.
- `ArmDriveAsync_AfterADisarm_DropsAnErrorFrameFromTheSupersededArm` fails for that same absence, and would still fail without Task 7b2 even once the seam exists, because the stale `Error` would reach the fresh channel and fault it.
- `WatchSource_LeavesTheBorrowedClientReadyForAnotherWatch` is green before and after; record it as a pin carved out of an existing test rather than a red.

- [ ] **Step 3: Implement**

Create `MFTLib/Index/WatchStreamItem.cs`:

```csharp
namespace MFTLib.Index;

/// <summary>
///     One item on the merged stream an <see cref="IIndexWatchSource" /> yields: either a
///     <see cref="JournalBatch" /> to apply or a <see cref="DriveWatchFailure" /> saying that
///     one drive's watch has ended badly. A per-drive failure travels as data rather than as an
///     exception so that one drive's problem cannot end another drive's watch.
/// </summary>
public abstract record WatchStreamItem
{
    private protected WatchStreamItem()
    {
    }
}

/// <summary>
///     One drive's watch has failed and will deliver nothing further in this session. The index
///     stops applying that drive's batches and leaves its block header cursor where the last
///     successfully applied batch left it, so a later re-arm resumes from a cursor that is still
///     true.
/// </summary>
public sealed record DriveWatchFailure(char DriveLetter, Exception Exception) : WatchStreamItem;
```

Modify `MFTLib/Index/JournalBatch.cs` so the record derives from `WatchStreamItem`:

```csharp
public sealed record JournalBatch(char DriveLetter, IReadOnlyList<UsnJournalEntry> Entries,
    ulong JournalId, long NextUsn) : WatchStreamItem;
```

Delete `MFTLib/Index/IndexWatchSource.cs`, move `IndexWatchTarget` verbatim into a new `MFTLib/Index/IndexWatchTarget.cs`, and create `MFTLib/Index/IIndexWatchSource.cs`:

```csharp
public interface IIndexWatchSource
{
    IAsyncEnumerable<WatchStreamItem> StartWatching(
        IReadOnlyList<IndexWatchTarget> targets, CancellationToken cancellationToken);

    Task ArmDriveAsync(IndexWatchTarget target, CancellationToken cancellationToken);

    Task DisarmDriveAsync(char driveLetter, CancellationToken cancellationToken);
}
```

Document the implementation's obligations on the interface and on each member. `StartWatching` yields a `DriveWatchFailure` and stops reading that one drive when that drive's watch fails; it completes the stream when the whole generation has ended or when the token is cancelled; it throws only when it cannot start at all or when a failure cannot be attributed to one drive; it rejects two targets naming one drive with an `ArgumentException` before it starts. The index cancels the token when the session stops and the source must then finish, since a source that ignores its token wedges `StopWatchingAsync` until that call's own token bounds the wait. `ArmDriveAsync` adds or replaces one drive on the running stream, resuming it from the target's cursor and yielding nothing that was produced before the arm; when it replaces a drive that is already armed it must retire the old reader before starting the fresh one, and must do so without ending any other drive's watch. `DisarmDriveAsync` stops one drive's items, is not a failure and yields no `DriveWatchFailure`, and completes only once nothing further for that drive can reach the stream. Both per-drive members honour their `cancellationToken` while they wait for a retiring reader, so a wedged reader surfaces as a cancellation rather than a hang. One instance runs at most one stream at a time: `StartWatching` throws `InvalidOperationException` when one is already live and the two per-drive members throw it when none is.

Modify `MFTLib/Index/WatchFault.cs` to add the third member and correct the doc comments on the enum's members and on the record itself:

```csharp
public enum WatchFaultKind
{
    /// <summary>A <see cref="FileIndex.Changed" /> subscriber threw while receiving a batch.</summary>
    Subscriber,

    /// <summary>
    ///     A watch source failure. With a drive letter, that one drive's watch has ended and the
    ///     rest of the session continues without it. With a null drive letter, the merged stream
    ///     itself failed and the pump has ended.
    /// </summary>
    Source,

    /// <summary>
    ///     The index could not apply a batch. The block is unchanged, the drive's cursor was not
    ///     advanced, and that drive is dropped for the rest of the session.
    /// </summary>
    Apply
}

/// <summary>
///     A fault observed by the watch pump. <paramref name="DriveLetter" /> names the drive the
///     fault belongs to, whichever <see cref="WatchFaultKind" /> it carries, and is null only for
///     a <see cref="WatchFaultKind.Source" /> failure of the merged stream itself, which is
///     attributable to no single drive and ends the pump.
/// </summary>
public sealed record WatchFault(WatchFaultKind Kind, char? DriveLetter, Exception Exception);
```

Modify `MFTLib/Index/DriveStatus.cs` to add, leaving `MftProducerFailureMessage` and its doc comment untouched:

```csharp
/// <summary>
///     Set when this drive's live watch failed, from the message of the exception that ended
///     it. The drive stays <see cref="DriveState.Ready" />: its block is valid and every query
///     still answers from it, but nothing after the failing batch has been applied. Cleared
///     when the drive is armed again by <see cref="FileIndex.StartWatchingAsync" /> or by a
///     rescan. Null while the watch is healthy or not running.
/// </summary>
public string? WatchFailureMessage { get; init; }
```

Modify `MFTLib/Index/FileIndexOptions.cs` so the property becomes `public IIndexWatchSource? WatchSource { get; init; }` and its doc comment says the stream carries per-drive failures as items, faults only for a failure that names no drive, and is the same object a rescan asks to disarm and re-arm one drive.

Modify `MFTLib/Index/FileIndex.cs`: add `readonly Dictionary<ushort, string> _watchFailureMessagesByOrdinal = [];` beside `_mftProducerFailureMessagesByOrdinal`; replace the `_watchCancellation` and `_watchPump` fields with `WatchSession? _watchSession;`; read the new dictionary in `DescribeOnlineDrive` with `GetValueOrDefault` and thread it into `DescribeDrive`, which sets `WatchFailureMessage = watchFailureMessage`. `DescribeDrive` goes from four parameters to five, one under the `maxParams: 6` limit; if a later task needs a sixth it takes a record instead. In `DisposeAsync`, pass `CancellationToken.None` to `StopWatchingAsync`, since disposal has no token of its own and must not abandon a pump whose blocks it is about to unmap, and delete the `_ = exception;` line from the catch that follows it, leaving `catch (Exception)` with the comment that already explains why the fault is discarded there. That is one of Task 6's two dead-code sites; the other is in `RaiseWatchFaulted`, which moves in the next file.

Create `MFTLib/Index/FileIndex.WatchPump.cs` holding the session type, the pump, and the failure recording:

```csharp
sealed class WatchSession(CancellationTokenSource cancellation, CancellationToken callerToken,
    IIndexWatchSource source)
{
    public CancellationTokenSource Cancellation { get; } = cancellation;

    public CancellationToken CallerToken { get; } = callerToken;

    // The exact source the pump is reading, so a rescan reaches the arm and disarm
    // operations of the stream in flight rather than of some other instance.
    public IIndexWatchSource Source { get; } = source;

    public Task<Exception?> Pump { get; set; } = Task.FromResult<Exception?>(null);
}

async Task<Exception?> PumpAsync(IIndexWatchSource source, IReadOnlyList<IndexWatchTarget> targets,
    CancellationToken cancellationToken)
{
    // Returning to StartWatchingAsync before any source code runs is what makes it safe to
    // launch this pump inside _stateLock: the lock is released before the source connects.
    await Task.Yield();

    // The one drop the ordinal dictionary cannot record. A letter TryGetDriveOrdinal does not
    // resolve has no key, so without this a second failure item for it would be announced
    // twice. Session-local on purpose: a fresh session starts with nothing dropped, which is
    // the same rule arming follows for every drive that does have an ordinal.
    var droppedDriveLettersWithoutOrdinal = new HashSet<char>();

    Exception? firstFault = null;
    var dropped = false;
    try
    {
        await foreach (var item in source.StartWatching(targets, cancellationToken).ConfigureAwait(false))
        {
            if (item is DriveWatchFailure failure)
            {
                if (DropDrive(failure.DriveLetter, failure.Exception, WatchFaultKind.Source,
                        droppedDriveLettersWithoutOrdinal))
                {
                    firstFault ??= failure.Exception;
                    dropped = true;
                }
            }
            else if (item is JournalBatch batch &&
                     !IsDriveWatchFaulted(batch.DriveLetter, droppedDriveLettersWithoutOrdinal))
            {
                dropped = !TryApplyBatch(batch, droppedDriveLettersWithoutOrdinal,
                    cancellationToken, ref firstFault);
            }

            if (dropped && !AnyWatchedDriveRemains(targets, droppedDriveLettersWithoutOrdinal))
            {
                // Every watched drive has failed. Breaking disposes the enumerator, which ends
                // the watch at the source; the session stays so StopWatchingAsync still rethrows.
                break;
            }

            dropped = false;
        }
    }
    catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
    {
        if (firstFault is null)
        {
            throw;
        }

        // Preserve a fault already observed before an ordinary stop cancelled the source.
    }
    catch (Exception exception)
    {
        firstFault ??= exception;
        RaiseWatchFaulted(new WatchFault(WatchFaultKind.Source, null, exception));
    }

    return firstFault;
}
```

`TryApplyBatch(JournalBatch batch, HashSet<char> droppedDriveLettersWithoutOrdinal, CancellationToken cancellationToken, ref Exception? firstFault)` calls `ApplyJournalEntriesCore` inside one `try` and `RaiseChanged` inside another. Both catches carry the filter that keeps this session's own cancellation out of them:

```csharp
    catch (Exception exception) when (
        exception is not OperationCanceledException || !cancellationToken.IsCancellationRequested)
```

A cancellation that belongs to the session therefore escapes both and lands in `PumpAsync`'s `OperationCanceledException` handler, where an ordinary stop is not a fault and drops no drive; this is Task 6's deferred filter, restored on both of the catches that replace the one it was dropped from. A core exception drops the drive with `WatchFaultKind.Apply`, records `firstFault` if it is the first, and returns false. A `RaiseChanged` exception records `firstFault` and raises `WatchFaultKind.Subscriber` only when it is the first fault of the session, and returns true, because a subscriber failure does not drop the drive. A clean apply returns true.

```csharp
bool DropDrive(char driveLetter, Exception exception, WatchFaultKind kind,
    HashSet<char> droppedDriveLettersWithoutOrdinal)
{
    // Resolved outside the lock: TryGetDriveOrdinal takes _stateLock itself.
    var hasOrdinal = TryGetDriveOrdinal(driveLetter, out var driveOrdinal);
    bool firstDrop;
    lock (_stateLock)
    {
        firstDrop = hasOrdinal
            ? _watchFailureMessagesByOrdinal.TryAdd(driveOrdinal, exception.Message)
            : droppedDriveLettersWithoutOrdinal.Add(char.ToUpperInvariant(driveLetter));
    }

    if (!firstDrop)
    {
        return false;
    }

    RaiseWatchFaulted(new WatchFault(kind, driveLetter, exception));
    return true;
}
```

`DropDrive` returns false when the drive is already dropped, so a second failure item for the same drive changes nothing, whether that drive has an ordinal or not. `IsDriveWatchFaulted(char driveLetter, HashSet<char> droppedDriveLettersWithoutOrdinal)` resolves the ordinal the same way and tests `ContainsKey` under `_stateLock`, falling back to the set for a letter with no ordinal. `AnyWatchedDriveRemains(IReadOnlyList<IndexWatchTarget> targets, HashSet<char> droppedDriveLettersWithoutOrdinal)` returns true when any target's drive is not dropped, evaluated freshly rather than counted down so that a drive a rescan re-armed and cleared is live again with no second structure to keep in step. `RaiseWatchFaulted` moves here with the `_ = exception;` line deleted from its catch, leaving `catch (Exception)` and the comment that already says why a fault reporter's own failure is discarded.

Rewrite `StartWatchingAsync` in `MFTLib/Index/FileIndex.Watch.cs` around an internal core so the rescan can re-arm without reparenting the session's lifetime:

```csharp
/// <summary>
///     Starts one pump over every MFT-backed drive. Each drive resumes from the journal cursor
///     persisted in its current block header, and every armed drive's
///     <see cref="DriveStatus.WatchFailureMessage" /> is cleared. Cancelling
///     <paramref name="cancellationToken" /> ends the session and raises no fault; the session
///     is reclaimed by <see cref="StopWatchingAsync" /> or <see cref="DisposeAsync" />. An index
///     with no watchable drives has nothing to start and completes immediately.
/// </summary>
public Task StartWatchingAsync(CancellationToken cancellationToken)
{
    return StartWatchingCoreAsync(cancellationToken, cancellationToken);
}

Task StartWatchingCoreAsync(CancellationToken sessionToken, CancellationToken startCancellationToken)
{
    ObjectDisposedException.ThrowIf(_disposed, this);
    startCancellationToken.ThrowIfCancellationRequested();

    var targets = BuildWatchTargets();
    if (targets.Count == 0)
    {
        return Task.CompletedTask;
    }

    var source = _options.WatchSource ?? throw new InvalidOperationException(
        $"{targets.Count} drive(s) support a live watch but " +
        $"{nameof(FileIndexOptions)}.{nameof(FileIndexOptions.WatchSource)} is not set.");

    lock (_stateLock)
    {
        if (_watchSession is not null)
        {
            throw new InvalidOperationException("This index is already watching.");
        }

        ClearWatchFailures(targets);
        var session = new WatchSession(
            CancellationTokenSource.CreateLinkedTokenSource(sessionToken), sessionToken, source);
        session.Pump = PumpAsync(source, targets, session.Cancellation.Token);
        _watchSession = session;
    }

    return Task.CompletedTask;
}
```

The core is not an `async` method, because nothing in it awaits once the pump yields on its own; its argument and state validation therefore throws synchronously rather than through the returned task. That is invisible to every existing caller and test, since `Assert.ThrowsExceptionAsync` invokes its delegate inside its own `try` and `RescanAsync` awaits the call.

`ClearWatchFailures` removes the ordinal entry for every target it is given, under the caller's `_stateLock`. `StopWatchingAsync` gains its required token and bounds its wait on the pump:

```csharp
/// <summary>
///     Cancels the current watch, waits for its pump to finish, and rethrows the first fault
///     observed during the session. Calling this when no watch is active has no effect.
///     <paramref name="cancellationToken" /> bounds the wait: cancelling it abandons the wait
///     and throws, and deliberately leaves the session in place so a later stop or
///     <see cref="DisposeAsync" /> can still reclaim it. A source that ignores the token this
///     call cancels is the only thing that can make that wait outlast the caller's patience.
/// </summary>
public async Task StopWatchingAsync(CancellationToken cancellationToken)
{
    WatchSession? session;
    lock (_stateLock)
    {
        session = _watchSession;
    }

    if (session is null)
    {
        return;
    }

    Exception? firstFault = null;
    var pumpFinished = false;
    try
    {
        await session.Cancellation.CancelAsync().ConfigureAwait(false);
        firstFault = await session.Pump.WaitAsync(cancellationToken).ConfigureAwait(false);
        pumpFinished = true;
    }
    catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
    {
        // Cancellation is how a stop terminates a source waiting for its next batch. The
        // filter separates that from this call's own token being cancelled, which is an
        // abandoned wait over a pump that is still running.
        pumpFinished = true;
    }
    finally
    {
        if (pumpFinished)
        {
            lock (_stateLock)
            {
                if (ReferenceEquals(_watchSession, session))
                {
                    _watchSession = null;
                }
            }

            session.Cancellation.Dispose();
        }
    }

    if (firstFault is not null)
    {
        ExceptionDispatchInfo.Capture(firstFault).Throw();
    }
}
```

Nothing here disposes the source: the interface declares no disposal member and the source releases what it borrowed in the `finally` of the stream the cancellation above ends.

Split `ApplyJournalEntries`: move its body up to and including the `_swapGate.Release()` into `internal IReadOnlyList<FileChange> ApplyJournalEntriesCore(char driveLetter, IReadOnlyList<UsnJournalEntry> entries, ulong journalId, long nextUsn)`, and leave the public method as the core call followed by `RaiseChanged(changes)` and `return changes;`. The public XML doc comment is unchanged apart from one sentence saying the watch pump calls the core and raises `Changed` itself so it can tell an apply failure from a subscriber failure.

Modify `MFTLib/Index/FileIndex.Rescan.cs`. Before `_swapGate.WaitAsync`, disarm the rescanned drive:

```csharp
// Disarming before the gate, never under it: the pump takes _swapGate synchronously
// inside ApplyJournalEntriesCore, so touching the watch while this method holds the gate
// would deadlock the rescan against its own pump. Nothing here deadlocks the other way
// either, because the disarm awaits a broker-side reader that never takes the gate and
// the merged channel is unbounded, so a blocked pump never blocks a reader's write.
var suspended = await SuspendDriveForRescanAsync(driveLetter, cancellationToken).ConfigureAwait(false);
```

`SuspendDriveForRescanAsync(char driveLetter, CancellationToken cancellationToken)` reads `_watchSession` under `_stateLock` and returns a null result when there is none, so a rescan with no watch running behaves exactly as it did. When a session exists but its pump has already completed, meaning every drive had faulted, it calls `StopWatchingAsync(cancellationToken)` inside a `try`/`catch` that discards the fault and reports that the session must be restarted whole. Otherwise it calls `session.Source.DisarmDriveAsync(driveLetter, cancellationToken)` and reports the session, so only that one drive stops.

Inside the existing `lock (_stateLock)` that publishes the swap, leave `_watchFailureMessagesByOrdinal` alone: clearing happens after the gate is released, immediately before the re-arm, so the drive is never live while it still looks dropped. After the `_swapGate` `finally` releases:

```csharp
if (suspended.Session is { } session)
{
    // Clear before arming, never after: a fresh batch arriving while this drive still
    // looked dropped would be discarded and lost, and no batch for it can exist in
    // between, because it is disarmed at the source.
    var target = BuildWatchTarget(driveLetter);
    lock (_stateLock)
    {
        if (TryGetDriveOrdinal(driveLetter, out var armedOrdinal))
        {
            _watchFailureMessagesByOrdinal.Remove(armedOrdinal);
        }
    }

    await session.Source.ArmDriveAsync(target, cancellationToken).ConfigureAwait(false);
}
else if (suspended.RestartWholeSession)
{
    await StartWatchingCoreAsync(suspended.SessionToken, cancellationToken).ConfigureAwait(false);
}
```

`BuildWatchTarget(char driveLetter)` is `BuildWatchTargets`'s single-drive counterpart, reading `UsnJournalId` and `UsnNextUsn` out of the drive's current block header under `_stateLock`; factor the two so the cursor is read in exactly one place. The whole-session restart links to the token the original session was linked to, not the rescan's, so a rescan does not silently reparent the watch's lifetime; the rescan's own token still cancels the start call itself.

When no watch is running, the rescan still clears the drive's `_watchFailureMessagesByOrdinal` entry as part of the swap, since a message about a watch failure on a block that has just been replaced is no longer true.

Extend the method's doc comment with the recovery contract: a rescan while watching disarms only that drive, swaps its block, clears its watch failure message, and re-arms only that drive from the fresh header cursor, while every other drive keeps streaming without losing a batch; nothing produced for that drive before the re-arm is applied to the new block, whether it was still on the wire or already queued on the merged stream; a rescan issued after every drive has already faulted reclaims the session and starts a fresh one over every drive, discarding the fault it is recovering from; and a failure to re-arm surfaces from here.

Modify `MFTLib/Broker/Client/BrokerIndexWatchSource.cs`. The class declares `: IIndexWatchSource`, `CreateSource` is deleted, and `WatchAsync` is renamed to `StartWatching` and becomes the interface's member. Its per-drive state moves out of `WatchAsync`'s locals onto the instance, guarded by one `Lock`, so the two new members can reach it: a claimed flag, the connected client, the batch source, the reader `CancellationTokenSource`, the merged `Channel<TaggedItem>` writer, and three per-drive maps keyed by the upper-case drive letter, `Dictionary<char, Task> _readersByDrive`, `Dictionary<char, int> _armGenerationsByDrive`, and `HashSet<char> _stopRequestedDrives`. The letter is upper-cased once, at the boundary, and used both as the state key and on every item the source yields; the client's own string form is produced by `JournalBrokerClient.NormalizeDriveLetter` only where a call needs it. `StartWatching` claims the flag at the top and clears every one of these in the `finally` that already calls `StopLiveWatchAsync`, throwing `InvalidOperationException` if the flag is already claimed.

`_armGenerationsByDrive` is this source's own counter and is not the wire arm epoch Task 7b2 added. They never meet: the epoch is assigned by `JournalBrokerClient` and consumed by its demux, and this counter is assigned and consumed inside this class. Nothing here reads, writes, or forwards an epoch, and no member of this class takes one.

`StartWatching` validates before it connects:

```csharp
ArgumentNullException.ThrowIfNull(targets);
cancellationToken.ThrowIfCancellationRequested();
var driveLetters = new HashSet<char>();
foreach (var target in targets)
{
    if (!driveLetters.Add(char.ToUpperInvariant(target.DriveLetter)))
    {
        // Two cursors for one drive have no meaning and the dictionary below would throw a
        // duplicate-key ArgumentException naming neither the parameter nor the drive.
        throw new ArgumentException(
            $"Drive {target.DriveLetter} appears more than once in the watch targets.", nameof(targets));
    }
}
```

A stream with no targets has no reader that could ever end it, so `StartWatching` calls `writer.TryComplete()` immediately after creating the channel when `targets.Count == 0`, and the drain loop then ends at once.

The channel's element type becomes an internal `readonly record struct TaggedItem(WatchStreamItem Item, char DriveLetter, int ArmGeneration)`, and the drain loop inside `StartWatching` filters on it:

```csharp
await foreach (var tagged in channel.Reader.ReadAllAsync(cancellationToken).ConfigureAwait(false))
{
    // Anything a superseded arm queued here is stale by definition: the drive it belongs to
    // has since been disarmed or re-armed, and applying it would drag that drive's cursor
    // backwards behind a block a rescan has already replaced. The client's own arm epoch
    // drops what was still on the wire; this drops what had already crossed it and was
    // sitting in this channel when the drive was retired. A DriveWatchFailure is filtered
    // the same way, because a failure the drive has already been re-armed out of no longer
    // describes anything true about it.
    if (tagged.ArmGeneration != CurrentArmGeneration(tagged.DriveLetter))
    {
        continue;
    }

    yield return tagged.Item;
}
```

`ReadDriveAsync(JournalBatchSource batchSource, IndexWatchTarget target, int armGeneration, ChannelWriter<TaggedItem> writer, CancellationToken cancellationToken)` takes the generation it was started with, upper-cases the target's letter once, tags everything it writes, and ends like this:

```csharp
catch (Exception exception) when (
    exception is not OperationCanceledException || !cancellationToken.IsCancellationRequested)
{
    // One drive's failure ends only that drive. Writing it as an item keeps the merged
    // stream alive for every other drive, which is what the host and the client's
    // per-drive channels already do underneath.
    writer.TryWrite(new TaggedItem(new DriveWatchFailure(driveLetter, exception), driveLetter, armGeneration));
    return;
}

// A clean end with no stop asked for this drive means the client completed every channel
// at once, which is the generation ending, so the merged stream ends with it. A drive that
// was disarmed or re-armed was told to stop, and returning quietly is what stops one
// drive's replacement from ending every other drive's watch.
if (!IsStopRequested(driveLetter))
{
    writer.TryComplete();
}
```

with no rethrow and no `writer.TryComplete(exception)`. Delete `CompleteWhenAllFinishAsync`: with drives arriving and leaving while the stream is live, "every reader has finished" no longer means the watch is over, and each reader now decides for itself as above.

`ArmDriveAsync` and `DisarmDriveAsync` share one shape, and the order inside it is what keeps them from hanging:

```csharp
public async Task ArmDriveAsync(IndexWatchTarget target, CancellationToken cancellationToken)
{
    ArgumentNullException.ThrowIfNull(target);
    var driveLetter = char.ToUpperInvariant(target.DriveLetter);
    JournalBrokerClient client;
    Task? previousReader;
    lock (_streamLock)
    {
        client = _client ?? throw new InvalidOperationException(
            "No stream is running on this watch source, so there is no drive to arm on it.");

        // Both halves of the replacement are claimed before anything is awaited. The stop
        // flag is what tells this drive's previous reader that its channel was completed on
        // purpose, so it returns quietly rather than reading a normal completion as the whole
        // generation ending and completing the merged channel for every other drive too.
        _stopRequestedDrives.Add(driveLetter);
        _armGenerationsByDrive[driveLetter] = _armGenerationsByDrive.GetValueOrDefault(driveLetter) + 1;
        _readersByDrive.Remove(driveLetter, out previousReader);
    }

    // Call the client first, await the previous reader second, never the other way round.
    // SendStartWatchAsync is what completes that reader's channel, through the client's
    // ArmDriveLocked, so awaiting it first would await a task nothing has told to finish.
    // CancellationToken.None for the same reason StartWatching uses it: cancelling this write
    // can leave the acknowledgement to terminate the next watch.
    await client.SendStartWatchAsync(new Dictionary<string, UsnJournalCursor>
    {
        [JournalBrokerClient.NormalizeDriveLetter(driveLetter.ToString())] =
            new(target.JournalId, target.NextUsn)
    }, CancellationToken.None).ConfigureAwait(false);

    if (previousReader is not null)
    {
        await previousReader.WaitAsync(cancellationToken).ConfigureAwait(false);
    }

    lock (_streamLock)
    {
        _stopRequestedDrives.Remove(driveLetter);
        StartDriveReaderLocked(target, driveLetter);
    }
}
```

`DisarmDriveAsync` is the same three phases with `SendDisarmDriveAsync` in the middle and no fresh reader at the end: claim the stop flag, increment the generation, take the reader out of the map, call the client (which completes that drive's channel before the frame reaches the wire), then await the reader bounded by the caller's token. The stop flag stays set, because the drive is not armed until an arm clears it. Both throw `InvalidOperationException` when no stream is running, before writing anything. `StartDriveReaderLocked` reads the drive's current generation, starts `ReadDriveAsync` with it against the stored batch source, writer, and reader token, and records the task in `_readersByDrive`. A failure or cancellation while retiring the old reader leaves that drive stopped and surfaces to `RescanAsync`, which is already where a failed re-arm is contracted to appear.

Change `BrokerMftBlockProducer.CreateWatchSource` to `public IIndexWatchSource CreateWatchSource()` returning `new BrokerIndexWatchSource(connectAsync)` directly, since the class is now the seam rather than a factory for a delegate.

- [ ] **Step 4: Run to verify they pass, then the full suite on both platforms**

```bash
dotnet test MFTLib.Tests/MFTLib.Tests.csproj -p:Platform=x64 --filter "TestCategory!=RequiresAdmin"
bash scripts/coverage-linux.sh
aislop scan .
```

Expected: PASS on both. Everything this task adds is Linux-runnable; nothing here needs Windows, a volume, or a real journal.

- [ ] **Step 5: Commit**

```bash
git add MFTLib/Index MFTLib/Broker/Client MFTLib.Tests
git commit -m "feat(index): isolate a watch fault to the drive that raised it"
```

---

## Phase 5: Change fidelity

### Task 8: FileChange carries its own paths

**Files:**
- Modify: `MFTLib/Index/FileChange.cs`, `MFTLib/Index/JournalMutator.cs`
- Create: `MFTLib.Tests/Index/MutatorFixture.cs`
- Test: `MFTLib.Tests/Index/JournalMutatorTests.cs`

**Interfaces:**
- Consumes: `IndexNavigation.BuildPath(Snapshot, ushort, uint)`.
- Produces: `public sealed record FileChange(FileChangeKind Kind, FileEntry Entry, string Path, string? PreviousPath = null);`

**Design notes:** `FileChange` is `(Kind, Entry, PreviousName)` today. `Entry` is a live handle and `Changed` is raised only after the whole batch has mutated the block (`MFTLib/Index/FileIndex.Watch.cs:224-230`, `RaiseChanged`), so a handler that reads `Entry.Path` sees the effect of later entries in the same batch. Each change therefore captures its own path at its own mutation. `PreviousName` becomes `PreviousPath` and carries the full old path, because a move between two directories loses the old location entirely under a bare name, which is exactly what git-wizard's repository classifier needs.

Capture order matters: build the path after a create's row write, before a delete's tombstone (the tombstone keeps the name and parent, but capturing first keeps the rule uniform), and for a rename build the old path before `TryRenameRow` and the new path after.

- [ ] **Step 1: Write the failing tests**

Add to `MFTLib.Tests/Index/JournalMutatorTests.cs`:

```csharp
[TestMethod]
public void Rename_CarriesBothTheOldAndTheNewFullPath()
{
    using var fixture = new MutatorFixture();          // MftShaped: row 5 root, row 6 "documents", row 7 "notes.txt"
    var moved = UsnJournalEntry.Create(new UsnJournalEntryOptions
    {
        RecordNumber = 7, ParentRecordNumber = 5, FileName = "renamed.txt",
        Reason = UsnReason.RenameNewName, Timestamp = fixture.Timestamp
    });

    var change = fixture.Apply([moved]).Single();

    Assert.AreEqual(FileChangeKind.Renamed, change.Kind);
    Assert.AreEqual(@"T:\renamed.txt", change.Path);
    Assert.AreEqual(@"T:\documents\notes.txt", change.PreviousPath);
}

[TestMethod]
public void EachChange_KeepsThePathItHadWhenItWasApplied()
{
    using var fixture = new MutatorFixture();
    var created = UsnJournalEntry.Create(new UsnJournalEntryOptions
    {
        RecordNumber = 9, ParentRecordNumber = 6, FileName = "first.txt",
        Reason = UsnReason.FileCreate, Timestamp = fixture.Timestamp
    });
    var renamed = UsnJournalEntry.Create(new UsnJournalEntryOptions
    {
        RecordNumber = 9, ParentRecordNumber = 5, FileName = "second.txt",
        Reason = UsnReason.RenameNewName, Timestamp = fixture.Timestamp
    });

    var changes = fixture.Apply([created, renamed]);

    Assert.AreEqual(@"T:\documents\first.txt", changes[0].Path);
    Assert.AreEqual(@"T:\second.txt", changes[1].Path);
    Assert.AreEqual(@"T:\documents\first.txt", changes[1].PreviousPath);
}

[TestMethod]
public void Delete_CarriesThePathOfTheRowItTombstoned()
{
    using var fixture = new MutatorFixture();
    var deleted = UsnJournalEntry.Create(new UsnJournalEntryOptions
    {
        RecordNumber = 7, ParentRecordNumber = 6, FileName = "notes.txt",
        Reason = UsnReason.FileDelete, Timestamp = fixture.Timestamp
    });

    var change = fixture.Apply([deleted]).Single();

    Assert.AreEqual(FileChangeKind.Deleted, change.Kind);
    Assert.AreEqual(@"T:\documents\notes.txt", change.Path);
    Assert.IsNull(change.PreviousPath);
}
```

Add `MutatorFixture` as a new file, `MFTLib.Tests/Index/MutatorFixture.cs`, next to `SyntheticBlockBuilder.cs` (same folder, same block-building-helper role; not `MFTLib.Tests/TestSupport`, which holds the heavier `FileIndex`-level `WatchHarness`). It wraps `SyntheticBlockBuilder.MftShaped()` in a `Snapshot`/`BlockWriter` pair over a `JournalMutator`, following the pattern already used at `LookupEngineTests.cs:159-179` and `WatchHarness.cs:220-229`, except it opens the block for writing rather than reading, since `Apply` mutates it:

```csharp
namespace MFTLib.Tests.Index;

/// <summary>
///     Wraps a <see cref="SyntheticBlockBuilder.MftShaped" /> block in a
///     <see cref="Snapshot" />/<see cref="BlockWriter" /> pair so a test can apply journal
///     entries directly through a <see cref="JournalMutator" /> without repeating the setup.
/// </summary>
internal sealed class MutatorFixture : IDisposable
{
    readonly SyntheticBlockBuilder _builder;
    readonly Snapshot _snapshot;
    readonly JournalMutator _mutator;
    ulong _nextUsn = 1000;

    public MutatorFixture()
    {
        _builder = SyntheticBlockBuilder.MftShaped();
        var block = _builder.OpenForWriting();
        var driveBlock = new DriveBlock(_builder.DriveLetter, 0, block, deleteFileOnRelease: false);
        _snapshot = Snapshot.Create([driveBlock]);
        _mutator = new JournalMutator(new BlockWriter(block));
    }

    public DateTime Timestamp { get; } = new(2026, 9, 2, 6, 0, 0, DateTimeKind.Utc);

    public IReadOnlyList<FileChange> Apply(IReadOnlyList<UsnJournalEntry> entries)
    {
        return _mutator.Apply(_snapshot, driveOrdinal: 0, entries, journalId: 7, _nextUsn++);
    }

    public void Dispose()
    {
        _snapshot.ReleaseNow();
        _builder.Dispose();
    }
}
```

`Apply` returns the same `IReadOnlyList<FileChange>` that `JournalMutator.Apply` returns, which is why the three tests above call `.Single()` or index into it directly, and why `fixture.Timestamp` is just this fixture's fixed `DateTime`. The second test is the one that fails today for a reason other than compilation once `Path` exists, because `changes[0].Entry.Path` after the batch reads `T:\second.txt`. This new file adds about 35 lines on its own; `JournalMutatorTests.cs` only grows by the three test methods above, about 65 lines, landing near 353 lines, still under the 400-line ceiling.

- [ ] **Step 2: Run to verify they fail**

```bash
dotnet test MFTLib.Tests/MFTLib.Tests.csproj -p:Platform=x64 --filter "FullyQualifiedName~JournalMutatorTests"
```

Expected: FAIL to compile, `FileChange` has no `Path`.

- [ ] **Step 3: Implement**

`FileChange` becomes:

```csharp
/// <summary>
///     One applied journal change. <paramref name="Entry" /> is a live handle whose values are
///     current, not frozen, so its own <c>Path</c> reflects every later entry in the same batch.
///     <paramref name="Path" /> is the path this change happened at, captured at the mutation,
///     and <paramref name="PreviousPath" /> is the full path a rename or move came from.
/// </summary>
public sealed record FileChange(FileChangeKind Kind, FileEntry Entry, string Path, string? PreviousPath = null);
```

In `JournalMutator`, thread the path capture through each handler. `ApplyRename` becomes:

```csharp
FileChange? ApplyRename(Snapshot snapshot, ushort driveOrdinal, UsnJournalEntry entry, uint rowIndex)
{
    var previousPath = IndexNavigation.BuildPath(snapshot, driveOrdinal, rowIndex);
    if (!Writer.TryRenameRow(rowIndex, entry.FileName, (uint)entry.ParentRecordNumber))
    {
        return null;
    }

    Writer.Block.Rows[(int)rowIndex].ModifiedTicks = entry.Timestamp.Ticks;
    return new FileChange(FileChangeKind.Renamed, FileEntry.Create(snapshot, driveOrdinal, rowIndex),
        IndexNavigation.BuildPath(snapshot, driveOrdinal, rowIndex), previousPath);
}
```

The delete arm in `ApplyOne` captures the path before `MarkTombstone`. `ApplyCreate` and `ApplyModification` build theirs after their write. Delete the now-unused `NamePool.ReadRowName` call in `ApplyRename`; the old name is inside the old path.

- [ ] **Step 4: Run to verify they pass, then the full suite on both platforms**

```bash
dotnet test MFTLib.Tests/MFTLib.Tests.csproj -p:Platform=x64 --filter "TestCategory!=RequiresAdmin"
bash scripts/coverage-linux.sh
```

Expected: PASS. Every existing assertion on `FileChange.PreviousName` is rewritten to `PreviousPath` in this commit.

- [ ] **Step 5: Commit**

```bash
git add MFTLib/Index/FileChange.cs MFTLib/Index/JournalMutator.cs MFTLib.Tests/Index
git commit -m "feat(index): every change carries the path it happened at"
```

---

### Task 9: Hydrate rows a producer filter excluded

**Files:**
- Modify: `MFTLib/Index/JournalMutator.cs`
- Modify: `MFTLib.Tests/Index/MutatorFixture.cs`
- Create: `MFTLib.Tests/Index/JournalMutatorHydrationTests.cs`

**Interfaces:**
- Consumes: `BlockWriter.TryWriteRow`, `RowColumns` including Task 2's sequence component, `MutatorFixture.Apply`/`Timestamp` (Task 8).
- Produces: `MutatorFixture.Block`, a read-only test-fixture accessor; no new public member on any production type.

**Design notes:** Under `BrokerScanProfile.DirectoryIndex` the cold writer skips every record that is not a directory and not a named keep-file (`MFTLib/Broker/Host/MftBlockRowWriter.cs:66-68`), so an ordinary file inside a repository has no row. `ApplyModification` then returns null for it (`MFTLib/Index/JournalMutator.cs:134-137`), so editing a pre-existing file produces no `Changed` event at all, and deleting one tombstones an empty slot whose path is unbuildable. git-wizard's watch path classifies exactly those events.

The fix is to initialize an unpopulated slot from the journal entry itself before handling the modification, rename, or delete. A hydration caused by a modification still reports `Modified`, and a hydration caused by a delete still reports `Deleted`: the event kind the caller was going to raise is unaffected in both cases, because the row genuinely existed at that moment, hydrated or not. A hydration caused by a rename is different: the index never had a previous path for that row, so reporting `Renamed` with `Path` equal to `PreviousPath` would mislead a consumer into reading it as a move. When the rename arm hydrates a row that was not already in use, the change is reported as `FileChangeKind.Created` instead, with `PreviousPath` left null; a rename of an already-live row is unaffected and still reports `Renamed` with its real previous path. The spec's decision that `DirectoryIndex` is a producer-side filter is untouched; this is about what the index does with a record it later observes.

The parent guard mirrors the cold writer's reasoning, not its mechanics: an out-of-range parent would point the row at an unrelated record, so hydration marks compaction needed and drops the entry rather than writing a wrong parent, the same signal `ApplyOne`'s own out-of-range-record-number guard already raises. `MftBlockRowWriter.TryWriteRecord`'s own out-of-range-parent guard (`MFTLib/Broker/Host/MftBlockRowWriter.cs:76-79`) just returns false and lets the caller count it as skipped, since that path never touches a `BlockWriter` to mark.

Size is unknown for a hydrated row: the USN record carries no size, so `RowFlags.SizeUnknown` is set for non-directories, matching `ApplyCreate`.

Task 8's `MutatorFixture` exposes only `Apply` and `Timestamp`. Two of this task's tests need to read the block directly: one asserts the sequence number a hydrated row was written with, the other asserts the compaction-needed flag. Add one read-only property to `MFTLib.Tests/Index/MutatorFixture.cs`:

```csharp
public BlockFile Block => _mutator.Writer.Block;
```

`BlockWriter.Writer` and `BlockFile` are already public, so this exposes nothing that was not already reachable through the fixture's own mutator; it is a read seam, not a new capability.

- [ ] **Step 1: Write the failing tests**

Create `MFTLib.Tests/Index/JournalMutatorHydrationTests.cs`:

```csharp
using MFTLib.Index;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace MFTLib.Tests.Index;

[TestClass]
public class JournalMutatorHydrationTests
{
    [TestMethod]
    public void Modification_HydratesAnUnwrittenRowAndStillReportsModified()
    {
        using var fixture = new MutatorFixture();          // row 20 was never written by the producer
        var edited = UsnJournalEntry.Create(new UsnJournalEntryOptions
        {
            RecordNumber = 20, ParentRecordNumber = 6, FileName = "source.cs",
            Reason = UsnReason.DataOverwrite, FileAttributes = FileAttributes.Archive,
            SequenceNumber = 3, Usn = 1000, Timestamp = fixture.Timestamp
        });

        var change = fixture.Apply([edited]).Single();

        Assert.AreEqual(FileChangeKind.Modified, change.Kind);
        Assert.AreEqual(@"T:\documents\source.cs", change.Path);
        Assert.IsTrue(change.Entry.SizeKnown == false);
        Assert.AreEqual((ushort)3, fixture.Block.SequenceNumbers[20]);
    }

    [TestMethod]
    public void Delete_HydratesAnUnwrittenRowSoTheTombstoneKeepsItsPath()
    {
        using var fixture = new MutatorFixture();
        var deleted = UsnJournalEntry.Create(new UsnJournalEntryOptions
        {
            RecordNumber = 21, ParentRecordNumber = 6, FileName = "gone.cs",
            Reason = UsnReason.FileDelete, Usn = 1000, Timestamp = fixture.Timestamp
        });

        var change = fixture.Apply([deleted]).Single();

        Assert.AreEqual(FileChangeKind.Deleted, change.Kind);
        Assert.AreEqual(@"T:\documents\gone.cs", change.Path);
        Assert.IsTrue(change.Entry.IsDeleted);
    }

    [TestMethod]
    public void Rename_HydratesAnUnwrittenRowAndReportsCreatedWithNoPreviousPath()
    {
        using var fixture = new MutatorFixture();
        var renamed = UsnJournalEntry.Create(new UsnJournalEntryOptions
        {
            RecordNumber = 23, ParentRecordNumber = 6, FileName = "surfaced.cs",
            Reason = UsnReason.RenameNewName, Usn = 1000, Timestamp = fixture.Timestamp
        });

        var change = fixture.Apply([renamed]).Single();

        Assert.AreEqual(FileChangeKind.Created, change.Kind);
        Assert.AreEqual(@"T:\documents\surfaced.cs", change.Path);
        Assert.IsNull(change.PreviousPath);
    }

    [TestMethod]
    public void Hydration_RefusesAnOutOfRangeParentAndMarksCompactionNeeded()
    {
        using var fixture = new MutatorFixture();
        var bad = UsnJournalEntry.Create(new UsnJournalEntryOptions
        {
            RecordNumber = 22, ParentRecordNumber = (ulong)uint.MaxValue + 1, FileName = "orphan.cs",
            Reason = UsnReason.DataOverwrite, Usn = 1000, Timestamp = fixture.Timestamp
        });

        Assert.AreEqual(0, fixture.Apply([bad]).Count);
        Assert.IsTrue(fixture.Block.Header.IsCompactionNeeded);
    }

    [TestMethod]
    public void Modification_OfALiveRowDoesNotRewriteItsName()
    {
        using var fixture = new MutatorFixture();
        var edited = UsnJournalEntry.Create(new UsnJournalEntryOptions
        {
            RecordNumber = 7, ParentRecordNumber = 6, FileName = "different-name.txt",
            Reason = UsnReason.DataOverwrite, Usn = 1000, Timestamp = fixture.Timestamp
        });

        var change = fixture.Apply([edited]).Single();

        Assert.AreEqual("notes.txt", change.Entry.Name);
    }
}
```

The third test pins the rename ruling: a rename landing on a row the index never had is reported as a create with no previous path, not as a rename with a misleading one. The last test pins the boundary: hydration applies only to a row that is not in use, and a live row's name still comes from the producer.

- [ ] **Step 2: Run to verify they fail**

```bash
dotnet test MFTLib.Tests/MFTLib.Tests.csproj -p:Platform=x64 --filter "FullyQualifiedName~JournalMutatorHydrationTests"
```

Expected: the first four FAIL. `Modification_HydratesAnUnwrittenRow...` fails because `Apply` returns an empty list; `Delete_Hydrates...` fails on the path assertion; `Rename_Hydrates...` fails because nothing hydrates the row before the rename arm runs today, so it does not report `Created` with a null `PreviousPath`; the parent-guard test fails because nothing marks compaction. The fifth already passes and stays as a guard.

- [ ] **Step 3: Implement**

Add to `JournalMutator`:

```csharp
/// <summary>
///     Fills a slot a producer filter never wrote, from the journal entry that just referred
///     to it. <see cref="BrokerScanProfile.DirectoryIndex" /> keeps directories and named
///     files only, so an ordinary file inside a watched tree has no row until it is observed;
///     without this, a modification to it produces no change at all and a delete tombstones an
///     empty slot with no name to report. <paramref name="hydrated" /> tells the caller whether
///     this call is what populated the row, which only the rename arm needs: a rename of a row
///     the index never had is reported as a create, not a rename, because there is no real
///     previous path to give it.
/// </summary>
bool TryHydrateRow(UsnJournalEntry entry, uint rowIndex, out bool hydrated)
{
    ref var row = ref Writer.Block.Rows[(int)rowIndex];
    if (row.IsInUse)
    {
        hydrated = false;
        return true;
    }

    if (entry.ParentRecordNumber > uint.MaxValue)
    {
        Writer.MarkCompactionNeeded();
        hydrated = false;
        return false;
    }

    var flags = RowFlags.InUse;
    if ((entry.FileAttributes & FileAttributes.Directory) != 0)
    {
        flags |= RowFlags.Directory;
    }
    else
    {
        flags |= RowFlags.SizeUnknown;
    }

    var columns = new RowColumns((uint)entry.ParentRecordNumber, flags, (uint)entry.FileAttributes,
        Size: 0, entry.Timestamp.Ticks, entry.SequenceNumber);
    hydrated = true;
    return Writer.TryWriteRow(rowIndex, entry.FileName, in columns);
}
```

In `ApplyOne`'s delete arm, call `TryHydrateRow(entry, rowIndex, out _)` before building the deleted row's path, returning null when it fails. In the rename arm, call it before `ApplyRename`, returning null when it fails; when it succeeds with `hydrated` true, skip `ApplyRename` and return `new FileChange(FileChangeKind.Created, FileEntry.Create(snapshot, driveOrdinal, rowIndex), IndexNavigation.BuildPath(snapshot, driveOrdinal, rowIndex))` instead, since hydration already wrote the full row and this is `ApplyCreate`'s return shape without its own write; when `hydrated` is false, call `ApplyRename` as before. In `ApplyModification`, replace the `if (!row.IsInUse) { return null; }` early return with a `TryHydrateRow(entry, rowIndex, out _)` call placed before the `ref var row` binding, since hydration writes through the same row and the `hydrated` flag does not change anything there: a hydrated modification still reports `Modified` either way. Keep the `meaningful == UsnReason.None` early return ahead of hydration: a bare `Close` should not create a row.

`ApplyCreate` keeps its own path and does not call the helper; a create already writes the full row and must overwrite a stale one.

- [ ] **Step 4: Run to verify they pass, then the full suite on both platforms**

```bash
dotnet test MFTLib.Tests/MFTLib.Tests.csproj -p:Platform=x64 --filter "TestCategory!=RequiresAdmin"
bash scripts/coverage-linux.sh
```

Expected: PASS. `MFTLib/Index/JournalMutator.cs` (146 lines today) should land near 195 lines; if it passes 400, split the hydration helper into its own file. `MFTLib.Tests/Index/JournalMutatorHydrationTests.cs` is a new file of about 85 lines, well under the 400-line ceiling; `MFTLib.Tests/Index/JournalMutatorTests.cs` (368 lines today) is untouched by this task.

- [ ] **Step 5: Commit**

```bash
git add MFTLib/Index/JournalMutator.cs MFTLib.Tests/Index/MutatorFixture.cs MFTLib.Tests/Index/JournalMutatorHydrationTests.cs
git commit -m "feat(index): hydrate rows a DirectoryIndex scan filtered out"
```

---

## Phase 6: The remaining consumer surface

### Task 10: No-cache blocks die with the process

**Files:**
- Modify: `MFTLib/Index/BlockFile.cs`, `MFTLib/Index/NamedBlockSection.cs`, `MFTLib/Index/DriveBlock.cs`, `MFTLib/Index/FileIndex.Scanning.cs`, `MFTLib/Index/FileIndex.ScanCleanup.cs`, `MFTLib/Index/FileIndexOptions.cs`
- Modify (drop the `deleteFileOnRelease:` argument from `new DriveBlock(...)`; this is the exhaustive list, the compiler finds nothing beyond it): `MFTLib.Tests/Index/AggregateEngineTests.cs` (lines 36, 74, 146, 178, 220, 221), `MFTLib.Tests/Index/CapacityExhaustionTests.cs` (line 49), `MFTLib.Tests/Index/DriveBlockTests.cs` (lines 24, 40, 71, 88, 102), `MFTLib.Tests/Index/DuplicateNameFinderTests.cs` (lines 39, 90), `MFTLib.Tests/Index/EnumerationProducerTests.cs` (line 69), `MFTLib.Tests/Index/FileEntryTests.cs` (lines 37, 107, 155), `MFTLib.Tests/Index/IndexNavigationTests.cs` (lines 35, 116, 140, 165, 189, 217), `MFTLib.Tests/Index/JournalMutatorTests.cs` (line 41), `MFTLib.Tests/Index/LookupEngineTests.cs` (lines 37, 38, 142, 162, 175), `MFTLib.Tests/Index/MutatorFixture.cs` (line 21), `MFTLib.Tests/Index/RowScannerTests.cs` (line 29), `MFTLib.Tests/Index/SearchEngineTests.cs` (lines 35, 119, 174), `MFTLib.Tests/Index/SnapshotSwapTests.cs` (line 27), `MFTLib.Tests/Index/SnapshotTests.cs` (line 14)
- Modify (delete one test outright, name given in Step 3): `MFTLib.Tests/Index/DriveBlockTests.cs` (also covered above for its other four call sites), `MFTLib.Tests/Index/FileIndexResilienceTests.cs`
- Modify (add the new `FileOptions` argument to three existing `BlockFile.OpenMapping(...)` calls): `MFTLib.Tests/Index/BlockFileTests.cs` (lines 229, 252, 281)
- Test: `MFTLib.Tests/Index/NoCacheLifetimeTests.cs`

**Interfaces:**
- Consumes: nothing new.
- Produces: `BlockFile.OpenMapping` gains a required `FileOptions fileOptions` parameter with no default; `DriveBlock`'s `deleteFileOnRelease` constructor parameter is deleted, no default, no overload.

**Design notes:** Spec section 5.1 requires no-cache blocks to be created with delete-on-close so the OS removes them when the process dies. Today `BlockFile.Dispose` (`MFTLib/Index/BlockFile.cs:183-215`) guards a delete with `if (!DeleteOnClose) { return; }` at lines 200-203, then calls `File.Delete(Path)` at line 207 inside the `try` at lines 205-214. This only runs on an orderly dispose. `FileIndexOptions.NoCache`'s doc comment documents that weaker contract explicitly, and `AddDriveAsync` (`MFTLib/Index/FileIndex.Scanning.cs:40-43`) calls `CleanupStaleNoCacheBlocks` to sweep up afterward. That method is not defined in `FileIndex.Scanning.cs`; it lives in `MFTLib/Index/FileIndex.ScanCleanup.cs:26-48`, alongside `CleanupRetiredSiblings` (lines 5-23, stays) and the shared `TryDeleteBestEffort` (lines 51-64, stays - both remaining cleanup paths call it). All three of the managed delete, the doc comment's weaker claim, and the sweep go away together.

`MemoryMappedFile.CreateFromFile(..., leaveOpen: false)` disposes the backing `FileStream` when the mapping is disposed, so the delete-on-close handle closes at the right moment, and Windows defers the actual removal until the last section reference drops. `FileShare.Delete` is already on both creation paths, which is what lets a rescan rename the old file aside while it is still mapped. On Linux, `FileStream` emulates `FileOptions.DeleteOnClose` by unlinking the path when the handle closes, matching POSIX unlink-while-open semantics, so the file-removal-on-dispose contract this task tests is not Windows-only. Deletion surviving an unhandled process kill specifically is a property of the OS handle, not something this task spawns a child process to prove; it is documented here and left to the OS guarantee rather than exercised end to end.

`DriveBlock`'s `deleteFileOnRelease` exists only for no-cache blocks: `MFTLib/Index/DriveBlock.cs:92` documents that a renamed-aside block always passes `false` and uses the `deleteOverride` instead, and `DriveBlock.cs:147` reads `if (!_deleteFileOnRelease && deleteOverride is null)`. With the OS owning no-cache deletion, the parameter goes and line 147 becomes `if (deleteOverride is null)`.

**Trace every `DriveBlock` construction and every `Release` call site before deleting, and then delete.** This is not a survey with two outcomes. If a call site turns out to depend on the parameter's behavior, that call site is rewritten to say what it actually wants; the parameter is not kept and not given a default. Nothing in this repository has ever shipped, so there is no caller whose expectations have to be preserved. `deleteFileOnRelease` has no default, so every construction site in the Files list above fails to compile until its argument is dropped; that list is exhaustive as of this task's own audit and the compiler confirms it after the edit.

`MFTLib.Tests/Index/BlockFileTests.cs:153-176` (`DeleteOnClose_RemovesTheFileWhenDisposed`) already asserts that a `BlockFile.Create`d block with `DeleteOnClose = true` has no file left on disk once disposed, and needs no change: that assertion is externally identical whether the old managed `File.Delete` or the new `FileOptions.DeleteOnClose` produced it, and Step 3 deletes the former outright, so this existing test staying green is itself proof the flag did the work. The new `NoCacheLifetimeTests.cs` exercises the same contract one layer lower, directly against `BlockFile.OpenMapping`'s new parameter, and ties its temp path to the `mftlib-nocache-` naming convention `FileIndex.Scanning.cs`'s `ComputeScanBlockPath` actually uses.

- [ ] **Step 1: Write the new tests**

Create `MFTLib.Tests/Index/NoCacheLifetimeTests.cs`:

```csharp
[TestClass]
public class NoCacheLifetimeTests
{
    [TestMethod]
    public void NoCacheBlock_FileIsGoneOnceTheMappingIsDisposed()
    {
        var path = Path.Combine(Path.GetTempPath(), $"mftlib-nocache-{Guid.NewGuid():N}-probe.mlix");
        var length = BlockLayout.HeaderRegionBytes;
        var (mappedFile, view) = BlockFile.OpenMapping(path, FileMode.Create, length, length,
            FileOptions.DeleteOnClose);
        Assert.IsTrue(File.Exists(path));

        view.Dispose();
        mappedFile.Dispose();

        Assert.IsFalse(File.Exists(path),
            "FileOptions.DeleteOnClose must remove the file once the mapping and its view are both disposed.");
    }

    [TestMethod]
    public void CacheModeBlock_FileSurvivesTheSameDisposalSequence()
    {
        var path = Path.Combine(Path.GetTempPath(), $"mftlib-cache-{Guid.NewGuid():N}-probe.mlix");
        var length = BlockLayout.HeaderRegionBytes;
        var (mappedFile, view) = BlockFile.OpenMapping(path, FileMode.Create, length, length,
            FileOptions.None);

        view.Dispose();
        mappedFile.Dispose();

        try
        {
            Assert.IsTrue(File.Exists(path), "A cache-mode mapping must not be removed on dispose.");
        }
        finally
        {
            File.Delete(path);
        }
    }
}
```

Both tests run unguarded on Windows and Linux: neither needs `OperatingSystem.IsWindows()` or `Assert.Inconclusive`, since `FileOptions.DeleteOnClose` is emulated on both.

Also delete `MFTLib.Tests/Index/DriveBlockTests.cs:52-64` (`DeleteFileOnRelease_RemovesTheSupersededBlockFile`), which constructs a `DriveBlock` with `deleteFileOnRelease: true` and asserts `Release()` deletes the file - the behavior this task removes - and `MFTLib.Tests/Index/FileIndexResilienceTests.cs:331-352` (`OpenAsync_NoCacheMode_DeletesAPreExistingStaleTempBlockForTheDrive`), which plants a file directly at a `mftlib-nocache-*` path and asserts `FileIndex.OpenAsync(noCache: true)` sweeps it - `CleanupStaleNoCacheBlocks`'s contract exactly, which no longer exists once that method is deleted (a manually planted file was never opened with `DeleteOnClose` by this process, so the OS will not remove it). It is the last test in its file; delete the trailing blank line with it. Leave every other `NoCache`-named test alone: `FileIndexResilienceTests.cs`'s `OpenAsync_NoCacheMode_CancelledMidScan_DeletesTheTempBlockImmediately` and `DisposeAsync_NoCacheMode_DeletesEveryRetiredTempBlock`, and `FileIndexLifetimeTests.cs`'s `OpenAsync_NoCacheMode_LeavesNothingInTheCacheDirectory`, all assert behavior this task does not change and must still pass afterward.

- [ ] **Step 2: Run to verify the build fails**

```powershell
dotnet test MFTLib.Tests\MFTLib.Tests.csproj -p:Platform=x64 --filter "FullyQualifiedName~NoCacheLifetimeTests"
```

Expected: build failure, not a runtime assertion failure - `BlockFile.OpenMapping` does not yet accept a fifth `FileOptions` argument. This is this task's red state, the same as every other task in this plan that adds a required parameter ahead of updating its callers.

- [ ] **Step 3: Implement**

In `MFTLib/Index/BlockFile.cs`, give `OpenMapping` a `FileOptions fileOptions` parameter (five parameters, under the limit) and pass it to the `FileStream` constructor overload that takes `bufferSize` and `FileOptions` (use `bufferSize: 4096`, `FileStream`'s own default, at both call sites this task changes). Every caller passes `options.DeleteOnClose ? FileOptions.DeleteOnClose : FileOptions.None`. In `Dispose` (lines 183-215), delete the `if (!DeleteOnClose) { return; }` block at 200-203 and the `try`/`File.Delete(Path)`/`catch (IOException)` block at 205-214, and rewrite the `DeleteOnClose` property's doc comment to say the OS owns the deletion. Update `BlockFileTests.cs`'s three existing `OpenMapping(...)` calls (lines 229, 252, 281) to pass `FileOptions.None`; none of the three exercises delete-on-close.

In `MFTLib/Index/NamedBlockSection.cs:35`, apply the same conditional `FileOptions` to the `FileMode.Create` stream (`bufferSize: 4096`, matching `BlockFile.cs`).

In `MFTLib/Index/DriveBlock.cs`, delete the `deleteFileOnRelease` parameter and `_deleteFileOnRelease` field, and change line 147 to `if (deleteOverride is null)`. Update the class summary and the line 92 remark, which described a distinction that no longer exists (there is no more `deleteFileOnRelease: false` state to contrast against `ScheduleDeleteAt`).

In `MFTLib/Index/FileIndex.ScanCleanup.cs`, delete `CleanupStaleNoCacheBlocks` (lines 26-48). In `AddDriveAsync` (`MFTLib/Index/FileIndex.Scanning.cs`), the branch at lines 40-47 today is:

```csharp
if (_options.NoCache)
{
    CleanupStaleNoCacheBlocks(drive.DriveLetter, drive.VolumeSerial);
}
else
{
    CleanupRetiredSiblings(drive.DriveLetter, drive.VolumeSerial);
}
```

Collapse it to:

```csharp
if (!_options.NoCache)
{
    CleanupRetiredSiblings(drive.DriveLetter, drive.VolumeSerial);
}
```

Do not call `CleanupRetiredSiblings` unconditionally; a no-cache drive has no cache-directory `.retired-*` siblings to find. Rewrite the comment at lines 35-39 immediately above the branch (it currently describes a no-cache temp block that `DisposeAsync` deletes) to describe only the remaining cache-mode case. Drop the `deleteFileOnRelease:` argument from the three `new DriveBlock(...)` sites at lines 206, 262, and 301.

Rewrite the `NoCache` doc comment in `MFTLib/Index/FileIndexOptions.cs` to describe the real contract: the block is created in the temp directory with `FileOptions.DeleteOnClose`, so the OS removes it when the last handle closes, including when the process is killed rather than disposed.

Drop the `deleteFileOnRelease:` argument at every test-file call site named in this task's Files list.

- [ ] **Step 4: Run to verify it passes, then the full suite on both platforms**

```powershell
MSBuild.exe MFTLib.sln -p:Configuration=Debug -p:Platform=x64
dotnet test MFTLib.Tests\MFTLib.Tests.csproj -p:Platform=x64 --filter "TestCategory!=RequiresAdmin"
```

```bash
bash scripts/coverage-linux.sh
```

Expected: PASS on both platforms; nothing in this task is Windows-only. Every test that asserted a leftover no-cache file gets swept by the next open, or that `DriveBlock.Release()` deletes on a `deleteFileOnRelease: true` flag, is deleted, not adjusted (see Step 1).

File-size check after the edits: `BlockFile.cs` (341 lines before, stays well under 400), `DriveBlock.cs` (168, shrinks), `NamedBlockSection.cs` (115, +1-2), `FileIndex.Scanning.cs` (351, shrinks slightly), `FileIndex.ScanCleanup.cs` (67, shrinks to roughly 44), `FileIndexOptions.cs` (45, doc comment only), `BlockFileTests.cs` (303, +0-3), `DriveBlockTests.cs` (106, shrinks by the deleted test), `FileIndexResilienceTests.cs` (353, shrinks by the deleted test), `NoCacheLifetimeTests.cs` (new, under 50). None crosses 400; no split is needed anywhere in this task.

- [ ] **Step 5: Commit**

```bash
git add MFTLib/Index MFTLib.Tests/Index
git commit -m "feat(index): no-cache blocks use OS delete-on-close"
```

---

### Task 11: MFT scan progress reaches the index consumer

**Files:**
- Modify: `MFTLib/Index/IndexScanProgress.cs`, `MFTLib/Index/EnumerationProducer.cs`, `MFTLib/Broker/Client/BrokerMftBlockProducer.cs`
- Create: `MFTLib/Index/IndexScanPhase.cs`, `MFTLib.Tests/TestSupport/SynchronousProgress.cs`
- Test: `MFTLib.Tests/Index/EnumerationProducerTests.cs`, `MFTLib.Tests/BrokerMftBlockProducerTests.cs`

**Interfaces:**
- Consumes: `BrokerScanProgress`, `BrokerScanPhase`.
- Produces:
  - `public enum IndexScanPhase { Enumerating, ParsingMft, Transferring }`
  - `IndexScanProgress` as an init-property record with `DriveLetter`, `Phase`, `RowsWritten`, `TotalRows`, `CurrentDirectory`

**Design notes:** `BrokerMftBlockProducer.ProduceAsync` never reads `request.Progress`, and its own summary says index progress cannot represent broker phases. That leaves a consumer using `FileIndexOptions.Progress` in silence for the entire cold scan of an NTFS drive, which is the longest operation in the product. Two progress channels with different shapes and one dead property is not an acceptable answer under decision 1.

`IndexScanProgress(uint RowsWritten, string CurrentDirectory)` cannot carry a phase, a total, or a drive, and `CurrentDirectory` is meaningless for an MFT scan, so it is reshaped rather than extended. `BrokerScanPhase.Parsing` maps to `ParsingMft` and `Transferring` to `Transferring`; the enumeration producer reports `Enumerating`.

The producer may already have a `BrokerScanOptions.Progress` from its constructor; compose rather than replace, so a consumer that wants both channels keeps both.

`BrokerMftBlockProducer.cs` lives under `MFTLib/Broker/Client/` but declares `namespace MFTLib;`, not `namespace MFTLib.Broker`; the namespace boundary decision is enforced by folder, not by the literal C# namespace name (code under `MFTLib/Broker/` may reference `MFTLib.Index`), so this file's existing `using MFTLib.Index;` is the allowed direction and stays that way when `BuildProgressAdapter` is added.

- [ ] **Step 1: Write the failing tests**

`MFTLib.Tests/Index/EnumerationProducerTests.cs` already has a private `SynchronousProgress<T>` class, right after `Produce_ReportsProgress`, added for exactly the reason the two new tests below need one: `System.Progress<T>` posts through a captured synchronization context, which in MSTest is the thread pool, so an assertion right after the awaited call that triggered it is racy, not deterministic. Move that class out into a new shared file, `MFTLib.Tests/TestSupport/SynchronousProgress.cs`:

```csharp
namespace MFTLib.Tests.TestSupport;

/// <summary>
///     A synchronous <see cref="IProgress{T}" /> that records every report immediately, unlike
///     <see cref="Progress{T}" />, which posts through a synchronization context and so cannot
///     be asserted on deterministically right after the call that triggered it.
/// </summary>
internal sealed class SynchronousProgress<T>(Action<T> callback) : IProgress<T>
{
    public void Report(T value)
    {
        callback(value);
    }
}
```

Delete the nested `SynchronousProgress<T>` class from `EnumerationProducerTests.cs` and add `using MFTLib.Tests.TestSupport;` to its usings; the existing call
`new SynchronousProgress<IndexScanProgress>(observed.Add)` keeps compiling unchanged, since the type name and constructor shape are the same.

Add to `MFTLib.Tests/BrokerMftBlockProducerTests.cs` (it already has `using MFTLib.Tests.TestSupport;`):

```csharp
[TestMethod]
public async Task ProduceAsync_ForwardsBrokerProgressOntoTheIndexProgressChannel()
{
    await using var harness = new InProcessBlockBrokerHarness();
    var samples = new List<IndexScanProgress>();
    var request = harness.Request with { Progress = new SynchronousProgress<IndexScanProgress>(samples.Add) };
    var producer = new BrokerMftBlockProducer(harness.ConnectAsync);

    var result = await producer.CreateProducer()(request, harness.CancellationToken);
    result.Block.Dispose();

    Assert.IsTrue(samples.Count > 0, "the broker reported no progress to the index channel");
    Assert.IsTrue(samples.All(sample => sample.DriveLetter == 'c'));
    Assert.IsTrue(samples.Any(sample => sample.Phase == IndexScanPhase.Transferring));
}

[TestMethod]
public async Task ProduceAsync_KeepsTheCallersOwnBrokerProgressChannel()
{
    await using var harness = new InProcessBlockBrokerHarness();
    var brokerSamples = new List<BrokerScanProgress>();
    var producer = new BrokerMftBlockProducer(harness.ConnectAsync,
        new BrokerScanOptions { Progress = new SynchronousProgress<BrokerScanProgress>(brokerSamples.Add) });

    var result = await producer.CreateProducer()(harness.Request, harness.CancellationToken);
    result.Block.Dispose();

    Assert.IsTrue(brokerSamples.Count > 0);
}
```

Do not use `System.Progress<T>` here: it posts to the captured synchronization context, which in MSTest is the thread pool, so the collected list would not reliably be populated by the time the assertion runs. `SynchronousProgress<T>` runs its callback on the calling thread instead. Do not add a sleep or an elapsed-time assertion to wait for samples either.

Update `MFTLib.Tests/Index/EnumerationProducerTests.cs`'s `Produce_ReportsProgress` test to assert the new shape, against its existing `observed` list: `Assert.AreEqual(IndexScanPhase.Enumerating, observed[0].Phase);` and `Assert.AreEqual('T', observed[0].DriveLetter);`.

In the same file, the top-level `RecordingProgress` class (declared after `EnumerationProducerTests` closes) has `public List<string> Directories { get; } = [];` and `Directories.Add(value.CurrentDirectory);`. Change `Directories` to `List<string?>` in the same commit, matching the new `string?` type of `IndexScanProgress.CurrentDirectory`; leaving it as `List<string>` is a nullable-reference warning under this project's `<Nullable>enable</Nullable>`.

- [ ] **Step 2: Run to verify they fail**

```bash
dotnet test MFTLib.Tests/MFTLib.Tests.csproj -p:Platform=x64 --filter "FullyQualifiedName~BrokerMftBlockProducerTests|FullyQualifiedName~EnumerationProducerTests"
```

Expected: FAIL to compile, `IndexScanProgress` has no `Phase`.

- [ ] **Step 3: Implement**

Create `MFTLib/Index/IndexScanPhase.cs` with the three members and a doc comment per member naming which producer emits it.

Rewrite `MFTLib/Index/IndexScanProgress.cs`:

```csharp
namespace MFTLib.Index;

/// <summary>
///     One progress sample from whichever producer is building a drive's block.
///     <see cref="TotalRows" /> is null while the producer does not yet know the total, which
///     an enumeration walk never does. <see cref="CurrentDirectory" /> is null for an MFT scan,
///     which has no notion of a current directory.
/// </summary>
public sealed record IndexScanProgress
{
    public required char DriveLetter { get; init; }

    public required IndexScanPhase Phase { get; init; }

    public required uint RowsWritten { get; init; }

    public uint? TotalRows { get; init; }

    public string? CurrentDirectory { get; init; }
}
```

Update `EnumerationProducer` to build the new shape with `Phase = IndexScanPhase.Enumerating` and the drive letter it already has in `EnumerationProducerOptions`.

In `BrokerMftBlockProducer.ProduceAsync`, build the adapter before the scan:

```csharp
var brokerProgress = BuildProgressAdapter(request, scanOptions?.Progress);
var options = (scanOptions ?? new BrokerScanOptions()) with
{
    BlockTargets = ...,
    Progress = brokerProgress
};
```

`BuildProgressAdapter` returns null when both `request.Progress` and the caller's progress are null, and otherwise an `IProgress<BrokerScanProgress>` that forwards to the caller's channel and reports

```csharp
new IndexScanProgress
{
    DriveLetter = request.DriveLetter,
    Phase = sample.Phase == BrokerScanPhase.Parsing ? IndexScanPhase.ParsingMft : IndexScanPhase.Transferring,
    RowsWritten = (uint)Math.Min(sample.RecordsProcessed, uint.MaxValue),
    TotalRows = sample.TotalRecords is { } total ? (uint)Math.Min(total, uint.MaxValue) : null
}
```

to `request.Progress`. Delete the sentence in the class summary that told callers index progress cannot represent broker phases.

- [ ] **Step 4: Run to verify they pass, then the full suite on both platforms**

```bash
dotnet test MFTLib.Tests/MFTLib.Tests.csproj -p:Platform=x64 --filter "TestCategory!=RequiresAdmin"
bash scripts/coverage-linux.sh
```

Expected: PASS on both.

- [ ] **Step 5: Commit**

```bash
git add MFTLib/Index MFTLib/Broker/Client/BrokerMftBlockProducer.cs MFTLib.Tests
git commit -m "feat(index): forward MFT scan progress to the index progress channel"
```

---

### Task 12: Open an MFT entry by file id

**Files:**
- Create: `MFTLib/Index/WindowsFileById.cs`, `MFTLib.Tests/Index/FileEntryOpenTests.cs`
- Modify: `MFTLib/Index/FileEntry.Open.cs`; `MFTLib/Index/DriveBlock.cs` (the `RootDirectoryPath` doc comment only - no behavior change, see Design notes); `MFTLib.Tests/Index/FileEntryTests.cs` (rewrite `Open_ProducerKindIsNotEnumeration_ThrowsNotSupported`, lines 145-166, to the new contract - see Step 1)
- Test: `MFTLib.Tests/Index/WindowsFileByIdTests.cs`

**Interfaces:**
- Consumes: `BlockFile.SequenceNumbers` from Task 2.
- Produces:
  - `public static class WindowsFileById` with `[SupportedOSPlatform("windows")] public static FileStream Open(string anyPathOnVolume, uint recordNumber, ushort sequenceNumber, FileAccess access)`
  - On `FileEntry` (`FileEntry.Open.cs`): a private `static Func<string, uint, ushort, FileAccess, FileStream> _openById` seam, and `internal static IDisposable OverrideOpenByIdForTest(Func<string, uint, ushort, FileAccess, FileStream> replacement)` restoring the previous delegate on `Dispose` - the same internal, `IDisposable`-returning override shape Task 13 uses for `IndexedDrive.OverrideVolumeSerialReaderForTest`.

**Design notes:** `FileEntry.Open` throws `NotSupportedException` for every non-enumeration producer today (`MFTLib/Index/FileEntry.Open.cs:13-17`, unchanged since Task 2), and spec section 5.3 names the mechanism: open a directory on the target volume with backup semantics as the reference handle, which needs no elevation, then call `OpenFileById`.

Measured on chonkers on 2026-09-09, unelevated: a `FILE_ID_DESCRIPTOR` of type `FileIdType` carrying `(sequenceNumber << 48) | recordNumber` opens successfully against a `C:\` handle created with `FILE_FLAG_BACKUP_SEMANTICS` and `FILE_READ_ATTRIBUTES`; the same descriptor with the sequence bits zeroed fails with `ERROR_INVALID_PARAMETER`. That is why tasks 2, 4, and 5 exist. `OpenFileById`'s first parameter (`hVolumeHint` per its actual signature) accepts a handle to any file or directory already open on the target volume, not specifically the volume root, so the parameter this task threads through is named `anyPathOnVolume` rather than `volumeRootPath` - every production `DriveBlock.RootDirectoryPath` is a real directory on the drive being indexed regardless of producer kind (`FileIndex.Scanning.cs:200-201`, `256`, `294` all pass `rootDirectoryPath: drive.RootDirectory`), so it always qualifies.

`WindowsFileById.cs` declares its own `CreateFileW`/`OpenFileById` P/Invoke with `DllImport` rather than reusing `MFTLib.Internal.Kernel32`'s existing `CreateFile` wrapper or naming a type under `MFTLib/Interop/`: the namespace rule forbids `MFTLib.Index` from referencing `MFTLib.Internal` or `MFTLib.Interop`, and a locally declared platform invoke inside `MFTLib/Index/` is not a reference to either, so the boundary holds (same reasoning as decision 12 for Task 13's `GetVolumeInformationW`). Use `DllImport`, not `LibraryImport`: this repository has no `LibraryImport` declaration anywhere, `MFTLib/Internal/Kernel32.cs` already establishes the `DllImport` idiom for this exact kind of Windows file-handle interop, and Task 13 resolves the identical choice the same way for `GetVolumeInformationW`.

`FileEntry.Open` cannot itself carry `[SupportedOSPlatform("windows")]` the way Task 13's `IndexedDrive.FromWindowsVolume` does, because its `ProducerKind.Enumeration` branch must keep working on Linux (`scripts/coverage-linux.sh` runs the full managed suite there). A field initializer that points `_openById` straight at the attributed `WindowsFileById.Open` would therefore be an unguarded reference and trip CA1416 ("this call site is reachable on all platforms") under this project's `EnableNETAnalyzers`/`AnalysisLevel=latest-Recommended` settings (`Directory.Build.props:7-8`). Guard it structurally, the same "prefer indirection over suppression" resolution Task 13 reaches for its own CA1416 case: `_openById` defaults to a small unattributed `DefaultOpenById` method that checks `OperatingSystem.IsWindows()` and throws `PlatformNotSupportedException` before calling the attributed `WindowsFileById.Open` - the officially recommended CA1416 fix (guard the call with `OperatingSystem.IsWindows()`), and consistent with this codebase's other resolved CA1416 sites (`MFTLib/Elevation/ElevationUtilities.cs:33`, `Benchmark/BenchmarkRunner.cs:8`) always addressing the warning rather than leaving it. Fall back to `[SuppressMessage("Interoperability", "CA1416", ...)]` on the field only if the guard still leaves something flagged. This also gives the new `PlatformNotSupportedException` branch a real, reachable behavior worth testing on Linux (Step 1), rather than leaving it a dead corner the coverage baseline can't reach.

The tests split along the platform line. The real `OpenFileById` round trip is a Windows-only unit test on `WindowsFileById` that needs no block at all: create a temp file, read its file reference with `GetFileInformationByHandle`, split it, open by id, read the bytes back. Building a block whose slot capacity reaches a real record number would be a quarter-gigabyte file, so `FileEntry.Open`'s own tests drive the `_openById` seam instead and assert only that the row's index and sequence number are what gets passed through, plus that the unguarded default throws off Windows. Together they cover the contract; separately none of them does.

`MFTLib.Tests/Index/FileEntryTests.cs:145-166`, `Open_ProducerKindIsNotEnumeration_ThrowsNotSupported`, builds an `Mft`-producer block with a `DriveBlock` that has no `rootDirectoryPath` and asserts `entry.Open` throws `NotSupportedException` - exactly the behavior this task removes. Under the new `FileEntry.Open` body that scenario (non-Enumeration producer, no `RootDirectoryPath`) throws `InvalidOperationException` instead, before any P/Invoke runs, so this must be rewritten in the same commit or it goes from green to red the moment Step 3 lands. `FileEntryTests.cs:168-175`, `Open_NoRootDirectoryConfigured_ThrowsInvalidOperation`, exercises the unrelated Enumeration/no-root path and needs no change. `MFTLib.Tests/Index/FileIndexQueryTests.cs:118`, `Open_ReadsTheRealFileBehindAnEnumerationEntry`, is also unaffected (Enumeration producer, unchanged branch).

`MFTLib/Index/DriveBlock.cs`'s `RootDirectoryPath` doc comment today describes the property purely in enumeration-producer terms ("The real filesystem directory an enumeration producer scanned... every production enumeration block sets it"). After this task, `RootDirectoryPath` is required for `FileEntry.Open` on both producer kinds - resolving a real path for Enumeration entries, resolving a handle on the target volume for MFT entries opened by file id - so the comment needs a one-line update to say so. No other change to `DriveBlock.cs` is needed: every production `DriveBlock` construction already threads `rootDirectoryPath` regardless of producer kind (see above), so nothing about production behavior changes.

- [ ] **Step 1: Write the failing tests**

Create `MFTLib.Tests/Index/WindowsFileByIdTests.cs`:

```csharp
[TestMethod]
public void Open_RoundTripsARealFileThroughItsFileReference()
{
    if (!OperatingSystem.IsWindows())
    {
        Assert.Inconclusive("Windows-only: OpenFileById against a real volume root");
    }

    var path = Path.Combine(Path.GetTempPath(), $"mftlib-openbyid-{Guid.NewGuid():N}.txt");
    File.WriteAllText(path, "round trip");
    try
    {
        var (recordNumber, sequenceNumber) = FileReferenceProbe.Read(path);
        var volumeRoot = Path.GetPathRoot(Path.GetFullPath(path))!;

        using var stream = WindowsFileById.Open(volumeRoot, recordNumber, sequenceNumber, FileAccess.Read);
        using var reader = new StreamReader(stream);

        Assert.AreEqual("round trip", reader.ReadToEnd());
    }
    finally
    {
        File.Delete(path);
    }
}

[TestMethod]
public void Open_ThrowsIOExceptionNamingTheRecordWhenTheSequenceNumberIsWrong()
{
    if (!OperatingSystem.IsWindows())
    {
        Assert.Inconclusive("Windows-only: OpenFileById against a real volume root");
    }

    var path = Path.Combine(Path.GetTempPath(), $"mftlib-openbyid-{Guid.NewGuid():N}.txt");
    File.WriteAllText(path, "x");
    try
    {
        var (recordNumber, sequenceNumber) = FileReferenceProbe.Read(path);
        var volumeRoot = Path.GetPathRoot(Path.GetFullPath(path))!;

        var failure = Assert.ThrowsException<IOException>(
            () => WindowsFileById.Open(volumeRoot, recordNumber, (ushort)(sequenceNumber + 1), FileAccess.Read));

        StringAssert.Contains(failure.Message, recordNumber.ToString());
    }
    finally
    {
        File.Delete(path);
    }
}
```

`FileReferenceProbe` is a Windows-only test helper that opens the path with `FILE_READ_ATTRIBUTES`, calls `GetFileInformationByHandle`, and returns `(uint)(fileIndex & 0xFFFFFFFF)` and `(ushort)(fileIndex >> 48)`. Declare `BY_HANDLE_FILE_INFORMATION` with `Pack = 4` and the three `FILETIME` members as `uint` pairs; the default packing inserts four bytes before the first `long` and yields a garbage file index.

Create `MFTLib.Tests/Index/FileEntryOpenTests.cs` (namespace `MFTLib.Tests.Index`, `[TestClass]`, using directives matching `FileEntryTests.cs`) holding:

```csharp
[TestMethod]
public void Open_OnAnMftEntryPassesTheRowIndexAndItsSequenceNumber()
{
    using var builder = SyntheticBlockBuilder.MftShaped();
    builder.SetSequenceNumber(7, 169);
    using var block = builder.OpenForReading(out _)!;
    var snapshot = Snapshot.Create([new DriveBlock('T', 0, block, rootDirectoryPath: @"T:\")]);
    var entry = LookupEngine.Find(snapshot, @"T:\documents\notes.txt")!.Value;

    (string Root, uint Record, ushort Sequence) captured = default;
    using var restore = FileEntry.OverrideOpenByIdForTest((root, record, sequence, _) =>
    {
        captured = (root, record, sequence);
        return new MemoryBackedFileStream();
    });

    using var stream = entry.Open(FileAccess.Read);

    Assert.AreEqual(@"T:\", captured.Root);
    Assert.AreEqual(7u, captured.Record);
    Assert.AreEqual((ushort)169, captured.Sequence);
}

[TestMethod]
public void Open_OnLinuxWithNoOverride_ThrowsPlatformNotSupported()
{
    if (OperatingSystem.IsWindows())
    {
        Assert.Inconclusive("Linux-only: the unattributed default seam only throws off Windows");
    }

    using var builder = SyntheticBlockBuilder.MftShaped();
    builder.SetSequenceNumber(7, 169);
    using var block = builder.OpenForReading(out _)!;
    var snapshot = Snapshot.Create([new DriveBlock('T', 0, block, rootDirectoryPath: @"T:\")]);
    var entry = LookupEngine.Find(snapshot, @"T:\documents\notes.txt")!.Value;

    Assert.ThrowsException<PlatformNotSupportedException>(() => entry.Open(FileAccess.Read));
}
```

`SyntheticBlockBuilder.SetSequenceNumber` and `FileEntry.OverrideOpenByIdForTest` are added in this task; the override is the coverage seam the first test needs, and the second test is what gives `DefaultOpenById`'s guard clause (Step 3) real, reachable coverage on Linux rather than an untested branch. `MemoryBackedFileStream` can be a `FileStream` over a temp file if a fake is awkward.

In `MFTLib.Tests/Index/FileEntryTests.cs`, rewrite `Open_ProducerKindIsNotEnumeration_ThrowsNotSupported` (lines 145-166) to:

```csharp
[TestMethod]
public void Open_MftProducerNoRootDirectoryConfigured_ThrowsInvalidOperation()
{
    using var builder = new SyntheticBlockBuilder('M');
    var root = builder.AddRoot();
    var fileRow = builder.AddRow("record.dat", root, RowFlags.InUse, 128, FileMoment, sequenceNumber: 0);
    builder.Complete(ScanMoment);
    builder.MutateHeader((ref header) => header.ProducerKind = ProducerKind.Mft);

    var block = builder.OpenForReading(out _)!;
    var driveBlock = new DriveBlock(builder.DriveLetter, 0, block);
    var snapshot = Snapshot.Create([driveBlock]);
    try
    {
        var entry = FileEntry.Create(snapshot, 0, fileRow);
        Assert.ThrowsException<InvalidOperationException>(() => entry.Open(FileAccess.Read));
    }
    finally
    {
        snapshot.ReleaseNow();
    }
}
```

This keeps the same Mft-producer-with-no-root-directory setup and only changes the expected exception and the test's name, matching the new `FileEntry.Open` contract: the `InvalidOperationException` guard runs before any P/Invoke, so this stays a cross-platform test with no `OperatingSystem.IsWindows()` guard of its own, same as before.

- [ ] **Step 2: Run to verify they fail**

```powershell
dotnet test MFTLib.Tests\MFTLib.Tests.csproj -p:Platform=x64 --filter "FullyQualifiedName~WindowsFileByIdTests|FullyQualifiedName~FileEntryOpenTests|FullyQualifiedName~Open_MftProducerNoRootDirectoryConfigured_ThrowsInvalidOperation"
```

Expected: FAIL to compile, there is no `WindowsFileById` and `FileEntryTests.cs` still asserts the old exception type.

- [ ] **Step 3: Implement**

Create `MFTLib/Index/WindowsFileById.cs` with a `DllImport` static class declaring `CreateFileW` and `OpenFileById` (no `CloseHandle` - both handles are `SafeFileHandle`, which releases itself), the `FILE_ID_DESCRIPTOR` layout (`DwSize`, `Type`, `FileId`, plus eight bytes of union padding, 24 bytes total, `DwSize` set from `Marshal.SizeOf`), and:

```csharp
[SupportedOSPlatform("windows")]
public static FileStream Open(string anyPathOnVolume, uint recordNumber, ushort sequenceNumber, FileAccess access)
{
    using var volumeHandle = CreateFileW(anyPathOnVolume, FileReadAttributes, ShareAll, IntPtr.Zero,
        OpenExisting, FileFlagBackupSemantics, IntPtr.Zero);
    if (volumeHandle.IsInvalid)
    {
        throw new IOException(
            $"Could not open '{anyPathOnVolume}' to resolve record {recordNumber}.",
            Marshal.GetHRForLastWin32Error());
    }

    var descriptor = new FileIdDescriptor
    {
        DwSize = (uint)Marshal.SizeOf<FileIdDescriptor>(),
        Type = 0,
        FileId = ((long)sequenceNumber << 48) | recordNumber
    };

    var fileHandle = OpenFileById(volumeHandle, ref descriptor, ToDesiredAccess(access), ShareAll,
        IntPtr.Zero, 0);
    if (fileHandle.IsInvalid)
    {
        throw new IOException(
            $"Could not open record {recordNumber} (sequence {sequenceNumber}) on '{anyPathOnVolume}'.",
            Marshal.GetHRForLastWin32Error());
    }

    return new FileStream(fileHandle, access);
}
```

Both `DllImport` declarations need `SetLastError = true` so `Marshal.GetHRForLastWin32Error()` (an HRESULT, not the raw Win32 code `Marshal.GetLastWin32Error()` returns) reads the right value; call it immediately after the failing call, before anything else can reset it. Use `SafeFileHandle` for both handles so the volume handle is released on every path. The volume handle is opened per call rather than cached: caching it on `DriveBlock` would keep a handle open on a volume the user may want to eject, and the open costs one syscall against the read that follows.

In `MFTLib/Index/FileEntry.Open.cs`, replace the `NotSupportedException` with the MFT branch:

```csharp
public FileStream Open(FileAccess access)
{
    var driveBlock = DriveBlock;
    if (driveBlock.ProducerKind == ProducerKind.Enumeration)
    {
        return new FileStream(ResolveRealPath(driveBlock), FileMode.Open, access,
            FileShare.ReadWrite | FileShare.Delete);
    }

    if (driveBlock.RootDirectoryPath is not { } anyPathOnVolume)
    {
        throw new InvalidOperationException(
            $"Drive block {driveBlock.DriveLetter} has no configured root directory to open by file id.");
    }

    return _openById(anyPathOnVolume, RowIndex, driveBlock.Block.SequenceNumbers[(int)RowIndex], access);
}

static FileStream DefaultOpenById(string anyPathOnVolume, uint recordNumber, ushort sequenceNumber,
    FileAccess access)
{
    if (!OperatingSystem.IsWindows())
    {
        throw new PlatformNotSupportedException(
            "Opening an MFT entry by file id is only supported on Windows.");
    }

    return WindowsFileById.Open(anyPathOnVolume, recordNumber, sequenceNumber, access);
}

static Func<string, uint, ushort, FileAccess, FileStream> _openById = DefaultOpenById;

internal static IDisposable OverrideOpenByIdForTest(Func<string, uint, ushort, FileAccess, FileStream> replacement)
{
    var previous = _openById;
    _openById = replacement;
    return new RestoreOpenById(previous);
}

sealed class RestoreOpenById(Func<string, uint, ushort, FileAccess, FileStream> previous) : IDisposable
{
    public void Dispose()
    {
        _openById = previous;
    }
}
```

`DefaultOpenById` is not itself `[SupportedOSPlatform("windows")]`: its `OperatingSystem.IsWindows()` guard is what makes the call to the attributed `WindowsFileById.Open` reachable without a CA1416 warning (see Design notes), and it is what `_openById` defaults to instead of the attributed method directly. Update the method's doc comment: the route no longer "arrives with the MFT producer", it is here.

In `MFTLib/Index/DriveBlock.cs`, update the `RootDirectoryPath` doc comment to say it is required for `FileEntry.Open` on both producer kinds now - resolving a real path for Enumeration entries, resolving a handle on the target volume for MFT entries opened by file id - not only "an enumeration producer scanned." No other line in `DriveBlock.cs` changes.

- [ ] **Step 4: Run to verify they pass, then the full suite on both platforms**

```powershell
dotnet test MFTLib.Tests\MFTLib.Tests.csproj -p:Platform=x64 --filter "TestCategory!=RequiresAdmin"
```

```bash
bash scripts/coverage-linux.sh
```

Expected: PASS on Windows, with the real round trip and `Open_OnLinuxWithNoOverride_ThrowsPlatformNotSupported` (inconclusive there) green. PASS on Linux, with the two `WindowsFileById` round-trip tests inconclusive, the seam test green, and `Open_OnLinuxWithNoOverride_ThrowsPlatformNotSupported` exercising `DefaultOpenById`'s guard for real.

- [ ] **Step 5: Commit**

```bash
git add MFTLib/Index MFTLib.Tests/Index
git commit -m "feat(index): open an MFT entry through its NTFS file reference"
```

---

### Task 13: IndexedDrive.FromWindowsVolume

**Files:**
- Create: `MFTLib/Index/IndexedDrive.Windows.cs`
- Modify: `MFTLib/Index/IndexedDrive.cs`
- Test: `MFTLib.Tests/Index/IndexedDriveTests.cs`

**Interfaces:**
- Consumes: nothing new.
- Produces: `[SupportedOSPlatform("windows")] public static IndexedDrive FromWindowsVolume(string drive)`

**Design notes:** `IndexedDrive` requires a `uint VolumeSerial` and nothing in the repository obtains one from the OS: `NtfsVolumeInformation` has six geometry fields and no serial, `NtfsVolumeInformation.Query` reads `FSCTL_GET_NTFS_VOLUME_DATA` into a native buffer whose `VolumeSerialNumber` field it never touches, and `JournalBrokerClient.QueryVolumesAsync` reconstructs sizing only. Every serial flowing through the code today is caller-supplied, so a consumer either invents one, which defeats volume-replacement detection, or writes its own interop purely to satisfy MFTLib's cache contract.

`GetVolumeInformationW` returns the serial without elevation and without a volume handle, so the factory needs one call. Per decision 12, the P/Invoke is declared inside `MFTLib/Index/` so the namespace boundary check in Task 15 stays clean; decision 12's reasoning names `LibraryImport` as the mechanism, but this task uses `DllImport` instead, matching the repository's existing P/Invoke style (`MFTLib/Internal/Kernel32.cs`, `MFTLib/Internal/MFTLibNative.cs`). This repository has no `LibraryImport` declaration anywhere, and `LibraryImport` does not support `StringBuilder` marshalling, so `DllImport` is both more consistent and avoids a marshalling dead end on a signature with two unused name buffers. Decision 12's namespace-boundary argument is about where the P/Invoke lives, not which attribute declares it, so it applies unchanged to `DllImport`.

`IndexedDrive.cs` is a one-line record; the factory and its interop go in a partial file so neither grows past its responsibility.

- [ ] **Step 1: Write the failing tests**

Create `MFTLib.Tests/Index/IndexedDriveTests.cs`:

```csharp
[TestMethod]
[DataRow("C")]
[DataRow("C:")]
[DataRow(@"C:\")]
[DataRow("c:/")]
public void FromWindowsVolume_NormalizesEverySpellingToTheSameDrive(string spelling)
{
    using var restore = IndexedDrive.OverrideVolumeSerialReaderForTest(root =>
    {
        Assert.AreEqual(@"C:\", root);
        return 0xDEADBEEF;
    });

    var drive = IndexedDrive.FromWindowsVolume(spelling);

    Assert.AreEqual('C', drive.DriveLetter);
    Assert.AreEqual(@"C:\", drive.RootDirectory);
    Assert.AreEqual(0xDEADBEEFu, drive.VolumeSerial);
}

[TestMethod]
public void FromWindowsVolume_ThrowsIOExceptionNamingTheDriveWhenTheQueryFails()
{
    using var restore = IndexedDrive.OverrideVolumeSerialReaderForTest(_ => null);

    var failure = Assert.ThrowsException<IOException>(() => IndexedDrive.FromWindowsVolume("Q"));

    StringAssert.Contains(failure.Message, "Q");
}

[TestMethod]
[DataRow("")]
[DataRow("CD")]
[DataRow("1")]
[DataRow(@"\\server\share")]
public void FromWindowsVolume_RejectsAnythingThatIsNotADriveLetter(string spelling)
{
    Assert.ThrowsException<ArgumentException>(() => IndexedDrive.FromWindowsVolume(spelling));
}

[TestMethod]
[SupportedOSPlatform("windows")]
public void FromWindowsVolume_ReadsARealSerialForTheSystemDrive()
{
    if (!OperatingSystem.IsWindows())
    {
        Assert.Inconclusive("Windows-only: GetVolumeInformationW against a real drive");
    }

    var systemRoot = Path.GetPathRoot(Environment.SystemDirectory)!;
    var drive = IndexedDrive.FromWindowsVolume(systemRoot);

    Assert.AreNotEqual(0u, drive.VolumeSerial);
    Assert.AreEqual(systemRoot, drive.RootDirectory);
}
```

`FromWindowsVolume_ReadsARealSerialForTheSystemDrive` carries its own `[SupportedOSPlatform("windows")]`, matching the pattern already used at `MFTLib.Tests/JournalBrokerClientTests.ConnectionAndWatchFailures.cs:10-11` and `MFTLib.Tests/Index/NamedBlockSectionTests.cs:36-37`: a test that calls a `[SupportedOSPlatform("windows")]`-attributed production member is itself attributed the same way, in addition to the `Assert.Inconclusive` guard. `FromWindowsVolume_NormalizesEverySpellingToTheSameDrive` and `FromWindowsVolume_ThrowsIOExceptionNamingTheDriveWhenTheQueryFails` run on Linux through the seam; `FromWindowsVolume_RejectsAnythingThatIsNotADriveLetter` runs on Linux too but never reaches the seam at all, since the `ArgumentException` throws during validation before `FromWindowsVolume` ever asks for a serial; only `FromWindowsVolume_ReadsARealSerialForTheSystemDrive` needs Windows.

- [ ] **Step 2: Run to verify they fail**

```bash
dotnet test MFTLib.Tests/MFTLib.Tests.csproj -p:Platform=x64 --filter "FullyQualifiedName~IndexedDriveTests"
```

Expected: FAIL to compile, `IndexedDrive` has no `FromWindowsVolume`.

- [ ] **Step 3: Implement**

Create `MFTLib/Index/IndexedDrive.Windows.cs`:

```csharp
using System.Runtime.InteropServices;
using System.Runtime.Versioning;

namespace MFTLib.Index;

public sealed partial record IndexedDrive
{
    internal static Func<string, uint?>? _volumeSerialReaderOverride;

    /// <summary>
    ///     Builds a drive from a Windows drive letter, reading its real volume serial so a
    ///     re-lettered or replaced volume never matches the wrong cached block. Accepts "C",
    ///     "C:", "C:\", and "c:/"; anything that is not a single drive letter is rejected,
    ///     including UNC roots, which have no serial of this shape.
    /// </summary>
    [SupportedOSPlatform("windows")]
    public static IndexedDrive FromWindowsVolume(string drive)
    {
        ArgumentNullException.ThrowIfNull(drive);
        var trimmed = drive.TrimEnd('\\', '/');
        if (trimmed.Length is not (1 or 2) || !char.IsAsciiLetter(trimmed[0]) ||
            (trimmed.Length == 2 && trimmed[1] != ':'))
        {
            throw new ArgumentException($"'{drive}' is not a Windows drive letter.", nameof(drive));
        }

        var driveLetter = char.ToUpperInvariant(trimmed[0]);
        var rootDirectory = driveLetter + @":\";
        var reader = _volumeSerialReaderOverride ?? ReadVolumeSerial;
        var serial = reader(rootDirectory) ?? throw new IOException(
            $"Could not read the volume serial for drive {driveLetter}.");
        return new IndexedDrive(driveLetter, rootDirectory, serial);
    }

    [SupportedOSPlatform("windows")]
    static uint? ReadVolumeSerial(string rootDirectory)
    {
        return GetVolumeInformationW(rootDirectory, IntPtr.Zero, 0, out var serial, out _, out _, IntPtr.Zero, 0)
            ? serial
            : null;
    }

    [SupportedOSPlatform("windows")]
    [DllImport("kernel32.dll", EntryPoint = "GetVolumeInformationW", SetLastError = true,
        CharSet = CharSet.Unicode, ExactSpelling = true)]
    static extern bool GetVolumeInformationW(
        string lpRootPathName,
        IntPtr lpVolumeNameBuffer,
        uint nVolumeNameSize,
        out uint lpVolumeSerialNumber,
        out uint lpMaximumComponentLength,
        out uint lpFileSystemFlags,
        IntPtr lpFileSystemNameBuffer,
        uint nFileSystemNameSize);

    internal static IDisposable OverrideVolumeSerialReaderForTest(Func<string, uint?> reader)
    {
        var previous = _volumeSerialReaderOverride;
        _volumeSerialReaderOverride = reader;
        return new RestoreVolumeSerialReader(previous);
    }

    sealed class RestoreVolumeSerialReader(Func<string, uint?>? previous) : IDisposable
    {
        public void Dispose()
        {
            _volumeSerialReaderOverride = previous;
        }
    }
}
```

`_volumeSerialReaderOverride` starts `null`, and `FromWindowsVolume` falls back to `ReadVolumeSerial` inline (`_volumeSerialReaderOverride ?? ReadVolumeSerial`) rather than through a static field initializer. This is deliberate: `FromWindowsVolume` already carries `[SupportedOSPlatform("windows")]`, so calling the equally-attributed `ReadVolumeSerial` from inside it needs no runtime guard and raises no CA1416 (the same attribute-propagation pattern `NamedBlockSection.Create` already relies on to call Windows-only APIs with no extra checks). A field initializer assigning `ReadVolumeSerial` directly (`static Func<string, uint?> _volumeSerialReader = ReadVolumeSerial;`) runs unconditionally at type-init time, outside any `[SupportedOSPlatform("windows")]`-attributed context, and does trigger CA1416 under this project's `EnableNETAnalyzers`/`AnalysisLevel=latest-Recommended` settings (`Directory.Build.props:7-8`); this codebase's existing CA1416 sites (`MFTLib/Elevation/ElevationUtilities.cs:33`, `Benchmark/BenchmarkRunner.cs:8`) always resolve it with an explicit `[SuppressMessage]` or `#pragma`, never leave it unaddressed. Prefer this call-site indirection over a suppression; fall back to a `[SuppressMessage("Interoperability", "CA1416", ...)]` on a field only if the analyzer still flags something the indirection cannot satisfy.

`lpVolumeNameBuffer` and `lpFileSystemNameBuffer` are declared `IntPtr` and always passed `IntPtr.Zero` with size `0`; verified against Microsoft Learn's `GetVolumeInformationW` reference, both are `[out, optional]` and the corresponding size parameter "is ignored if the ... buffer is not supplied," so a null buffer with a zero size is documented, legitimate behavior, not an unsafe shortcut. `IntPtr` (not `StringBuilder`) matches this repository's existing convention for an unused optional buffer parameter (`MFTLib/Internal/Kernel32.cs`'s `lpSecurityAttributes`, `lpInBuffer`, `lpOutBuffer`, `hTemplateFile` are all `IntPtr`).

`OverrideVolumeSerialReaderForTest` is `internal`, not `public`: `MFTLib.Tests` is already an `InternalsVisibleTo` friend of `MFTLib` (`MFTLib/MFTLib.csproj:39`), and this codebase's established convention for a test-only seam is to keep it internal rather than add it to the public surface, stated explicitly at `MFTLib/Index/IndexNavigation.cs:132-134` ("Narrow internal surface the test assembly uses to exercise navigation without making the helpers public"); `ElevationUtilities._isWindows` and `Kernel32._createFile`/`_deviceIoControl` are the same shape of seam and are both `internal` too. The `IDisposable`-returning override, restoring the previous delegate on `Dispose`, is what makes the failure branch reachable for the coverage baseline on Linux. Change `MFTLib/Index/IndexedDrive.cs:9` to `public sealed partial record IndexedDrive(char DriveLetter, string RootDirectory, uint VolumeSerial);`.

- [ ] **Step 4: Run to verify they pass, then the full suite on both platforms**

```bash
dotnet test MFTLib.Tests/MFTLib.Tests.csproj -p:Platform=x64 --filter "TestCategory!=RequiresAdmin"
bash scripts/coverage-linux.sh
```

Expected: PASS on both.

- [ ] **Step 5: Commit**

```bash
git add MFTLib/Index MFTLib.Tests/Index
git commit -m "feat(index): read a real volume serial with IndexedDrive.FromWindowsVolume"
```

---

## Phase 7: Documentation and the gate

### Task 14: Documentation

**Files:**
- Modify: `README.md`, `docs/index-format.md`, `docs/broker-integration.md`, `CHANGELOG.md`, `AGENTS.md`
- Modify: any doc comment this branch made untrue

**`AGENTS.md` is not optional.** Its Architecture section states "compact ABI version 4 (`MFT_NATIVE_ABI_VERSION`): 48-byte `MftCompactEntry` rows with int64 size at offset 32 and int64 modified time (FILETIME) at offset 40". Task 4 makes the version and the stride wrong. The same section's `BrokerMftBlockProducer` sentence describes production only and now needs the watch source. `CLAUDE.md` imports `AGENTS.md` and needs no edit of its own.

**Design notes:** Run the docs-update check across the repository rather than only the files listed. This branch changed the block format twice, the native interface once, the producer-policy contract, the no-cache contract, the change event's shape, and the meaning of `StartWatchingAsync`, and every one of those is described in prose somewhere.

- [ ] **Step 1: Format documentation**

`docs/index-format.md`: confirm the header table carries `LiveRowCount` at 88 and `SequenceRegionOffset` at 96, that `HeaderFieldBytes` reads 104, that `FormatVersion` reads 2 with the existing one-line note that a mismatch means discard and rescan, and that the sequence-number region has its own section between the row region and the name pool with its size arithmetic.

- [ ] **Step 2: Broker documentation**

`docs/broker-integration.md`: add the live watch bridge beside the block write path. Say that the index owns the watch, that `BrokerMftBlockProducer.CreateWatchSource()` borrows the same connection the producer used, that the cursor comes from each block's own header and therefore replays the scan window, and that `BrokerScanResult.CatchUpEntries` is deliberately unread.

- [ ] **Step 3: README**

`README.md`: one paragraph on the index owning its live watch and on the two producer policies, saying plainly that a failed MFT scan never falls back to a directory walk and that the enumeration producer is chosen, never inherited. One sentence that no-cache blocks are removed by the operating system when the process ends.

- [ ] **Step 4: Changelog**

`CHANGELOG.md` Unreleased section: the block format and native interface changes, the live watch bridge, per-drive producer failure, `FileChange`'s new shape, `FileEntry.Open` by file id, `IndexedDrive.FromWindowsVolume`, and an explicit breaking-change list naming every deleted member, since the pinned consumers will read it when plans 3 and 4 start.

- [ ] **Step 5: Sweep doc comments and commit**

```bash
grep -rn "not available in this build\|arrives with the MFT producer\|starts nothing\|sequence number stripped\|leaves the temp file behind" MFTLib/ docs/ README.md
```

Expected: no output. Each of those phrases described a contract this branch changed.

```bash
git add README.md docs CHANGELOG.md MFTLib
git commit -m "docs(index): the watch bridge, format version 2, and the new producer contract"
```

---

### Task 15: Coverage and quality gate

**Files:** any file the gate flags.

- [ ] **Step 1: Verify the namespace boundary**

```bash
grep -rn "MFTLib\.Mft\|MFTLib\.Broker\|MFTLib\.Internal\|MFTLib\.Interop" MFTLib/Index/
grep -rln "MftRecord\|MftResult\|JournalBroker\|BrokerFrame\|MftVolume\|MFTLibNative" MFTLib/Index/
```

Expected: no output from either. `WindowsFileById` and `IndexedDrive.Windows` declare their own platform invokes and must not name a type under `MFTLib/Interop/`.

- [ ] **Step 2: Verify no file exceeds the size limit**

```bash
wc -l MFTLib/Index/*.cs MFTLib/Broker/**/*.cs MFTLib/Journal/*.cs MFTLib.Tests/*.cs MFTLib.Tests/Index/*.cs MFTLibNative/mft/*.cpp MFTLibNative/usn/*.cpp MFTLibNative/*.h | awk '$2 != "total" && $1 > 400 {print $1, $2}'
```

Expected: only files that were already over the limit before this branch, which is `MFTLibNative/test/linux_smoke_test.cpp` alone. `MFTLib/Index/FileIndex.Watch.cs`, `MFTLib/Index/FileIndex.Scanning.cs`, `MFTLib/Index/JournalMutator.cs`, and `MFTLib/Broker/Client/JournalBrokerClient.LiveWatch.cs` are the ones this branch grew; split by responsibility, not by line count.

- [ ] **Step 3: Run the full managed suite on Windows**

```powershell
MSBuild.exe MFTLib.sln -p:Configuration=Debug -p:Platform=x64
dotnet test MFTLib.Tests\MFTLib.Tests.csproj -p:Platform=x64 --filter "TestCategory!=RequiresAdmin"
```

- [ ] **Step 4: Run coverage on both platforms**

```powershell
.\scripts\run-coverage.ps1 -NonInteractive
```

```bash
bash scripts/coverage-linux.sh
```

Expected: 100 percent line, branch, method, and full-method coverage on every file this branch touched under `MFTLib/Index/`. The `-NonInteractive` run legitimately settles near 99.82 percent line and 99.08 percent method overall because `MftVolume.GetVolumeHandleForTest` is admin-only; that is not a regression and is not this branch's gap. Nothing added here may appear in the Linux exclusion filter in `scripts/coverage-linux.sh`.

- [ ] **Step 5: Run the native smoke test on Linux**

```bash
cmake --build build/linux && LD_LIBRARY_PATH=build/linux ./build/linux/test/linux_smoke_test
```

Expected: every case passes, including the sequence-number assertions from tasks 4 and 5.

- [ ] **Step 6: Run the aislop gate**

```bash
aislop scan .
aislop ci .
```

Expected: score 100, exit code 0. `ci.failBelow` is 100. Fix the underlying issue; never disable a rule and never edit `.aislop/config.yml`. Two warnings pre-exist this branch in files it does not touch (`MFTLib.Tests/Index/BlockValidationMatrixTests.cs:158` AsyncFixer02 and `MFTLib.Tests/MftVolumeAdminTests.cs:401` IDISP001); if this branch edits either file it also fixes that warning.

- [ ] **Step 7: Confirm the tree is clean**

```bash
git status --porcelain
```

Expected: no output.

- [ ] **Step 8: Commit any gate fixes**

```bash
git add -A
git commit -m "chore(index): coverage and aislop gate pass"
```

---

## Plan completion

When every task is checked, the branch delivers an index that owns its own live USN watch through a substrate-neutral seam, reports a failed drive instead of failing the whole open, never silently starts an unelevated whole-volume walk, hands every change the path it happened at, hydrates the rows a directory-index scan filtered out, forwards MFT scan progress to the index consumer, opens an MFT entry through its real NTFS file reference, reports a live file count, removes no-cache blocks through the operating system, and can build an `IndexedDrive` from a real volume serial. The block format and the native interface are the breaking artifacts; the on-disk block from plan 2's attended run is discarded and rescanned.

Plans 3 and 4 begin from here: the file-wizard and git-wizard ports, which unpin from `32f40b9`. The spec at `docs/superpowers/specs/2026-09-02-packed-index-design.md` stays in place and is deleted when plan 5 consumes it.
