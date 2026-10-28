# Issue 844 Implementation Plan: one MFT scan per volume, every name stored, format 8 names

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or
> superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Every MFT index stores the whole volume with every name in a deduplicated, dual-width name pool
(format 8), retention is deleted, the library selects the producer, and all indexes on one volume in one
process share one scan, one block and one journal watch.

**Architecture:** Six slices, one pull request each (spec section 9). Slice 1 replaces the block's UTF-16 name
pool with interned 12-byte name entries, an in-block chained hash and an ASCII-or-UTF-16 byte pool, with
two-pass queries and lazy read-safety validation. Slice 2 deletes `RetainedFileNames` end to end. Slices 3 to 6
add capacity growth, producer selection with directory views, volume scan sharing, and the re-measurement.

**Tech Stack:** C# / .NET 10, MSTest, memory-mapped block files, MSBuild x64 native DLL (unchanged here).

**Spec:** `docs/superpowers/specs/2026-10-27-issue-844-volume-scan-and-name-encoding-design.md` (approved). The
contract is the spec plus the owner rulings 1 to 12 in the body of MFTLib issue 844 and the name-encoding
consult synthesis comment on that issue. Executors rule against the spec.

**Spike:** the local branch `spike/name-dedup` (four commits on main, tip `8dca9d42`) and its untracked
`SPIKE-REPORT.md`. The orchestrator gives each slice-1 implementer the spike checkout location. The spike is
never merged: code is ported file by file with every correction in spec section 4 applied.

## Orchestrator decisions on the two slice-1 questions

1. **`BlockValidationResult.InvalidNameDescriptor` stays** as the result for the name-region checks the spec
   already lists as eager at open (entry count within `E`, pool used within `P`, bucket count equal to the
   formula for `E`), plus one more constant-cost invariant, `NamePoolUsed >= NameEntryCount` (every published
   entry holds at least one byte). `SyntheticBlockEditor.CorruptNamePool` still produces it and the existing
   tests keep their expectations. Slice 1's `api-decisions.md` task notes that the member now reports eager
   name-region geometry failures only. No spec amendment and no public-surface change. Task 1.4 and Task 1.7
   apply this.
2. **`IndexRowEnumerator` copies share one resource owner**: ASCII names are captured into one reusable buffer
   shared by every struct copy, so advancing one copy invalidates older copies (`InvalidOperationException`
   from their `Current` and `MoveNext`). Slice 1 records this as an `api-decisions.md` ruling (reason: rows are
   already valid only until the enumerator advances, and a per-copy buffer would cost an allocation per copy),
   adds it to the spec's section 1 "Changed" table and to `docs/query-lifetime.md`, in the same PR.

Slices 1 and 2 branch from main before the spec's own pull request merges (it is documentation only); each
rebases onto main once it lands, before opening its PR.

## Spec ambiguities resolved in this plan (no orchestrator action needed unless you disagree)

- **Which section 4 corrections land in slice 1.** Slice 1 ships identity, rename, both exact lookups, the fold
  routine, substring and glob, the pool reservation formula, the bucket formula and the validation split. It also
  parameterizes every layout function by the entry capacity `E` and stores `E` in the header, with creation still
  setting `E = S`, so slice 3 adds only the growth planner and `BlockFileCreateOptions.NameEntryCapacity` and
  does not change the format again.
- **Root key text shrinks from 1,983 to 1,967 UTF-16 units** because the declared header grows from 128 to 160
  bytes (spec section 4). `RootKey.FromDirectory` rejects longer paths. Slice 1 updates the XML comment, the
  ruling text in `api-decisions.md` ("Two key types: VolumeKey and RootKey...", line 396) and `index-format.md`.
- **Where the lazy-read `Diagnostics` line goes.** `NamePool.Read` is static over a `BlockFile` and cannot see
  `FileIndexOptions.Diagnostics`. Slice 1 gives `BlockFile` an internal `Diagnostics` sink set by the root runtime
  wherever it creates or adopts a block, and `NamePool` reports through `DiagnosticsReport.Send` before throwing.
- **The spike's `index-storage` benchmark verb** is ported without `--keep-name` (slice 2 deletes retention), and
  calls `MftBlockRowWriter.WriteBatches` without the retention argument, so it compiles on either side of slice 2.
- **Ignore-case matching is defined once:** a name matches a pattern ignoring case exactly when the ordinal
  match of `NameFolding.Fold(name)` against `NameFolding.Fold(pattern)` succeeds. Every fast path must agree with
  that definition; the oracle tests compare against it.
- **Watch's `SMPW` tag goes to 3 in slice 2**, with the retention flags, not in slice 4.

## Global Constraints

- No backward compatibility (AGENTS.md "No backward compatibility"): delete in place; no `[Obsolete]`, no
  transitional fields, no dual read paths. Format version 8; version 7 blocks fail the version check, are deleted
  under the owner lock and the root cold-scans. No migration.
- Breaking changes are never listed as a cost or risk in a plan, review or PR body.
- Full-word identifiers (`maximum`, not `max`); `Mft` and `Usn` stay.
- No em-dashes (U+2014) in code, comments, docs, commit messages or PR bodies.
- No absolute machine paths in shipped code or tracked docs.
- Code files under about 500 lines; a larger file carries a one-line note saying why.
- Tests never assert on wall-clock time; inject or mock the clock, or count operations.
- Test classes touching `MFTLibNative.Hooks`, `FileUtilities.Hooks`, `Kernel32.Hooks`, their `ResetToDefaults`
  or any `NativeTestHooks` member carry class-level `[DoNotParallelize]` with cleanup resets
  (`docs/test-isolation.md`).
- Each PR: green `test / windows`, `test / linux`, `test / linux-package`, `aislop / quality-gate`; its
  `api-decisions.md` rulings and `MFTLib.Tests/PublicSurface/*.approved.txt` updates in the same PR; a PR that grows
  an approved file names, in its body, the consumer production caller of every added member.
- PR bodies start with `## Problem` and cite issues as "MFTLib issue 844" with no URL.
- `AGENTS.md` is at 14,964 of its 15,000-character budget: slices 1 and 2 do not edit it. Any later edit must be
  net-neutral and is checked with `wc -m AGENTS.md` plus the `AgentInstructionsTests` filter.
- `TEST-REPORT.md` is never edited in a PR.
- aislop: run the installed `aislop`, never `npx aislop`; `aislop scan .` before completion and
  `aislop scan --staged` before each commit; findings are this turn's work; never edit `.aislop/config.yml`.
- Constants from the spec, verbatim: entry 12 bytes (`PoolOffsetBytes` 4, `LengthAndEncoding` 2 with 15-bit
  length and high bit UTF-16, `HashTag` 2 = upper 16 hash bits, `Next` 4); row 32 bytes with the name id at offset
  8; header 160 declared bytes; entry zero reserved; pool `P = pageAlign(max(1 MiB, 24 * S))`; buckets
  `roundUpToPowerOf2(max(1024, ceil(E / 4)))`; cancellation checked before setup, every 4096 name entries in the
  entry pass, before the first row and every 4096 rows.

## Review Focus

Inputs the spec implies that are most likely to bite a user; each line names the task whose tests pin it.

1. **Unpaired surrogates in NTFS names** (NTFS stores them): such a name is stored UTF-16, round-trips exactly
   through `FileEntry.Name` and `IndexRow.Name`, folds unit by unit, and matches itself under every mode. Task 1.1
   (fold) and Task 1.5 (oracle row).
2. **Case-only rename during a live watch**: after `TryRenameRow(row, "README.md")` on a row named `readme.md`,
   `FileEntry.Name` returns the new spelling, a case-sensitive exact search for the old spelling no longer finds
   the row, and a peer row still named `readme.md` keeps its spelling. Task 1.3.
3. **A corrupt cached block never reads out of bounds**: out-of-range id, odd UTF-16 offset, pool overrun, cyclic
   `Next` and wrong tag each open, then `FileEntry.Name` throws `InvalidDataException` and `Find` returns null
   instead of crashing or looping. Task 1.4.
4. **A query running while the writer interns new names**: an entry pass captures the entry count; a row renamed
   to a name interned after the pass is not matched by that pass and no read faults. Task 1.5.
5. **Very large volumes**: the pool reservation clamps below 2 GiB so every pool span fits an `int` length, and
   bucket and entry arithmetic does not overflow at `S = int.MaxValue - 2`. Task 1.2.

---

# Slice 1: Format 8 name encoding

**Branch:** `feature/844-format-8-names` from main. **No public change** beyond orchestrator decision 2. First
commit adds this plan file at `docs/superpowers/plans/2026-10-27-issue-844-implementation-plan.md`.

**Spec location:** this branch starts before PR 845 (documentation only) merges, so the spec is not yet on main.
Read it at the repository path on this plan's `Spec:` line from PR 845's branch:
`git fetch origin docs/844-design` then
`git show origin/docs/844-design:docs/superpowers/specs/2026-10-27-issue-844-volume-scan-and-name-encoding-design.md`.
Once PR 845 lands, rebase onto main before opening the PR; Task 1.7's spec amendment then edits that file in your
tree.

**Meets slice 2 at:** `ScanShape.ForMft` (called by the new `Benchmark/BenchmarkRunner.IndexStorage.cs`),
`MftBlockRowWriter` (called by the same verb) and these files both slices edit:
`MFTLib.Tests/MftBlockRowWriterTests.cs`, `MFTLib.Tests/JournalBrokerHostScanTests.cs`,
`MFTLib.Tests/Index/EnumerationProducerTests.cs`, `MFTLib.Tests/Index/NamedBlockSectionLayoutTests.cs`,
`MFTLib.Tests/Index/RootKeyTests.cs`, `MFTLib.Tests/BenchmarkRunnerTests.cs`, `docs/index-format.md`,
`docs/api-decisions.md`, `CHANGELOG.md`. Whichever slice merges second rebases and resolves; for slice 1 that
means changing the benchmark's `ScanShape.ForMft(retainedFileNames: null, includeFreed: false)` to slice 2's
`ScanShape.ForMft(includeFreed: false)`.

**Build and test commands used below:**

```powershell
.\scripts\build-windows.ps1 -NoRestore      # native + managed Release|x64 (after one restore)
dotnet build MFTLib.Tests/MFTLib.Tests.csproj -c Release -p:Platform=x64
dotnet test MFTLib.Tests/MFTLib.Tests.csproj -c Release -p:Platform=x64 --no-build --filter "<filter>"
```

### File map

| File | Responsibility |
| --- | --- |
| Create `MFTLib/Index/NameFolding.cs` | The one fold: invariant simple uppercase per UTF-16 unit, `Rune` for valid pairs |
| Create `MFTLib/Index/NameHash.cs` | FNV-1a 64 over folded UTF-16LE units (port, folding through `NameFolding`) |
| Create `MFTLib/Index/NameEntry.cs` | 12-byte explicit-layout entry (port unchanged) |
| Create `MFTLib/Index/NameView.cs` | Borrowed ASCII or UTF-16 name; ordinal equality, copy, `ToString` (port, ignore-case members removed) |
| Rewrite `MFTLib/Index/NamePool.cs` | Ordinal interning, lazy-validated `Read`, chain walks, bucket rebuild |
| Create `MFTLib/Index/NamePoolIntegrity.cs` | Full integrity pass, tests only (not called in production) |
| Create `MFTLib/Index/BlockFile.Names.cs` | Entry, bucket and pool spans; access counter; diagnostics sink |
| Modify `MFTLib/Index/BlockLayout.cs`, `BlockHeader.cs`, `BlockFile.cs`, `BlockFile.Layout.cs`, `FileRow.cs`, `RootKey.cs` | Format 8 geometry and the name-id descriptor |
| Modify `MFTLib/Index/BlockWriter.cs`, `BlockWriter.RowMetadata.cs`, `BlockRowSnapshot.cs` | Interning writes, ordinal rename, growth preserving ids |
| Create `MFTLib/Index/NamePattern.cs` | A query pattern folded and encoded once; per-entry match with ASCII and UTF-16 paths |
| Create `MFTLib/Index/NameIdCandidates.cs` | Inline set of four ids, then a rented array |
| Create `MFTLib/Index/NameQueryMatches.cs` | Entry pass into a bitset, or exact id candidates; row membership test |
| Create `MFTLib/Index/RowEnumerationResources.cs` | Shared resource owner for `IndexRowEnumerator` copies (port) |
| Modify `MFTLib/Index/NameMatching.cs`, `SearchEngine.cs`, `LookupEngine.cs`, `RowScanner.cs`, `IndexRowEnumerable.cs`, `IndexRow.cs`, `IndexNavigation.cs`, `FileEntry.cs` | Readers over `NameView` and two-pass matching |
| Modify `MFTLib/Sources/MftBlockCapacity.cs`, `MFTLib/Index/EnumerationProducer.cs`, `MFTLib/Index/FileIndex.RootRuntime.Production.cs`, `MFTLib/Index/BlockFileCreateOptions.cs` | One pool reservation routine for both producers |
| Modify `MFTLibTestExtensions/SyntheticBlock.cs`, `SyntheticBlockEditor.cs`; create `MFTLibTestExtensions/SyntheticNamePool.cs` (internal) | Test package forwarding |
| Create `Benchmark/BenchmarkRunner.IndexStorage.cs`; modify `Benchmark/BenchmarkRunner.cs`, `BenchmarkRunner.Index.cs`, `BenchmarkRunner.EnumerationWalk.cs` | `index-storage` verb; new reservation routine |
| Tests | `NameFoldingTests`, `NamePoolTests`, `NamePoolValidationTests`, `NameSpellingOracleTests`, `NameQueryLifetimeTests` (new); spike-modified tests ported |

### Task 1.1: The fold routine and the hash

**Files:**
- Create: `MFTLib/Index/NameFolding.cs`, `MFTLib/Index/NameHash.cs`
- Test: `MFTLib.Tests/Index/NameFoldingTests.cs`

**Interfaces:**
- Produces: `static class NameFolding` with `internal static char Fold(char unit)`,
  `internal static void Fold(ReadOnlySpan<char> source, Span<char> destination)` (same length; a valid surrogate
  pair folds through `Rune.ToUpperInvariant`, any other unit through `char.ToUpperInvariant`),
  `internal static bool IsAscii(ReadOnlySpan<char> text)`.
- Produces: `static class NameHash` with `internal static ulong Compute(ReadOnlySpan<char> name)` and
  `internal static ulong Compute(NameView name)` (Task 1.3 adds the `NameView` overload once `NameView` exists).
  FNV-1a 64 (`14695981039346656037`, prime `1099511628211`) over the folded units, low byte then high byte, as
  in the spike's `NameHash.cs`.

- [ ] **Step 1: Write the failing tests**

```csharp
[TestMethod] public void Fold_EveryAsciiPair_AgreesWithAsciiEqualsIgnoreCase()
// for a in 0..127, b in 0..127:
//   Assert.AreEqual(Ascii.EqualsIgnoreCase([ (byte)a ], [ (byte)b ]), NameFolding.Fold((char)a) == NameFolding.Fold((char)b));
//   and NameFolding.Fold((char)a) is ASCII.
[TestMethod] public void Fold_EveryBmpUnit_HashesAsItsFold()
// for u in 0..0xFFFF: Assert.AreEqual(NameHash.Compute([NameFolding.Fold((char)u)]), NameHash.Compute([(char)u]));
// lone surrogates (0xD800..0xDFFF) fold to themselves.
[TestMethod] public void Fold_EverySupplementaryScalar_StaysSupplementaryAndHashesAsItsFold()
// for scalar in 0x10000..0x10FFFF: folded = NameFolding.Fold(pair) has length 2, is a valid pair,
// and NameHash.Compute(folded) == NameHash.Compute(pair).
[TestMethod]
[DataRow("ı", "I")]   // dotless i
[DataRow("ſ", "S")]   // long s
[DataRow("café", "CAFÉ")]
public void Fold_NonAsciiThatFoldsToAscii(string input, string expected)
[TestMethod] public void Compute_IsCaseInsensitiveAndStable()
// NameHash.Compute("Makefile") == NameHash.Compute("MAKEFILE"); pin NameHash.Compute("a") to its literal FNV value
// computed once from the formula (write the literal into the test).
```

- [ ] **Step 2: Run, expect FAIL** (types missing): `--filter FullyQualifiedName~NameFoldingTests`
- [ ] **Step 3: Implement** `NameFolding` and `NameHash` as specified in Interfaces.
- [ ] **Step 4: Run, expect PASS** with the same filter.
- [ ] **Step 5: Commit** `feat: add the name fold routine and folded name hash` (with this plan file).

### Task 1.2: Format 8 geometry

One compile unit with Task 1.3: the row descriptor, header and pool change together. Do Task 1.2's test and
layout steps, then continue into Task 1.3 before building the whole solution; commit once both are green.

**Files:**
- Modify: `MFTLib/Index/BlockLayout.cs`, `MFTLib/Index/BlockHeader.cs`, `MFTLib/Index/RootKey.cs`,
  `MFTLib/Index/BlockFileCreateOptions.cs`, `MFTLib/Sources/MftBlockCapacity.cs`, `MFTLib/Index/EnumerationProducer.cs`,
  `MFTLib/Index/FileIndex.RootRuntime.Production.cs:94-110` (initial plan only; growth stays as is),
  `Benchmark/BenchmarkRunner.Index.cs:129-130`, `Benchmark/BenchmarkRunner.EnumerationWalk.cs:160-161`,
  `MFTLibTestExtensions/SyntheticBlock.cs:126-127`
- Create: `MFTLib/Index/NameEntry.cs` (port)
- Test: `MFTLib.Tests/Index/BlockLayoutTests.cs`, `BlockHeaderTests.cs`, `RootKeyTests.cs`, `BlockRootKeyStorageTests.cs`

**Interfaces:**
- Produces in `BlockLayout`: `FormatVersion = 8`; `HeaderFieldBytes = 160`;
  `uint ComputeNamePoolCapacity(uint slotCapacity)` returning `AlignUp(max(1048576, 24 * S), 4096)` clamped to
  `MaximumNamePoolCapacity = 0x7FFFF000` (so a pool span length fits `int`);
  `uint ComputeBucketCount(uint entryCapacity)` returning `RoundUpToPowerOf2(max(1024, ceil(E / 4)))`;
  `long NameEntryRegionOffset(uint slotCapacity)`, `long NameEntryRegionBytes(uint entryCapacity)` (`12 * (E + 1)`
  page-aligned), `long BucketRegionOffset(uint slotCapacity, uint entryCapacity)`,
  `long BucketRegionBytes(uint entryCapacity)`, `long NamePoolOffset(uint slotCapacity, uint entryCapacity)`,
  `long TotalBlockBytes(uint slotCapacity, uint entryCapacity, uint namePoolCapacity)`;
  `uint DoubleCapacity(uint capacity)` clamps at `int.MaxValue - 1` (room for entry zero).
  `MinimumNamePoolHeadroomBytes` and the estimate-taking `ComputeNamePoolCapacity(uint estimatedNameBytes)` are
  deleted. `ComputeSlotCapacity` is unchanged.
- Produces in `BlockHeader`: offsets 124 `NameEntryCount`, 128 `NameEntryCapacity`, 132 `BucketCount`,
  136 `NameEntryRegionOffset` (u64), 144 `BucketRegionOffset` (u64), 152 `Reserved` (u64). Root key length at 160,
  text at 162. `HasConsistentGeometry` checks every offset against the layout for the stored `S`, `E`, `P`;
  `RowCount <= S`, `NameEntryCount <= E`, `NamePoolUsed <= P`; `S < int.MaxValue`, `E < int.MaxValue`;
  `BucketCount == ComputeBucketCount(E)`. It does not require `E == S`.
- `RootKey.MaximumTextUnits = (HeaderRegionBytes - HeaderFieldBytes - sizeof(ushort)) / sizeof(char)` (1,967).
- `MftBlockCapacity.Plan` returns `(S, ComputeNamePoolCapacity(S))`; `AverageNameBytesPerRow` is deleted.
  `EnumerationProducer.EstimatedNameBytesPerRow` and `EstimateNamePoolBytes` are deleted; the enumeration initial
  plan uses `ComputeNamePoolCapacity(slotCapacity)`. Benchmarks use the routine. `SyntheticBlock` uses
  `Math.Max(ComputeNamePoolCapacity(S), (uint)AlignUp(nameBytes, PageSize))` because its names are arbitrary.

- [ ] **Step 1: Write the failing tests** (port the spike's `BlockLayoutTests` and `BlockHeaderTests` edits, then
  change them to the final formulas)

```csharp
[DataRow(0u, 1024u)] [DataRow(4096u, 1024u)] [DataRow(4097u, 2048u)]
[DataRow(2147483646u, 536870912u)]
public void ComputeBucketCount_IsAPowerOfTwoAtOrAboveAQuarterOfTheEntries(uint entries, uint expected)

[DataRow(0u, 1048576u)] [DataRow(43690u, 1048576u)] [DataRow(43691u, 1052672u)]  // 24 * 43691 = 1,048,584 -> next page
[DataRow(1000000u, 24002560u)]                                                   // 24,000,000 page-aligned
[DataRow(2147483646u, 0x7FFFF000u)]
public void ComputeNamePoolCapacity_ReservesTwentyFourBytesPerSlotWithAOneMebibyteFloor(uint slots, uint expected)

public void NameRegions_FollowTheRowsAndSequencesInOrder()    // entry region after sequences, buckets after entries, pool after buckets, all page-aligned
public void TotalBlockBytes_DependsOnTheEntryCapacityIndependently()  // TotalBlockBytes(S, 2*S, P) > TotalBlockBytes(S, S, P)
public void Header_FieldOffsetsMatchTheFormat8Table()                 // Marshal.OffsetOf for 124,128,132,136,144,152; size 160
public void HasConsistentGeometry_RejectsABucketCountOffTheFormula()
public void HasConsistentGeometry_AcceptsAnEntryCapacityOtherThanTheSlotCapacity()
public void FromDirectory_AcceptsAt1967UnitsAndRejectsAt1968()       // RootKeyTests: replaces the 1983 case
public void MftPlan_UsesTheSharedReservationRoutine()                // MftBlockCapacity.Plan(geometry).NamePoolCapacity == ComputeNamePoolCapacity(S)
```

- [ ] **Step 2: Implement** the Interfaces above. Port `NameEntry.cs` unchanged from the spike.
- [ ] **Step 3: Continue into Task 1.3** (the solution does not build until the descriptor and pool change land).

### Task 1.3: Interned names in the block, ordinal identity

**Files:**
- Modify: `MFTLib/Index/FileRow.cs`, `BlockFile.cs`, `BlockFile.Layout.cs`, `BlockWriter.cs`, `BlockWriter.RowMetadata.cs`,
  `BlockRowSnapshot.cs`, `RowScanner.cs`, `FileEntry.cs:182`, `IndexNavigation.cs:52,168`, `LookupEngine.cs` (temporary
  per-row compare, replaced in Task 1.5), `SearchEngine.cs` (temporary), `NameMatching.cs`
- Create: `MFTLib/Index/NameView.cs`, `MFTLib/Index/NamePool.cs` (rewrite), `MFTLib/Index/BlockFile.Names.cs`,
  `MFTLibTestExtensions/SyntheticNamePool.cs`
- Modify: `MFTLibTestExtensions/SyntheticBlockEditor.cs:163-171`, `MFTLib.Tests/Index/SyntheticBlockBuilder.cs`
  (`MutateNameDescriptor(uint rowIndex, uint nameId)` replaces the offset/length form)
- Test: `MFTLib.Tests/Index/NamePoolTests.cs` (port then correct), `BlockWriterTests.cs`, `BlockWriterTests.Rename.cs`,
  `FileRowTests.cs`, `CapacityExhaustionTests.cs`, `FileIndexEnumerationGrowthTests.cs`, `BlockFileTests.cs`, and every
  other test file the spike's diff touches (`git diff main...spike/name-dedup --stat` lists them)

**Interfaces:**
- `FileRow`: offset 8 `uint NameId`, 12 `ushort Reserved`, 14 `RowFlags Flags`;
  `static ulong ReadDescriptorWord(ref readonly FileRow row)`, `static void WriteDescriptorWord(ref FileRow row, uint nameId, RowFlags flags)`
  (clears reserved bits), `static uint DescriptorNameId(ulong)`, `static RowFlags DescriptorFlags(ulong)`.
  `DescriptorNameOffsetBytes` and `DescriptorNameLengthUnits` are deleted.
- `BlockFile` (internal): `uint NameEntryCapacity`, `uint BucketCount`, `uint NameEntryCount` (volatile read),
  `void PublishNameEntryCount(uint)`, `Span<NameEntry> NameEntries` (length `E + 1`), `Span<uint> Buckets`,
  `Span<byte> GetNamePoolBytes(uint offset, int length)`. `NamePoolCharacters` is deleted.
  `InitialHeader` sets `E = S` and `BucketCount = ComputeBucketCount(E)`. `TryAdoptPublishedCursors` also checks
  `E`, `BucketCount` and `NameEntryCount <= E`.
- `readonly ref struct NameView(ReadOnlySpan<byte> bytes, bool isUtf16)`: `IsUtf16`, `Length`, `IsEmpty`,
  `AsciiBytes`, `Utf16Characters`, `this[int]`, `bool EqualsOrdinal(ReadOnlySpan<char> name)`,
  `void CopyTo(Span<char>)`, `string ToString()`. No ignore-case members: ignore-case goes through `NamePattern`
  (Task 1.5) or `NameFolding`.
- `NamePool` (internal static): `MaximumNameLengthUnits = 32767`;
  `NameView Read(BlockFile block, uint nameId)`; `NameView ReadRowName(BlockFile block, uint rowIndex)`;
  `bool TryFindOrdinal(BlockFile block, ReadOnlySpan<char> name, out uint nameId)`;
  `bool TryGetOrAdd(BlockFile block, ReadOnlySpan<char> name, out uint nameId)`;
  `void RebuildBuckets(BlockFile block)`;
  `ChainWalker WalkChain(BlockFile block, ulong hash)` yielding ids by `Next` with the termination and bounds rules
  of Task 1.4 (a `ref struct` with `bool MoveNext(out uint nameId, out NameEntry entry)`).
  Identity: walk the chain for `hash`, compare `HashTag`, then `NameView.EqualsOrdinal`. A hit returns the existing
  id even when entries or pool are full. A miss checks `NameEntryCount < E` and pool room, writes bytes (ASCII one
  byte per unit, else UTF-16LE at an even offset), then the entry with `Next` = current head, then
  `PublishNamePoolUsed`, `PublishNameEntryCount`, then `Volatile.Write` of the bucket head.
- `BlockWriter.TryRenameRow`: when `currentName.EqualsOrdinal(name)` only the parent changes; otherwise intern and
  publish the new id with the flags in one descriptor store. A case-only rename therefore swaps the id.
- `BlockRowSnapshot`: copies used rows, sequences, entries `0..NameEntryCount` and pool bytes in 16 MiB chunks;
  `RestoreInto` requires `target.E >= NameEntryCount`, copies, publishes counts and calls `RebuildBuckets`.
- `SyntheticNamePool` (internal, test package): forwards `TryGetOrAdd`, `TryFindOrdinal`, `Read`, `ReadRowName`,
  `Hash`, and (Task 1.4) `CheckIntegrity`, and (Task 1.5) `EnumerateRows`.

- [ ] **Step 1: Write the failing tests** (in `NamePoolTests` and `BlockWriterTests.Rename`)

```csharp
public void TryGetOrAdd_CaseVariantsAreDistinctEntriesInOneChainWithOneTag()
// ids for "Makefile" and "makefile" differ; both entries share HashTag and bucket; Read(id).ToString() is each spelling.
public void TryGetOrAdd_TheSameSpellingReturnsTheSameId()
public void TryGetOrAdd_AsciiTakesOneBytePerUnitAndNonAsciiIsUtf16AtAnEvenOffset()
// "a.txt" uses 5 pool bytes; then "café" starts at offset 6 (padded) and uses 8 bytes; IsUtf16 true.
public void TryGetOrAdd_AnUnpairedSurrogateNameIsStoredUtf16AndReadsBackExactly()   // "\uD800x"
public void TryGetOrAdd_AHitSucceedsWhenEntriesOrPoolAreFull()
public void TryGetOrAdd_AMissFailsWhenEntriesOrPoolAreFull()                        // and the writer sets capacity exhausted
public void Rename_CaseOnly_InternsTheNewSpellingAndSwapsTheId()
// row "readme.md" renamed to "README.md": FileEntry.Name == "README.md"; NameEntryCount grew by one.
public void Rename_PeersSharingTheOldNameKeepIt()
// rows 10 and 11 both "notes.txt"; rename 10 to "Notes.txt"; row 11 still reads "notes.txt" with its old id.
public void Rename_ParentOnlyMoveKeepsTheDescriptor()
public void Growth_RestorePreservesIdsAndSpellings()
// enumeration growth (FileIndexEnumerationGrowthTests): every row's id and Name equal before and after; TryFindOrdinal finds each name in the grown block.
public void Reopen_PreservesIdsAndSpellings()
// write case variants across two directories, complete, reopen via SyntheticBlockBuilder.OpenForReading: Names equal input spellings.
```

- [ ] **Step 2: Run, expect FAIL** (compile errors are the expected failure here):
  `--filter "FullyQualifiedName~NamePoolTests|FullyQualifiedName~BlockWriterTests"`
- [ ] **Step 3: Port** `NameView`, `NamePool`, `BlockFile.Names`, `FileRow`, `BlockFile` and writer changes from the
  spike, then apply the corrections: ordinal identity (`NamePool.cs:79` in the spike), ordinal rename
  (`BlockWriter.cs:146-151`), `NameHash` through `NameFolding`, layout calls with `E`. Readers (`RowScanner.CurrentName`
  returns `NameView`; `FileEntry.Name`, `IndexNavigation` use `.ToString()`). Keep `SearchEngine` and `LookupEngine`
  compiling with a per-row `NamePattern`-free comparison for now: `SearchEngine.RowMatches` converts with
  `NameView.ToString()` and calls the existing span `NameMatching.Matches`; Task 1.5 replaces it.
- [ ] **Step 4: Update the existing tests** the spike updated (layout expectations, descriptor helpers,
  `SyntheticBlockBuilder.MutateNameDescriptor(row, nameId)`), keeping every behavior assertion.
- [ ] **Step 5: Build and run**: `.\scripts\build-windows.ps1 -NoRestore`, then
  `--filter "TestCategory=Smoke|FullyQualifiedName~NamePoolTests|FullyQualifiedName~BlockWriterTests|FullyQualifiedName~BlockLayoutTests|FullyQualifiedName~BlockHeaderTests|FullyQualifiedName~BlockFileTests|FullyQualifiedName~RootKeyTests|FullyQualifiedName~CapacityExhaustionTests|FullyQualifiedName~FileIndexEnumerationGrowthTests"`.
  Expected: PASS. `BlockFileTests` cases asserting `InvalidNameDescriptor` from row walks may fail here; Task 1.4
  settles them.
- [ ] **Step 6: Commit** `feat: store each distinct name once in a dual-width pool (format 8)`.

### Task 1.4: Validation split, integrity checker and access counter

**Files:**
- Modify: `MFTLib/Index/BlockFile.cs` (delete the row and chain walks from open), `BlockFile.Names.cs`, `NamePool.cs`,
  `BlockHeader.cs`, the broker section validation that calls header validation (it already validates a private header
  copy; confirm it calls the same `HasConsistentGeometry`), `FileIndex.RootRuntime.Production.cs` and
  `FileIndex.RootRuntime.WarmStart.cs` (set the block's diagnostics sink)
- Create: `MFTLib/Index/NamePoolIntegrity.cs`
- Modify: `MFTLibTestExtensions/SyntheticNamePool.cs` (add `CheckIntegrity`)
- Test: create `MFTLib.Tests/Index/NamePoolValidationTests.cs`; modify `BlockFileTests.cs:165,180`,
  `BlockValidationMatrixTests.cs:109`, `CacheDirectoryInspectionTests.cs:53`, `MFTLib.Tests/SyntheticBlockTests.cs:232-244`

**Interfaces:**
- Eager (open and broker section validation, constant cost): magic, version, region offsets equal the layout for
  stored `S`, `E`, `P`, total size, `RowCount <= S`, `NameEntryCount <= E`, `NamePoolUsed <= P`, `BucketCount`
  formula, root key, file name, producer, cache tag, scan shape, plus `NamePoolUsed >= NameEntryCount`
  (orchestrator decision 1). The four name-region checks (`NameEntryCount <= E`, `NamePoolUsed <= P`, `BucketCount`
  equal to the formula for `E`, `NamePoolUsed >= NameEntryCount`) report `BlockValidationResult.InvalidNameDescriptor`;
  the other geometry checks keep `InconsistentRegions`.
- Lazy in `NamePool.Read`: `0 < id <= NameEntryCount`; length nonzero and `<= MaximumNameLengthUnits`; offset plus
  bytes `<= NamePoolUsed`; UTF-16 offset even. Failure: `DiagnosticsReport.Send(block.Diagnostics, ...)` with a line
  naming the block path, the id and the failed check, then `throw new InvalidDataException(...)`. Id zero returns an
  empty view (a row with no name).
- Chain walk: each step requires `id <= NameEntryCount` and `entry.Next < id`; a failed step ends the walk as not
  found (no throw, no diagnostics).
- `BlockFile`: `internal Action<string>? Diagnostics { get; set; }`; `internal long NameRegionAccessCount`
  incremented (plain, non-atomic) by every `NameEntries`, `Buckets` and `GetNamePoolBytes` access. Exact under one
  thread; documented as approximate under concurrency.
- `NamePoolIntegrity.Check(BlockFile block) : string?` returns null or the first failure: every entry valid per
  `Read`, ASCII entries pass `Ascii.IsValid`, every chain decreasing, every entry's bucket and tag equal its rehash,
  visited count equals `NameEntryCount` (reachability), every row id `<= NameEntryCount` and reserved bits zero.
  Not called by production code. `SyntheticNamePool.CheckIntegrity(BlockFile)` forwards to it.

- [ ] **Step 1: Write the failing tests** (`NamePoolValidationTests`)

```csharp
[DataRow("OutOfRangeId")] [DataRow("OddUtf16Offset")] [DataRow("PoolOverrun")] [DataRow("ZeroLength")]
public void CorruptEntry_OpensThenFailsOnFirstNameRead(string corruption)
// build with SyntheticBlockBuilder, corrupt through the builder's header/row mutators, OpenForReading succeeds with
// null validation; ReadRowName throws InvalidDataException; the Diagnostics sink set on the block received one line.
[DataRow("CyclicNext")] [DataRow("WrongTag")]
public void CorruptChain_OpensAndExactLookupReturnsNotFoundWithoutLooping(string corruption)
// TryFindOrdinal returns false (wrong tag: the name is missed, which the spec accepts); the call returns (bounded).
public void Open_OfALargeBlock_TouchesNoEntryBucketOrPoolRegion()
// 200,000 rows with distinct names; after OpenForReading, block.NameRegionAccessCount == 0.
public void IntegrityCheck_PassesForEveryWriterProducedBlock()
// called at the end of NamePoolTests, BlockWriterTests.Rename and the growth and reopen round-trip tests: Assert.IsNull(SyntheticNamePool.CheckIntegrity(block)).
public void IntegrityCheck_ReportsEachCorruptionKind()
```

- [ ] **Step 2: Run, expect FAIL**: `--filter FullyQualifiedName~NamePoolValidationTests`
- [ ] **Step 3: Implement** the split. Delete `BlockFile.ValidateNameDescriptors` and `NamePool.ValidateBuckets`.
  Route the four name-region checks to `InvalidNameDescriptor` (orchestrator decision 1).
  `SyntheticBlockTests`, `BlockValidationMatrixTests:109` and `CacheDirectoryInspectionTests:53` keep their
  expectation. `BlockFileTests:165,180` replace their row-descriptor corruption with the four eager checks (one case
  each: entry count over `E`, pool used over `P`, wrong bucket count, pool used below entry count). Add a
  `NamePoolValidationTests` case asserting a corrupt per-entry descriptor no longer rejects at open. Update the
  `InvalidNameDescriptor` XML comment to say it reports inconsistent name-region geometry in the header.
- [ ] **Step 4: Wire the sink**: wherever the root runtime creates or adopts a `BlockFile`, set
  `block.Diagnostics = Options.Diagnostics`.
- [ ] **Step 5: Run, expect PASS**: `--filter "FullyQualifiedName~NamePoolValidationTests|FullyQualifiedName~BlockFileTests|FullyQualifiedName~BlockValidationMatrixTests|FullyQualifiedName~CacheDirectoryInspectionTests|FullyQualifiedName~SyntheticBlockTests|FullyQualifiedName~NamedBlockSectionLayoutTests"`
- [ ] **Step 6: Commit** `feat: validate name reads lazily and keep block open constant-cost`.

### Task 1.5: Two-pass queries with folded patterns

**Files:**
- Create: `MFTLib/Index/NamePattern.cs`, `MFTLib/Index/NameIdCandidates.cs`, `MFTLib/Index/NameQueryMatches.cs`,
  `MFTLib/Index/RowEnumerationResources.cs`
- Modify: `MFTLib/Index/NameMatching.cs`, `SearchEngine.cs`, `LookupEngine.cs:132-150`, `IndexRowEnumerable.cs`,
  `IndexRow.cs`, `MFTLibTestExtensions/SyntheticNamePool.cs` (add `EnumerateRows`)
- Test: create `MFTLib.Tests/Index/NameSpellingOracleTests.cs` (replaces the spike's `NameDedupSearchTests.cs`),
  `MFTLib.Tests/Index/NameQueryLifetimeTests.cs`; port the spike's `FileIndexEnumerateRowsTests.cs` additions

**Interfaces:**
- `sealed class NamePattern` built once per query from `SearchQuery`: holds the original text, its fold
  (`NameFolding.Fold`), `byte[]? OriginalAscii` (null when the original is not ASCII), `byte[]? FoldedAscii` (null when
  the fold is not ASCII), the mode and the case setting. `bool Matches(NameView name, ref char[] scratch)`:
  - ASCII entry, case-sensitive: `OriginalAscii` null means no match; exact `SequenceEqual`; substring
    `ReadOnlySpan<byte>.IndexOf`; glob over bytes.
  - ASCII entry, ignore case: `FoldedAscii` null means no match (constant time); exact `Ascii.EqualsIgnoreCase`;
    substring `IndexOfAny(upper, lower)` of the first byte, then `Ascii.EqualsIgnoreCase` on the candidate window,
    repeating from the next position; glob over bytes comparing ASCII uppercase.
  - UTF-16 entry: case-sensitive ordinal ops on the characters against the original; ignore case folds the entry
    into `scratch` (grown as needed) and runs ordinal ops against the fold.
  - The recipe splits by `CaseSensitive` and `MatchMode`. Only ignore-case comparisons use the fold. Every
    case-sensitive comparison (exact, substring, glob) uses the original pattern's units or bytes, so a
    case-sensitive substring is an ordinal `IndexOf` of the original bytes. Glob is always wildcard matching, using
    the existing iterative last-star backtracking algorithm over `ReadOnlySpan<char>` or `ReadOnlySpan<byte>`.
    Literal runs between wildcards may be located with a vectorized `IndexOf`, but a glob is never reduced to one
    literal test (`*.cs` is never `IndexOf(".cs")`).
- `struct NameIdCandidates : IDisposable`: four inline `uint` slots, then an `ArrayPool<uint>` array;
  `void Add(uint)`, `bool Contains(uint)`, `int Count`, `void Dispose()`.
- `sealed class NameQueryMatches : IDisposable`, constructed `(BlockFile block, SearchQuery query, CancellationToken)`:
  checks cancellation before setup; captures `NameEntryCount`.
  - No pattern: all rows match.
  - Exact, case-sensitive: walk the chain for `NameHash.Compute(pattern)`; the single ordinal match, if any.
  - Exact, ignore case: walk the one chain; add every entry with a matching tag whose `NamePattern.Matches` is true.
    Records `OrdinalMatchId` (the entry equal ordinally to the pattern, or 0) for `Find`.
  - Substring and glob: rent `ulong[]` of `(count + 64) / 64` words; for ids `1..count`, check cancellation every
    4096 ids, set the bit when `NamePattern.Matches`.
  - `bool Matches(in FileRow row)`: id `<= capturedCount` and in the bitset or candidates.
- `SearchEngine.RowMatches(in FileRow row, NameQueryMatches names, SearchQuery query)`: row columns first, then
  `names.Matches`. `SearchOneRoot` and `EnumerateRoot` build one `NameQueryMatches` per root under the borrow.
- `LookupEngine.TryFindChild`: build exact candidates for the segment under the descent's case setting; scan rows
  for children of `parentRow` whose id is a candidate; return immediately on `OrdinalMatchId`; otherwise return the
  lowest matching row after the scan (or the first match when `OrdinalMatchId == 0`, which is already lowest).
- `RowEnumerationResources` (port): owns the active `NameQueryMatches` and the ASCII capture buffer;
  `int Advance(int version)`, `void ValidateVersion(int)`, `void BeginRoot(BlockFile, SearchQuery, CancellationToken)`,
  `void EndRoot()`, `ReadOnlySpan<char> Capture(NameView)`. Per orchestrator decision 2, advancing one enumerator copy invalidates
  older copies with `InvalidOperationException`.
- `NameMatching` keeps `ThrowIfUndefined` and the span `EqualsName` used by `FileIndex.RootRuntime.WarmStart.cs:49`;
  the span `Matches`, `ContainsSubstring` and `MatchesGlob(ReadOnlySpan<char>, ...)` are deleted once no caller remains.

- [ ] **Step 1: Write the failing oracle** (`NameSpellingOracleTests`). The expected name of each row is its input
  spelling. The oracle for ignore case is ordinal on `NameFolding.Fold` of both sides; for case-sensitive it is
  ordinal; glob uses the same iterative algorithm over the oracle strings (not `FileSystemName`, whose escape rules
  differ).

```csharp
static readonly (string Directory, string Name)[] Input =
[
    ("a", "Makefile"), ("a", "makefile"),            // case-sensitive siblings
    ("b", "README.md"), ("c", "readme.md"),          // case variants across directories
    ("a", "café.txt"), ("b", "CAFÉ.TXT"),  // UTF-16 width, both casings
    ("c", "ındex.js"), ("c", "ſetup.py"),  // folds to ASCII
    ("a", "index.js"), ("b", "index.js"),            // duplicate spelling, one id
    ("c", "\uD800lone.bin"),                         // unpaired surrogate
    ("d", "readme.txt"), ("d", "README.txt"),        // case-sensitive substring pin
    ("d", "Program.cs"), ("d", "Program.csx"),       // glob pin
];
[DataRow(NameMatchMode.Exact, false)] [DataRow(NameMatchMode.Exact, true)]
[DataRow(NameMatchMode.Substring, false)] [DataRow(NameMatchMode.Substring, true)]
[DataRow(NameMatchMode.Glob, false)] [DataRow(NameMatchMode.Glob, true)]
public async Task EveryQueryPath_ReturnsInputSpellingsMatchingTheOracle(NameMatchMode mode, bool caseSensitive)
// patterns: "makefile", "Makefile", "README.MD", "café", "INDEX", "I", "S", "*.js", "?ndex.*", "\uD800lone.bin", "missing", ""
// plus "readme" and "*.cs".
// Assert rows and names equal for SearchEngine.Search, SearchEngine.Enumerate and SyntheticNamePool.EnumerateRows.
public async Task CaseSensitiveSubstring_UsesTheOriginalPattern()
// Substring "readme", CaseSensitive: true returns the "readme.txt" row and not "README.txt"; ignore case returns both.
public async Task Glob_MatchesWildcardsNotALiteralSubstring()
// Glob "*.cs" under both case settings returns "Program.cs" and not "Program.csx".
public async Task Find_IgnoreCase_PrefersTheOrdinalSpellingElseTheLowestRow()
// Find("a\\makefile") under ignore case returns the "makefile" row; Find("a\\MAKEFILE") returns the lower of the two rows.
public async Task Find_CaseSensitive_TakesTheSingleOrdinalMatch()
public async Task Children_ReturnInputSpellings()
public async Task CaseOnlyRename_IsVisibleToEveryQueryMode()
public async Task Reopen_AndGrowth_PreserveTheOracle()   // run the oracle again after reopen and after an enumeration growth
```

- [ ] **Step 2: Write the lifetime tests** (`NameQueryLifetimeTests`, port the spike's cases from
  `FileIndexEnumerateRowsTests.cs` and add):

```csharp
public void EntryPass_ObservesCancellationEvery4096Entries()     // a token cancelled by a counting callback, not a timer
public void RenameToANewNameDuringAPass_IsNotMatchedAndDoesNotFault()
public void AdvancingOneEnumeratorCopy_InvalidatesOlderCopies()
public void DisposingAnyCopy_ReturnsTheBorrowAndTheBitset()
```

- [ ] **Step 3: Run, expect FAIL**: `--filter "FullyQualifiedName~NameSpellingOracleTests|FullyQualifiedName~NameQueryLifetimeTests"`
- [ ] **Step 4: Implement** the Interfaces. Port `NameQueryMatches`, `RowEnumerationResources` and the
  `IndexRowEnumerable`/`SearchEngine` changes from the spike, then apply the corrections (ordinal exact, candidate
  sets, `NamePattern` replacing `NameView.Contains`'s scalar loop at spike `NameView.cs:60-76`).
- [ ] **Step 5: Run, expect PASS**, then the Smoke tier: `--filter TestCategory=Smoke`
- [ ] **Step 6: Commit** `feat: match interned names once per query with folded patterns`.

### Task 1.6: `index-storage` benchmark verb

**Files:**
- Create: `Benchmark/BenchmarkRunner.IndexStorage.cs` (port, without `--keep-name`)
- Modify: `Benchmark/BenchmarkRunner.cs` (dispatch and usage line)
- Test: `MFTLib.Tests/BenchmarkRunnerTests.IndexStorage.cs` (port), `MFTLib.Tests/BenchmarkRunnerTests.cs` (usage text)

**Interfaces:** `Task<int> RunIndexStorageAsync(string[] arguments)`, usage
`Benchmark.exe index-storage --synthetic N`. Builds the block through `MftBlockCapacity.CreateOptions` with
`ScanShape.ForMft(retainedFileNames: null, includeFreed: false)` (slice 2 rebase: `ScanShape.ForMft(includeFreed: false)`),
writes with `MftBlockRowWriter.WriteBatches(writer, batches, new MftScanOptions(), null, cancellationToken)`. Timing
through the runner's injected `TimeProvider`. Prints slot capacity, entry capacity, entry count, pool used and
capacity, entry used and reserved bytes, bucket bytes, file bytes.

- [ ] **Step 1: Port the tests**, dropping the `--keep-name` cases; add `Run_IndexStorage_WithoutSyntheticCount_PrintsUsage`.
- [ ] **Step 2: Run, expect FAIL**: `--filter FullyQualifiedName~BenchmarkRunnerTests`
- [ ] **Step 3: Port the verb.**
- [ ] **Step 4: Run, expect PASS** with the same filter.
- [ ] **Step 5: Commit** `feat: add the index-storage benchmark verb`.

### Task 1.7: Documentation and rulings

**Files:** `docs/index-format.md` (Layout, Header, Root key text, Row, Capacity, Name pool sections: port the
spike's edits, then correct identity, reservation, bucket formula, `E`, the validation split table and "nothing is
rebuilt or rehashed at open"), `docs/query-lifetime.md` (entry-pass cancellation cadence; enumerator copy rule),
`docs/architecture.md` (the spike's two-line edit), `docs/api-decisions.md`, `CHANGELOG.md`, and the spec file
(section 1 "Changed" table).

- [ ] **Step 1: Run the docs-update check** across README, `docs/`, inline XML comments (`FileRow`, `BlockLayout`,
  `IndexRow`, `IndexRowEnumerator`, `RootKey.FromDirectory`).
- [ ] **Step 2: Add `api-decisions.md` rulings**: "Names are interned by exact spelling" (exact ordinal identity
  under the folded hash; ignore-case lookups collect every casing; `Find` prefers the ordinal spelling, else the
  lowest row; reason: consult converged ruling 2).
  - "Row enumerator copies share one resource owner": advancing one copy invalidates older copies; reason: rows are
    already valid only until the enumerator advances, and a per-copy buffer would cost an allocation per copy
    (orchestrator decision 2).
  - Note that `BlockValidationResult.InvalidNameDescriptor` now reports eager name-region geometry failures only:
    the four header checks at open. Entry, row and chain faults are lazy: `InvalidDataException` on read, and not
    found on a chain walk (ruling 9).
  - Update the root-key ruling's 1,983 to 1,967.
  - Do not touch rulings slice 2 owns (retention).
- [ ] **Step 2a: Amend the spec and query lifetime** in this PR: add a row to the spec's section 1 "Changed" table
  (`IndexRowEnumerator` copies: advancing one copy invalidates older copies with `InvalidOperationException`), and
  make `docs/query-lifetime.md`'s enumerator paragraph say the same.
- [ ] **Step 3: CHANGELOG** under the unreleased 0.3.0 section: format 8, cache blocks rebuilt on first open.
- [ ] **Step 4: Commit** `docs: describe format 8 names and the two-pass query lifetime`.

### Task 1.8: Slice verification and PR

- [ ] Full build: `.\scripts\build-windows.ps1 -NoRestore`
- [ ] Full suite: `dotnet test MFTLib.Tests/MFTLib.Tests.csproj -c Release -p:Platform=x64 --no-build`. Expected:
  0 failed. Linux runs in CI (`test / linux`); run `./scripts/coverage-linux.sh` under WSL when available.
- [ ] Public surface: `--filter "FullyQualifiedName~PublicSurfaceTests|FullyQualifiedName~PublicMemberCallerTests"`
  passes with no approved-file change.
- [ ] Rebased onto main after PR 845 merged; the spec amendment applies to the merged spec.
- [ ] Coverage gate: `.\scripts\run-coverage.ps1 -NonInteractive` then
  `pwsh -NoProfile -File scripts/test-coverage-status.ps1`.
- [ ] `aislop scan .`: 100, zero unsuppressed findings; `git diff --check` clean.
- [ ] Open the PR: body starts `## Problem` (the block stores each occurrence of a name in UTF-16, so storing every
  name costs more than today's filtered block), lists the corrections applied to the spike, and records the
  `index-storage` output for `--synthetic 3000000` as counts plus the command (never a log file).

---

# Slice 2: Delete retention

**Branch:** `feature/844-delete-retention` from main. Runs in parallel with slice 1. **Meets slice 1 at:**
`ScanShape` (signature change below; slice 1's benchmark calls `ForMft`), `MftBlockRowWriter`, and the shared files
listed under slice 1. Second to merge rebases.

**Spec location:** this branch starts before PR 845 (documentation only) merges. Read the spec at the repository
path on this plan's `Spec:` line from PR 845's branch: `git fetch origin docs/844-design` then
`git show origin/docs/844-design:docs/superpowers/specs/2026-10-27-issue-844-volume-scan-and-name-encoding-design.md`.
Rebase onto main once PR 845 lands, before opening the PR.

**Public change:** `FileIndexOptions.RetainedFileNames` and `InProcessBrokerScan.RetainedFileNames` are deleted.
No member is added, so no consumer-caller naming is needed. git-wizard and file-wizard migrate in their pin bumps
(slice 6): git-wizard `MftIndexSession.cs` line 161 and `MftIndexCachePolicy.DirectoryScanFileNames`; file-wizard
any `RetainedFileNames` assignment.

### Task 2.1: Scan shape without retention

**Files:**
- Modify: `MFTLib/Index/ScanShape.cs`, `MFTLibTestExtensions/SyntheticScanShape.cs`
- Test: `MFTLib.Tests/Index/FileIndexScanShapeTests.cs`, `MFTLib.Tests/BenchmarkRunnerTests.cs:165-172`,
  `MFTLib.Tests/BenchmarkRunnerTests.CacheReplacement.cs:28-33`, `MFTLib.Tests/Index/FileIndexCacheTagTests.cs:87`

**Interfaces:**
- `static ulong ScanShape.For(IndexedRoot root, FileIndexOptions options)`: enumeration
  `ForEnumeration(options.DirectoryWalk, root.RootDirectory)`, MFT `ForMft(options.MftSource?.IncludeFreed ?? false)`.
- `static ulong ForMft(bool includeFreed)`: u32 producer kind 1, then the include-freed flag byte.
- `static ulong ForEnumeration(DirectoryWalkOptions walk, string rootDirectory)`: u32 kind 2, pruned names, pruned
  attributes, dot-directory flag, pruned paths within the root, patterns (today's order, retention removed).
- `WriteRetention` and `FoldForMft` are deleted.

- [ ] **Step 1: Write the failing tests**: `ForMft_DependsOnlyOnIncludeFreed` (`ForMft(false) != ForMft(true)`, each
  stable across calls); `ForMft_HashesTheProducerKindThenTheFlag` (equals the first eight little-endian bytes of
  SHA-256 over `01 00 00 00 00`, computed in the test); delete `ReplacementCharacterInARetainedName_...` and
  `EnumerationRetentionCasingFollowsTheRootComparison`; reduce `ChangingAnMftOption_RecordsWrongScanShapeAndColdScans`
  to its `includeFreed` cases.
- [ ] **Step 2: Run, expect FAIL**: `--filter "FullyQualifiedName~FileIndexScanShapeTests|FullyQualifiedName~BenchmarkRunnerTests"`
- [ ] **Step 3: Implement** the signatures and update every caller (compiler errors list them).
- [ ] **Step 4: Run, expect PASS.**
- [ ] **Step 5: Commit** `refactor: drop retention from scan shapes`.

### Task 2.2: The ArmAndScan frame carries the section and include-freed only

**Files:**
- Modify: `MFTLib/Broker/Protocol/BrokerFrame.cs:65,214-220`, `MFTLib/Broker/Protocol/BrokerProtocol.cs:127-140`,
  `MFTLib/Broker/Protocol/BrokerProtocol.Write.cs:86-108`, the payload reader's `NullableNameCount` (delete when unused),
  `MFTLib/Broker/Client/BlockScanTarget.cs:18`, `MFTLib/Broker/Client/BrokerProcess.Scan.cs:46,65-68`,
  `MFTLib/Broker/Client/BrokerMftBlockProducer.cs:35-64`, `MFTLib/Broker/Host/JournalBrokerHost.Scan.cs:103,117`,
  `MFTLib/Broker/Host/JournalBrokerHost.Hooks.cs:17`, `MFTLibTestExtensions/ScriptedBrokerResources.cs:50-52`,
  `MFTLibTestExtensions/InProcessBrokerScan.cs`, `MFTLibTestExtensions/BrokerTestHarness.cs:120`,
  `MFTLib.Tests/PublicSurface/MFTLibTestExtensions.approved.txt:24`
- Delete: `MFTLib.Tests/BrokerProtocolTests.Retention.cs`
- Test: `BrokerProtocolTests.cs`, `.Frames.cs`, `.Scan.cs`, `.MalformedPayloads.cs`, `.GrowUsnJournalFrames.cs`,
  `BrokerFrameLengthTests.cs`, `BrokerFrameWriteLimitTests.cs`, `BrokerMftBlockProducerProtocolTests.cs`,
  `BrokerProcessTests.Scan.cs`, `BrokerProcessTests.TestExtensions.cs`, `JournalBrokerHostScanTests.cs`,
  `JournalBrokerHostChannelTests.cs`, `JournalBrokerHostTests.cs`, `JournalBrokerHostLivenessTests.Progress.cs`,
  `TestSupport/HostChannelHarness.cs`

**Interfaces:**
- `static BrokerFrame BrokerFrame.ArmAndScan(string sectionName, bool includeFreed)`.
- Payload: `[sectionName string][includeFreed byte 0 or 1]`, then `RequireComplete`.
- `ArmAndScanFrameLength` and `BrokerMftBlockProducer.ValidateRetainedFileNames` are deleted.
- `JournalBrokerHost.Hooks.ScanStarting : Action<VolumeKey>?`; `ScriptedBrokerResources.RecordScan(VolumeKey volume)`;
  `InProcessBrokerScan` keeps `Volume` only (internal constructor `(VolumeKey volume)`).
- `BrokerProcess.ScanOnChannelAsync` loses its retained-names parameter.

- [ ] **Step 1: Write the failing tests**: `ArmAndScan_RoundTripsSectionAndIncludeFreed` (both flag values);
  `ArmAndScan_TrailingBytesAfterTheFlagAreMalformed` (append a legacy nullable count `00 00 00 00 01` and expect the
  malformed-payload outcome the suite already asserts for other frames); `ArmAndScan_FrameLengthIsKindSectionAndFlag`
  (in `BrokerFrameLengthTests`, `1 + 4 + 2 * section.Length + 1` bytes written).
- [ ] **Step 2: Run, expect FAIL**: `--filter "FullyQualifiedName~BrokerProtocolTests|FullyQualifiedName~BrokerFrameLengthTests"`
- [ ] **Step 3: Implement**; update the public approval for `InProcessBrokerScan`; fix every caller and test
  double the compiler lists.
- [ ] **Step 4: Run, expect PASS**: `--filter "FullyQualifiedName~Broker|FullyQualifiedName~JournalBrokerHost|FullyQualifiedName~PublicSurfaceTests"`
- [ ] **Step 5: Commit** `refactor: drop retained names from the ArmAndScan frame`.

### Task 2.3: Producers write every record; the option is deleted

**Files:**
- Modify: `MFTLib/Index/FileIndexOptions.cs:77,94,108-135`, `MFTLib/Index/RootKind.cs:79-113`,
  `MFTLib/Index/MftIndexSource.cs:25-60`, `MFTLib/Index/MftBlockProducer.cs:66-69` (request field),
  `MFTLib/Index/FileIndex.RootRuntime.Production.cs:80,134`, `MFTLib/Index/EnumerationProducerOptions.cs:20`,
  `MFTLib/Index/EnumerationDirectoryReader.cs:8,20-22,50`, `MFTLib/Sources/MftBlockRowWriter.cs`,
  `MFTLib/Sources/MftBlockScan.cs:28-32`, `MFTLib/Sources/LocalMftBlockProducer.cs:34`,
  `MFTLib/Sources/MftDumpBlockProducer.cs:70`, `MFTLib/Sources/MftBlockProducerPipeline.cs:10`,
  `MFTLib/Broker/SharedMemory/IBlockSectionWriter.cs:11`, `RealBlockSectionWriter.cs:13`,
  `MFTLibTestExtensions/RecordingBlockSectionWriter.cs:26-32`, `MFTLib.Tests/TestSupport/CountingBlockSectionWriter.cs`,
  `MFTLib.Tests/TestSupport/ScriptedScanSteps.cs`, `MFTLib/MftScanOptions.cs:5` (doc), `MFTLib/Index/BlockValidationResult.cs:43` (doc),
  `MFTLib.Tests/PublicSurface/MFTLib.Index.approved.txt:175`
- Delete: `MFTLib/Sources/MftRowSelection.cs`, `MFTLib.Tests/Index/FileIndexRetainedFileNamesAdmissionTests.cs`
- Test: `MftBlockRowWriterTests.cs`, `Index/EnumerationProducerTests.cs`, `Index/FileIndexProducerSelectionTests.cs`,
  `MftProducerEndToEndTests.cs`, `LiveScanInvalidFixupTests.cs`, `LiveVolumeSourcesIncludeFreedTests.cs`,
  `Index/NamedBlockSectionLayoutTests.cs`, `Index/RootKeyTests.cs`, `Index/IndexNavigationTests.cs`

**Interfaces:**
- `IBlockSectionWriter.Write(..., MftScanOptions scanOptions, BlockWriteReporting reporting, CancellationToken)`
  replaces the `MftRowSelection` parameter (spec: include-freed moves onto the scan request);
  `MftBlockProducerPipeline` carries `MftScanOptions ScanOptions` in place of `Selection`.
- `MftBlockRowWriter.WriteBatches(BlockWriter writer, IEnumerable<IReadOnlyList<MftRecord>> batches, MftScanOptions scanOptions, IProgress<BlockWriteProgress>? progress, CancellationToken cancellationToken)`;
  `ShouldKeepRecord` is deleted.
- `MftIndexSource(MftBlockProducer producer, IIndexWatchSource? watchSource = null, MftDumpSourceIdentity? dumpIdentity = null, bool includeFreed = false)`.
- `RecordingBlockSectionWriter.LastRetainedFileNames` is deleted (it is internal; check the approved file).

- [ ] **Step 1: Write the failing tests**: `WriteBatches_WritesEveryInUseRecordWhateverItsName` (files `a.txt`,
  `.git`, `HEAD` and a directory all become rows); `EnumerationWalk_WritesEveryFileWhateverItsName`; keep the
  include-freed cases.
- [ ] **Step 2: Run, expect FAIL** (compile errors until the parameter is gone):
  `--filter "FullyQualifiedName~MftBlockRowWriterTests|FullyQualifiedName~EnumerationProducerTests"`
- [ ] **Step 3: Delete** the option, its validation, the source callback, the request and options fields, the filters
  and `MftRowSelection`; update the approval; fix every caller.
- [ ] **Step 4: Run, expect PASS** with the filter plus
  `FullyQualifiedName~FileIndexProducerSelectionTests|FullyQualifiedName~MftProducerEndToEndTests|FullyQualifiedName~PublicSurfaceTests|FullyQualifiedName~PublicMemberCallerTests`.
- [ ] **Step 5: Commit** `refactor: delete RetainedFileNames; every producer writes every record`.

### Task 2.4: Watch sample drops the retention flags

**Files:** `SampleProgram.Watch/WatchArguments.cs:20-34,39,56`, `SampleProgram.Watch/WatchArguments.Parse.cs:21-27,61-64`,
`SampleProgram.Watch/SampleHost.Broker.cs:167`; test `MFTLib.Tests/WatchArgumentsTests.cs:207-209,224-225,243-246,303-312`.

**Interfaces:** `VerbFlags` lose `--keep-name` and `--directories-only`; `Usage` loses
`[--directories-only | --keep-name A,B]`; `Validate` loses its two retention arms and parameters;
`WatchArguments.CacheTag => new("SMPW", 3)`.

- [ ] **Step 1: Write the failing tests**: `[DataRow(new[] { "watch", "C", "--keep-name", "a" }, "Option --keep-name does not apply to watch.")]`
  and the same for `--directories-only` (unknown flags already report through `NotApplicable`; confirm the exact
  existing wording first), `CacheTag_IsSmpw3`; delete the retention-state tests.
- [ ] **Step 2: Run, expect FAIL**: `--filter FullyQualifiedName~WatchArgumentsTests`
- [ ] **Step 3: Implement.**
- [ ] **Step 4: Run, expect PASS.**
- [ ] **Step 5: Commit** `refactor: drop the Watch sample's retention flags`.

### Task 2.5: Documentation and rulings

**Files:** `docs/api-decisions.md` (supersede "Retention is an index option applied by every producer" (line 745);
narrow "Retained names and pruning share the root comparison" (line 1077) to pruning; edit line 253 (sample-caller
audit), 578 ("No privacy promise": the filter sentence goes), 738-739, 914-915 (vocabulary)), `docs/architecture.md:59,134,174,238`
(and its `SMPW` version 2 text to 3), `docs/index-format.md:26,255-300` (scan-shape encodings), `docs/broker-lifetime.md:129`,
`docs/broker-integration.md:92,123`, `docs/broker-testing.md:87,94`, `README.md:41`, `CHANGELOG.md` (a removal entry; leave
past release entries as history).

- [ ] **Step 1: Run the docs-update check**; `grep -rn "RetainedFileNames\|keep-name\|directories-only\|MftRowSelection" --include=*.md --include=*.cs .`
  returns only `CHANGELOG.md` history lines and `docs/superpowers/` files.
- [ ] **Step 2: Write the superseding ruling** ("Every MFT index stores every name", owner ruling 3 and 4 of MFTLib
  issue 844, with the measured reason from ruling 3).
- [ ] **Step 3: Commit** `docs: remove retention from the contracts`.

### Task 2.6: Slice verification and PR

Same checklist as Task 1.8, with the public-surface step expecting exactly the two deletions above. PR body starts
`## Problem` (retention exists only to shrink a block that format 8 makes small, and it blocks volume sharing), lists
the consumer migration lines by file, and names no added member.

---

# Slice 3: Capacity growth for MFT rescans and independent entry capacity

Expand into tasks when slice 1 merges.

- **Goal:** one `PlanCapacities` routine serves both producers: on rescan each region whose use reached its
  capacity doubles (rows at `S`, entries at `E`, pool within 510 bytes of `P`), the rest keep the volume plan. Entries
  get their own growth so a rename-heavy watch that reaches `E` rescans into a block with only `E` doubled.
- **Depends on:** slice 1 (format 8, `E` in the header and layout).
- **Files likely touched:** `MFTLib/Index/FileIndex.RootRuntime.Production.cs:94-110,183-184` (today's enumeration-only
  doubling), `MFTLib/Index/BlockFileCreateOptions.cs` (add `NameEntryCapacity`), `MFTLib/Index/BlockFile.cs`
  (`InitialHeader` takes `E` from options), `MFTLib/Sources/MftBlockCapacity.cs`, `MFTLib/Broker/Client/BrokerProcess.Scan.cs`
  and `MFTLib/Sources/MftBlockProducerPipeline.cs` (the plan flows to client-side capacity planning as a minimum),
  `MFTLib/Index/EnumerationBlockGrowth.cs` (double only exhausted regions, rebuild buckets for the new `E`, ids preserved),
  `MFTLib/Index/MftBlockProducer.cs` (request carries the minimum plan), broker frames if the section size travels.
- **Contract points:** spec section 4 rows "MFT rescan growth" and "Bucket count"; the paragraph "Entries are never
  reclaimed..."; capacity-exhausted flag semantics in `docs/api-decisions.md` "Capacity exhaustion names the state;
  rescan is the remedy"; `docs/index-format.md` Capacity section.
- **Verification bar:** `BlockLayoutTests` pin `PlanCapacities` for each exhaustion combination; an MFT rescan after
  entry, pool and row exhaustion (separately) grows only that region; enumeration mid-walk growth preserves ids and
  passes `SyntheticNamePool.CheckIntegrity`; the slice-wide checklist of Task 1.8.

# Slice 4: Producer selection and directory MFT views without sharing

Expand into tasks when slice 2 merges (parallel with slices 1 and 3).

- **Goal:** consumers name a `RootKey` and a `ProducerSelection`; the library decides eligibility once at open,
  falls back to a walk only for eligibility reasons under Automatic, fails with `MftUnavailable` under MftOnly, and
  reports `RootStatus.Producer` and `RootStatus.MftUnavailable`. A directory root on an NTFS volume is an MFT view
  of the whole-volume block, rooted at its resolved row. A second same-volume index takes the private fallback until
  slice 5.
- **Depends on:** slice 2 (no retention in the shape or the options).
- **Files likely touched:** delete `MFTLib/Index/IndexedRoot*.cs` (and `IndexedRoot.Discovery.cs`, moving
  `EnumerateWindowsVolumes` to `WindowsVolume.EnumerateAll`), `MFTLib/Index/FileIndexOptions.cs` (`Root : RootKey?`,
  `Producer : ProducerSelection`), new `ProducerSelection.cs`, `MftUnavailableReason.cs`, `RootFailureKind.cs`
  (add `MftUnavailable`, delete `MftSourceMissing`), `RootStatus.cs`, `RootKind.cs` (admission), `ScanShape.For`,
  `CacheDirectory.Names.cs` (MFT blocks always `<letter>-<serial>.mlix`), `FileIndex.RootRuntime.*` (eligibility, view
  root row, `Offline` when the directory disappears), `LookupEngine.cs`, `SearchEngine.cs`, `IndexRowEnumerable.cs`
  (implicit subtree with the two-bitset verdict cache), `FileEntry.cs` (`Parent` null at the view root),
  `FileIndex.QueryUsnJournalSettings`, `WindowsVolume.cs` (`IsIndexable` is Automatic's predicate),
  `FileIndexSet.cs` (reject nested members with `ArgumentException`, owner answer 3),
  `MFTLibTestExtensions/SyntheticIndexedRoot.cs` (delete) and new `SyntheticVolumeFacts.cs`,
  `SampleProgram.Watch` (`--producer automatic|mft|walk`, roots as `RootKey`, print `Producer` and `MftUnavailable`),
  `SampleProgram.Shared/SampleHost.Drive.cs:14`, `SampleProgram.Direct` `SampleHost.Run.cs:94-111`, both PublicSurface
  approvals, `PublicMemberCallerTests`.
- **Contract points:** spec section 1 (whole table, the default `ProducerSelection` value invalid, validation before
  side effects, dump with WalkOnly rejected, producer fixed for the index lifetime, shared UAC prompt through
  `BrokerSession` launch sharing), section 2 "Roots, queries and anchors" minus sharing, section 3 file name and
  `InspectCached`/`DeleteCached` keying, section 5 (walk producer); owner answers 1, 2 and 3. Rulings superseded in
  `api-decisions.md`: "One producer enum, selection and observation", "The enumeration producer is a product feature,
  chosen per root", "Windows volume discovery reports every volume with an MFT verdict", the anchor ruling (per view);
  new ruling "producer selection".
- **Verification bar:** each `MftUnavailableReason` through Automatic (walks) and MftOnly (`Failed`); WalkOnly touches
  no source (a recording source); mid-scan failure stays `Failed` with `ProducerFailed`; producer fixed across rescan;
  dump with WalkOnly rejected; directory view `Find`, unscoped queries, `Parent` and `Children`; view `Offline` and back;
  nested `FileIndexSet` members rejected; `FileIndexProducerSelectionTests` rewritten; the PR body names the consumer
  production caller of every added member (spec section 1 table, third column); Task 1.8 checklist.

# Slice 5: Volume scan sharing

Expand into tasks when slices 3 and 4 merge.

- **Goal:** all MFT views of one share key in one process attach to one `VolumeScan` (block, owner lock, production,
  watch, catch-up, checkpoint-loss facts, write gate); each `FileIndex` is an `IndexView` with its own root row,
  borrows, disposal, watch request, events and anchors.
- **Depends on:** slices 3 and 4.
- **Files likely touched:** split `MFTLib/Index/FileIndex.RootRuntime*.cs` into `VolumeScan*.cs` and `IndexView*.cs`,
  new `VolumeScanRegistry.cs`, `FileIndex.cs`, `FileIndex.Watch*.cs` (requests per view, one watch per scan),
  event routing in the pump, `FileIndex.Rescan*.cs` (coalescing), `RootBlock.cs` and borrow counting,
  `FileEntry.cs` (carries its view; equality compares view, block, row, sequence), `NativeSeamIsolationTests`,
  `MFTLibTestExtensions` registry reset helper, `SampleProgram.Watch` `add-root <path>` console command.
- **Contract points:** spec section 2 entire (share key: source instance, `VolumeKey`, serial, cache directory full
  path or `Disabled`, `CacheTag`; lifetime table; event routing including moves across a view boundary as `Renamed`
  to both views; fault fan-out; coalesced rescans; one recovery per scan); `docs/watch-lifetime.md`,
  `docs/query-lifetime.md` (borrows per view and per block), `docs/block-ownership.md` (one lock per scan),
  `docs/broker-lifetime.md`, `docs/test-isolation.md` (the registry is process-global: classes that open shared scans
  reset it in cleanup and carry `[DoNotParallelize]`); `api-decisions.md`: "Block Generation is a per-root change
  version" becomes per volume block; new ruling "volume scan sharing".
- **Verification bar:** spec section 8 "Sharing" list in full (two views one production counted, concurrent opens,
  attach during Live, cancel one of two opens, dispose order both ways, last detach unmaps after borrows drain, event
  routing in/out/across, fault fan-out, coalesced rescans, checkpoint loss on both views, view `Offline` and back,
  foreign-view anchors rejected, `ExistingOnly` view on a cold scan); `NativeSeamIsolationTests` on Windows and Linux;
  32-worker ClassLevel stress with the existing exclusions; Task 1.8 checklist. Before merge, the slice-6 A/B second
  run reports warm client peak working set at or below 1,359 MB, or the owner receives the profile.

# Slice 6: A/B re-measurement and docs close-out

Expand into tasks when slice 5 merges.

- **Goal:** measure, close the docs, and hand off the consumer pin bumps.
- **Depends on:** slice 5 (the first A/B run happens after slice 1, against today's build).
- **Files likely touched:** none in production; `docs/architecture.md:59,134,236-238`, `docs/index-format.md`,
  `docs/broker-lifetime.md:129`, `docs/broker-integration.md`, `docs/broker-testing.md`, `README.md:41`, `CHANGELOG.md`;
  then the spec and this plan are deleted with their durable content folded into those docs.
- **Contract points:** spec section 8 "A/B re-measurement" (git-wizard harness `measure-retention-ab.ps1`, three
  iterations, C: to G:; block bytes expected about 2.53 GB; cold and warm wall; peak working set and private bytes;
  ordinal-distinct against ignore-case-distinct name count on C:); ruling 12; section 6 consumer migrations (git-wizard
  deletes `WindowsRootClassifier` and closes git-wizard issue 442; file-wizard drive discovery and `IndexedRoot` to
  `RootKey` plus `ProducerSelection`; tags `GWMI` 3, `FILE` 2).
- **Verification bar:** measurements recorded as counts plus the reproduction command (never logs); a warm working
  set miss goes to the owner with the profile; consumer pin-bump PRs green in their own repositories; Task 1.8
  checklist for the docs PR.

---

## Execution notes for the orchestrator

- Slices 1 and 2 dispatch now, each in its own worktree from main. They share no task; their meeting files are listed
  in each slice header.
- Slice 1 Tasks 1.2 and 1.3 are one compile unit and one commit; review them together.
- Both slice-1 questions are decided (see "Orchestrator decisions").
- Both slices branch before PR 845 merges and rebase onto main once it lands; each slice header says where to read
  the spec meanwhile.
- After each slice merges, expand its successors here (the next slice's commit deletes completed slices from this
  file).
