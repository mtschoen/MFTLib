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

# Slice 3: Capacity growth for MFT rescans and independent entry capacity

**Branch:** `feature/844-capacity-growth` from main after slice 1 merged (main at `b96f7daf` or later). One PR.
Runs in parallel with slice 4. **First commit:** replace this plan file's "# Slice 3" section with this text and
delete the completed "# Slice 1" and "# Slice 2" sections (slice 4 edits only its own section).

**Blockers for the orchestrator:** none.

**Goal:** one capacity planner serves both producers. A rescan of a capacity-exhausted block doubles each region
whose use reached its capacity (rows at `S`, entries at `E`, pool within 510 bytes of `P`) and keeps the volume plan
for the rest; the MFT plan is computed where the volume geometry is known (client side), so the index sends the
grown regions as a minimum. Entry capacity becomes a creation option, so an entry-only exhaustion (a rename-heavy
watch) grows `E` alone. Enumeration's mid-walk growth doubles only the exhausted regions.

**Public change:** none. Every added member is internal; no `*.approved.txt` file changes. The capacity-exhausted
flag stays bit 2 and the block format stays version 8 (ruling "Capacity exhaustion names the state; rescan is the
remedy" holds unchanged). No `api-decisions.md` ruling is added or superseded.

## Decisions this slice makes (spec silent or literal reading fails)

1. **Which region is exhausted is derived from the header, not recorded.** Entries are exhausted when
   `NameEntryCount >= NameEntryCapacity`. The pool is exhausted when `NamePoolCapacity - NamePoolUsed <= 510`:
   an interning refusal of a name of at most 255 UTF-16 units (the NTFS and Linux limit) needs at most 510 bytes
   plus one byte of UTF-16 alignment, so a refusal always leaves at most 510 bytes (Task 3.2 pins this). Rows are
   exhausted when `RowCount >= SlotCapacity`, **or** when the flag is set and neither entries nor pool reached
   capacity: a row refusal is the only refusal that leaves no count at capacity, because an MFT write at a record
   index past `S` leaves `RowCount` below `S`. When a row refusal coincides with an entry or pool exhaustion the
   rows keep the volume plan, which for MFT already covers every current record (the fresh volume query sizes `S`
   from today's MFT record count).
2. **Growth is gated by the flag**, as today: a block whose flag is clear asks for no minimum even when a region is
   full, because nothing was refused.
3. **The volume plan sets `E = S` and `P = pageAlign(max(1 MiB, 24 * S))` from the planned `S` before any minimum
   applies.** A row-only minimum therefore raises `S` and leaves `E` and `P` at the volume plan ("the rest keep the
   volume plan").
4. **Estimate clamp:** both estimates clamp at `BlockLayout.MaximumEstimatedRowCount = 1717986917`, the largest
   estimate whose `ComputeSlotCapacity` is `int.MaxValue - 1` (the `DoubleCapacity` clamp), so every planned block
   is creatable. This replaces `MftBlockCapacity`'s `uint.MaxValue / 2` clamp (slice 1 lane report, open question 2)
   and fixes the enumeration estimate's unchecked `topLevel + directories * topLevel`.

## Shared files with slice 4 (parallel)

| File | Slice 3 change | Slice 4 change |
| --- | --- | --- |
| `docs/superpowers/plans/2026-10-27-issue-844-implementation-plan.md` | replaces "# Slice 3", deletes slices 1 and 2 | replaces "# Slice 4" |
| `MFTLib/Index/FileIndex.RootRuntime.Production.cs` | `PlanCapacities`, `RunMftProducerAsync` (request minimum), `CreateAndPopulateBlock`, `CreateEnumerationBlock` (entry capacity); `EnumerationBlockGrowth` moves out | `ProduceRootBlockCoreAsync` (selection, `MftUnavailable`), `Configuration.*` reads become the resolved root |
| `MFTLib/Broker/Client/BrokerMftBlockProducer.cs` | copies `MinimumCapacities` onto the target | maps broker launch failures to `MftUnavailableException` |
| `Benchmark/BenchmarkRunner.Index.cs` | `NameEntryCapacity` at the `BlockFileCreateOptions` site (line 129) | `IndexedRoot` construction (line 191) |
| `MFTLib.Tests/Index/FileIndexMftRescanGrowthTests.cs` (new here) | opens MFT indexes through `IndexTreeFixture.Options(producer: ProducerKind.Mft)` | migrates every MFT open to `SyntheticVolumeFacts.Apply` |
| `docs/index-format.md`, `CHANGELOG.md` | Capacity section; changelog line | file-name and cache sections; changelog lines |

**Merge order:** slice 3 merges first (smaller). Slice 4 rebases and migrates `FileIndexMftRescanGrowthTests` with the
rest of its fixtures. If slice 4 lands first instead, this slice rebases and opens its MFT indexes with
`SyntheticVolumeFacts.Apply(options with { Root = new VolumeKey('T'), Producer = ProducerSelection.MftOnly }, serial)`.

## Review Focus

1. **A rename-heavy watch reaches `E` with rows and pool to spare:** the rescan doubles `E` only and keeps `S` and
   `P` at the volume plan. Task 3.1 (`GrowthMinimum`), Task 3.4 (`Rescan_AfterOneRegionIsExhausted_...` "Entries").
2. **A journal create for a record number past `S`:** flag set, `RowCount < S`; the rescan doubles rows. Task 3.1
   (`GrowthMinimum_FlagWithNoRegionAtCapacity_DoublesRows`), Task 3.4 (`Rescan_AfterARecordIndexBeyondTheSlots_...`).
3. **A pool refusal leaves just over the margin:** a refused name never leaves more than 510 bytes, so the pool is
   always recognised as the exhausted region. Task 3.2 (`PoolRefusal_...`).
4. **A huge MFT record count or a huge enumeration estimate:** planning never throws and never plans an
   uncreatable block. Task 3.1.
5. **The process restarted between exhaustion and rescan:** a warm-started exhausted block still grows on rescan,
   because the counts and flag are in the header. Task 3.4 (`Rescan_OfAWarmStartedExhaustedBlock_...`).

## File map

| File | Responsibility |
| --- | --- |
| Create `MFTLib/Index/BlockCapacities.cs` | The three capacities as one value; elementwise `AtLeast` |
| Modify `MFTLib/Index/BlockLayout.cs` | `PlanCapacities`, `GrowthMinimum`, the estimate clamp and the pool margin constant |
| Modify `MFTLib/Index/BlockFileCreateOptions.cs`, `BlockFile.cs` (`Create`, `InitialHeader`), `NamedBlockSection.cs:33` | Entry capacity is a creation option |
| Modify `MFTLib/Sources/MftBlockCapacity.cs`, `MFTLib/Sources/MftBlockProducerPipeline.cs`, `MFTLib/Index/MftBlockProducer.cs`, `MFTLib/Broker/Client/BlockScanTarget.cs`, `BrokerMftBlockProducer.cs`, `BrokerProcess.Scan.cs` | The minimum travels to client-side planning |
| Modify `MFTLib/Index/EnumerationProducer.cs` | Overflow-free, clamped estimate |
| Modify `MFTLib/Index/FileIndex.RootRuntime.Production.cs`; create `MFTLib/Index/FileIndex.RootRuntime.EnumerationGrowth.cs` | Root runtime plans both producers; mid-walk growth per region |
| Modify `MFTLibTestExtensions/SyntheticBlock.cs`, `RecordingBlockSectionWriter.cs`, `MFTLib.Tests/TestSupport/TestBlocks.cs`, `MFTLib.Tests/Index/SyntheticBlockBuilder.cs`, `MFTLib.Tests/Index/BlockRootKeyStorageTests.cs`, `Benchmark/BenchmarkRunner.Index.cs`, `Benchmark/BenchmarkRunner.EnumerationWalk.cs` | Set the new required option |

**Commands used below:**

```powershell
.\scripts\build-windows.ps1 -NoRestore
dotnet build MFTLib.Tests/MFTLib.Tests.csproj -c Release -p:Platform=x64
dotnet test MFTLib.Tests/MFTLib.Tests.csproj -c Release -p:Platform=x64 --no-build --filter "<filter>"
```

### Task 3.1: The capacity planner

**Files:**
- Create: `MFTLib/Index/BlockCapacities.cs`
- Modify: `MFTLib/Index/BlockLayout.cs`, `MFTLib/Sources/MftBlockCapacity.cs` (delete the local
  `MaximumEstimatedRowCount`; clamp with the layout's), `MFTLib/Index/EnumerationProducer.cs:54-77`
- Test: `MFTLib.Tests/Index/BlockLayoutTests.cs`, `MFTLib.Tests/MftBlockCapacityTests.cs`,
  `MFTLib.Tests/Index/EnumerationProducerTests.cs`

**Interfaces:**
- Produces: `readonly record struct BlockCapacities(uint SlotCapacity, uint NameEntryCapacity, uint NamePoolCapacity)`
  with `BlockCapacities AtLeast(BlockCapacities minimum)` (elementwise `Math.Max`). `default` means no minimum.
- Produces in `BlockLayout`: `internal const uint MaximumEstimatedRowCount = 1717986917;`
  `internal const uint NamePoolExhaustionMarginBytes = 510;`
  `internal static BlockCapacities PlanCapacities(uint estimatedRowCount, BlockCapacities minimum)`: the volume plan
  `S = ComputeSlotCapacity(Math.Min(estimatedRowCount, MaximumEstimatedRowCount))`, `E = S`,
  `P = ComputeNamePoolCapacity(S)`, then `.AtLeast(minimum)`.
  `internal static BlockCapacities GrowthMinimum(in BlockHeader header)`: `default` unless
  `header.IsCapacityExhausted`; otherwise each exhausted region (decision 1) is `DoubleCapacity(S)`,
  `DoubleCapacity(E)` or `DoubleNamePoolCapacity(P)` and every other region is 0.
- Produces in `EnumerationProducer`: `internal static uint EstimateFromCounts(uint topLevel, uint directories)`
  (`ulong` arithmetic, at least `MinimumEstimatedRowCount`, at most `BlockLayout.MaximumEstimatedRowCount`);
  `EstimateRowCount(string)` counts and calls it.

- [x] **Step 1: Write the failing tests** (`BlockLayoutTests`; build headers as `default(BlockHeader)` with the fields set)

```csharp
public void PlanCapacities_WithoutAMinimum_IsTheVolumePlan()
// PlanCapacities(1000000, default) == new BlockCapacities(1250000, 1250000, 30003200)
[DataRow(4000000u, 0u, 0u, 4000000u, 1250000u, 30003200u)]
[DataRow(0u, 2500000u, 0u, 1250000u, 2500000u, 30003200u)]
[DataRow(0u, 0u, 60006400u, 1250000u, 1250000u, 60006400u)]
[DataRow(1000u, 1000u, 4096u, 1250000u, 1250000u, 30003200u)]   // a minimum below the plan changes nothing
public void PlanCapacities_AMinimumRaisesOnlyItsRegion(uint minimumSlots, uint minimumEntries, uint minimumPool,
    uint slots, uint entries, uint pool)                            // estimate 1000000 in every row
public void PlanCapacities_AHugeEstimateClampsToACreatableSlotCapacity()
// PlanCapacities(uint.MaxValue, default).SlotCapacity == int.MaxValue - 1; NamePoolCapacity == 0x7FFFF000
public void GrowthMinimum_WithoutTheFlag_IsNone()                   // RowCount == S, flag clear -> default
[DataRow("Rows")] [DataRow("Entries")] [DataRow("Pool")]
public void GrowthMinimum_DoublesOnlyTheRegionAtCapacity(string region)
// S = E = 64, P = 4096, flag set; Rows: RowCount 64 -> (128, 0, 0); Entries: NameEntryCount 64 -> (0, 128, 0);
// Pool: NamePoolUsed 3586 (510 left) -> (0, 0, 8192)
public void GrowthMinimum_PoolWithMoreThanTheMarginLeft_IsNotExhausted()   // 511 left, entries full -> (0, 128, 0)
public void GrowthMinimum_FlagWithNoRegionAtCapacity_DoublesRows()         // RowCount 6 of 64 -> (128, 0, 0)
public void GrowthMinimum_EntriesAndPoolTogether_DoublesBoth()             // -> (0, 128, 8192)
public void GrowthMinimum_AtTheClamp_ReturnsTheClampedValue()              // S = int.MaxValue - 1 full -> SlotCapacity int.MaxValue - 1
```

  In `MftBlockCapacityTests`, change `EstimateRowCount_RecordCountBeyondThirtyTwoBits_ClampsBelowTheSlotHeadroom` to
  expect `1717986917` and `Plan_RecordCountBeyondThirtyTwoBits_SurvivesTheSlotCapacityHeadroom` to expect slot capacity
  `int.MaxValue - 1`. In `EnumerationProducerTests` add
  `EstimateFromCounts_ProductBeyondThirtyTwoBits_ClampsToTheLayoutMaximum` (`EstimateFromCounts(100000, 100000)` ==
  `1717986917`) and `EstimateFromCounts_SmallTree_IsTheMinimum`.

- [x] **Step 2: Run, expect FAIL** (members missing):
  `--filter "FullyQualifiedName~BlockLayoutTests|FullyQualifiedName~MftBlockCapacityTests|FullyQualifiedName~EnumerationProducerTests"`
- [x] **Step 3: Implement** the Interfaces. `MftBlockCapacity.Plan` and the root runtime keep their current
  signatures in this task (Tasks 3.3 and 3.4 switch them to `PlanCapacities`).
- [x] **Step 4: Run, expect PASS** with the same filter.
- [x] **Step 5: Commit** `feat: plan block capacities per region with a growth minimum` (with this plan file edit).

### Task 3.2: Entry capacity is a creation option

**Files:**
- Modify: `MFTLib/Index/BlockFileCreateOptions.cs`, `MFTLib/Index/BlockFile.cs:199,392-413`,
  `MFTLib/Index/NamedBlockSection.cs:33`, and every creation site the compiler lists once the option is `required`:
  `MftBlockCapacity.CreateOptions`, `FileIndex.RootRuntime.Production.cs` (`CreateEnumerationBlock`),
  `Benchmark/BenchmarkRunner.Index.cs:129`, `Benchmark/BenchmarkRunner.EnumerationWalk.cs:160`,
  `MFTLibTestExtensions/SyntheticBlock.cs:126`, `MFTLibTestExtensions/RecordingBlockSectionWriter.cs`,
  `MFTLib.Tests/TestSupport/TestBlocks.cs` (add `uint? nameEntryCapacity = null` to `Create` and `Options`, null
  meaning `slotCapacity`), `MFTLib.Tests/Index/SyntheticBlockBuilder.cs`, `MFTLib.Tests/Index/BlockRootKeyStorageTests.cs`
- Test: `MFTLib.Tests/Index/BlockFileTests.cs`, `MFTLib.Tests/Index/NamedBlockSectionLayoutTests.cs`,
  `MFTLib.Tests/Index/NamePoolTests.cs`

**Interfaces:**
- Produces: `BlockFileCreateOptions.NameEntryCapacity : required uint` (XML: "Allocatable name entries, independent of
  `SlotCapacity`; use `BlockLayout.PlanCapacities`"); the class summary's "Capacities are final: a block never grows"
  stays true and gains "a rescan or a walk replaces it with a larger block".
- `InitialHeader` sets `NameEntryCapacity = options.NameEntryCapacity`,
  `BucketCount = ComputeBucketCount(options.NameEntryCapacity)`, and the bucket and pool offsets with `E`.
  `BlockFile.Create` and `NamedBlockSection.Create` size the file with
  `TotalBlockBytes(options.SlotCapacity, options.NameEntryCapacity, options.NamePoolCapacity)`.
- Every production creation site passes `E = S` until Tasks 3.3 and 3.4 pass the planner's value.

- [x] **Step 1: Write the failing tests**

```csharp
// BlockFileTests
public void Create_WithAnEntryCapacityOtherThanTheSlotCapacity_LaysOutAndReopens()
// S = 4096, E = 10000, P = 1048576: header NameEntryCapacity 10000, BucketCount 4096, Length ==
// TotalBlockBytes(4096, 10000, 1048576); after Complete, BlockFile.Open (the reopen helper BlockFileTests already uses)
// validates with no result; 10000 distinct names intern, the 10001st is refused and sets the flag.
// NamedBlockSectionLayoutTests (Windows, as the class already is)
public void Create_SizesTheSectionWithTheEntryCapacity()   // E = 3 * S: OpenExisting accepts the section; length as above
// NamePoolTests (Review Focus 3)
[DataRow(509u)] [DataRow(510u)] [DataRow(511u)] [DataRow(512u)]
public void PoolRefusal_OfANtfsLengthName_LeavesAtMostTheExhaustionMargin(uint remaining)
// P = 4096, S = E = 64: intern one ASCII filler of (P - remaining) bytes, then try "é" + 254 'a' (UTF-16) and,
// in a fresh block, 255 ASCII 'b'. For each: refused implies remaining <= BlockLayout.NamePoolExhaustionMarginBytes;
// remaining >= 511 implies both fit.
```

- [x] **Step 2: Run, expect FAIL**: `--filter "FullyQualifiedName~BlockFileTests|FullyQualifiedName~NamedBlockSection|FullyQualifiedName~NamePoolTests"`
- [x] **Step 3: Implement**; fix every site the compiler lists.
- [x] **Step 4: Run, expect PASS**, then `--filter TestCategory=Smoke`.
- [x] **Step 5: Commit** `feat: make the name entry capacity a block creation option`.

### Task 3.3: The minimum reaches client-side MFT planning

**Files:**
- Modify: `MFTLib/Index/MftBlockProducer.cs` (request), `MFTLib/Sources/MftBlockCapacity.cs`,
  `MFTLib/Sources/MftBlockProducerPipeline.cs:35-37`, `MFTLib/Broker/Client/BlockScanTarget.cs`,
  `MFTLib/Broker/Client/BrokerMftBlockProducer.cs:43-47`, `MFTLib/Broker/Client/BrokerProcess.Scan.cs:39-41`,
  `Benchmark/BenchmarkRunner.IndexStorage.cs:26` (passes `default`)
- Test: `MFTLib.Tests/MftBlockCapacityTests.cs`, `MFTLib.Tests/MftIndexSourcesDumpTests.cs`,
  `MFTLib.Tests/BrokerProcessTests.Scan.cs`

**Interfaces:**
- Produces: `MftBlockProduceRequest.MinimumCapacities : BlockCapacities { get; init; }` (XML: "Regions the index needs
  larger than the volume plan, from the block a rescan replaces; `default` asks for the plan. A custom producer that
  plans its own block must honor it.").
- Produces: `BlockScanTarget.MinimumCapacities : BlockCapacities { get; init; }`, copied from the request by
  `BrokerMftBlockProducer`.
- `MftBlockCapacity.Plan(NtfsVolumeInformation volumeInformation, BlockCapacities minimum) : BlockCapacities` returns
  `BlockLayout.PlanCapacities(EstimateRowCount(volumeInformation), minimum)`.
  `MftBlockCapacity.CreateOptions(..., BlockContentIdentity identity, BlockCapacities minimum)` sets all three
  capacities from it. `MftBlockProducerPipeline.Write` passes `request.MinimumCapacities`; `ScanVolumeAsync` passes
  `target.MinimumCapacities`. The broker host needs no change: it validates the section from its header, which now
  carries `E`.

- [x] **Step 1: Write the failing tests**

```csharp
// MftBlockCapacityTests
public void Plan_AMinimumRaisesOnlyItsRegion()
// geometry of 1,000,000 records: Plan(geometry, new(0, 2500000, 0)) == new(1250000, 2500000, 30003200)
// MftIndexSourcesDumpTests (cross-platform, through the real pipeline)
public async Task DumpProducer_ARequestMinimumRaisesOnlyThatRegion()
// produce once with default; then with MinimumCapacities = new(0, 2 * first.NameEntryCapacity, 0):
// second block SlotCapacity == first, NameEntryCapacity == 2 * first, NamePoolCapacity == first.
// BrokerProcessTests.Scan
public async Task ScanVolume_TheTargetMinimumSizesTheSection()
// Target() with { MinimumCapacities = new(0, 0, 2 * baselinePool) }: the section's block has NamePoolCapacity
// 2 * baselinePool and the baseline slot and entry capacities (baseline from a scan with the default target).
```

- [x] **Step 2: Run, expect FAIL**: `--filter "FullyQualifiedName~MftBlockCapacityTests|FullyQualifiedName~MftIndexSourcesDumpTests|FullyQualifiedName~BrokerProcessTests"`
- [x] **Step 3: Implement** the Interfaces; fix the remaining callers.
- [x] **Step 4: Run, expect PASS** with the same filter.
- [x] **Step 5: Commit** `feat: pass the rescan growth minimum to MFT block planning`.

### Task 3.4: The root runtime plans both producers; walks grow only exhausted regions

**Files:**
- Modify: `MFTLib/Index/FileIndex.RootRuntime.Production.cs` (`PlanCapacities` at 93-107, `RunMftProducerAsync`
  at 67-85, `CreateAndPopulateBlock`, `CreateEnumerationBlock`)
- Create: `MFTLib/Index/FileIndex.RootRuntime.EnumerationGrowth.cs` (move `EnumerationBlockGrowth` here, out of the
  file slice 4 also edits)
- Test: create `MFTLib.Tests/Index/FileIndexMftRescanGrowthTests.cs`; modify
  `MFTLib.Tests/Index/FileIndexEnumerationGrowthTests.cs`, `MFTLib.Tests/Index/NamePoolTests.cs`

**Interfaces:**
- Consumes: `BlockLayout.PlanCapacities`, `BlockLayout.GrowthMinimum`, `BlockCapacities` (Task 3.1);
  `BlockFileCreateOptions.NameEntryCapacity` (Task 3.2); `MftBlockProduceRequest.MinimumCapacities` (Task 3.3).
- `RootRuntime.GrowthMinimumLocked() : BlockCapacities`: `GrowthMinimum` of the current block's header, or `default`
  with no block; read under `StateLock`. Replaces the private `PlanCapacities(uint)`.
- `RunMftProducerAsync` sets `MinimumCapacities` from it. `CreateAndPopulateBlock` plans
  `BlockLayout.PlanCapacities(EnumerationProducer.EstimateRowCount(rootDirectory), minimum)`.
- `CreateEnumerationBlock(string blockPath, bool deleteOnClose, BlockCapacities capacities)`.
- `EnumerationBlockGrowth(RootRuntime runtime, string blockPath, bool deleteOnClose, BlockCapacities capacities)`;
  `Grow(BlockWriter exhausted)` computes `next = current.AtLeast(BlockLayout.GrowthMinimum(in exhausted.Block.Header))`,
  returns null when `next == current` (every exhausted region is at its clamp), else snapshots, recreates with
  `next`, restores. The diagnostics line reads
  `Grew block file '{path}': enumeration capacity exhausted at {rows} rows; the walk continues with {S} slots, {E} name entries and {P} name-pool bytes.`

- [x] **Step 1: Write the failing tests**

```csharp
// FileIndexMftRescanGrowthTests ([DoNotParallelize] only if it touches a process-global hook; it should not)
// A recording MftBlockProducer stores each request. Production 1 creates its block from
// TestBlocks.Options(request.BlockPath, ProducerKind.Mft, slotCapacity: 64, namePoolCapacity: 4096,
// nameEntryCapacity: 64) with Root = request.Volume, RootRow = 5, VolumeSerial, CacheTag, ScanShape and DeleteOnClose
// from the request; writes root row 5; exhausts the named region through BlockWriter; completes. Later productions
// write a healthy block from MftBlockCapacity-sized options the same way.
[DataRow("Rows", 128u, 0u, 0u)]        // rows 6..63 written, a write at row 64 refused
[DataRow("Entries", 0u, 128u, 0u)]     // renames intern names until NameEntryCount == 64
[DataRow("Pool", 0u, 0u, 8192u)]       // one ASCII name to 3996 bytes used, then a 255-unit UTF-16 name refused
public async Task Rescan_AfterOneRegionIsExhausted_RequestsOnlyThatRegionDoubled(string region,
    uint slots, uint entries, uint pool)
// Requests[0].MinimumCapacities == default; after RescanAsync, Requests[1].MinimumCapacities == new(slots, entries, pool).
public async Task Rescan_AfterARecordIndexBeyondTheSlots_RequestsTheRowsDoubled()   // only a write at row 1000 refused
public async Task Rescan_OfAHealthyBlock_RequestsNoMinimum()                         // third request after a healthy rescan is default
public async Task Rescan_OfAWarmStartedExhaustedBlock_RequestsTheGrowth()
// Persistent cache: open, exhaust entries, dispose; reopen (warm start), RescanAsync: the request carries (0, 128, 0).
// FileIndexEnumerationGrowthTests: extend Open_ATreeBeyondTheFirstCapacityPlan... (its pool is what runs out)
// block = index.CurrentBlock!.Block: SlotCapacity and NameEntryCapacity equal the first plan's
// (PlanCapacities(EnumerationProducer.EstimateRowCount(TreeRoot), default)); NamePoolCapacity is at least twice the
// first plan's; NamePoolIntegrity.Check(block) is null; the diagnostics line names all three capacities.
// NamePoolTests
public void Restore_IntoABlockWithOnlyTheEntryCapacityDoubled_PreservesIdsAndIntegrity()
// S = E = 64 filled to 64 entries; BlockRowSnapshot.Capture; restore into S = 64, E = 128: every row's NameId and Name
// unchanged, BucketCount == ComputeBucketCount(128), NamePoolIntegrity.Check is null, a 65th name interns.
```

- [x] **Step 2: Run, expect FAIL**: `--filter "FullyQualifiedName~FileIndexMftRescanGrowthTests|FullyQualifiedName~FileIndexEnumerationGrowthTests|FullyQualifiedName~NamePoolTests"`
- [x] **Step 3: Implement** the Interfaces. Delete `BlockLayout.DoubleCapacity` and `DoubleNamePoolCapacity` callers
  outside `GrowthMinimum` (the functions stay, used by it).
- [x] **Step 4: Run, expect PASS** with the same filter, then
  `--filter "TestCategory=Smoke|FullyQualifiedName~CapacityExhaustionTests|FullyQualifiedName~FileIndexProducerSelectionTests|FullyQualifiedName~EnumerationProducerTests"`.
- [x] **Step 5: Commit** `feat: grow only the exhausted regions on rescan and mid-walk`.

### Task 3.5: Documentation

**Files:** `docs/index-format.md` ("Capacity" section, lines 403-420), XML comments on `BlockFileCreateOptions`,
`MftBlockProduceRequest.MinimumCapacities`, `BlockLayout.PlanCapacities`, `GrowthMinimum`,
`RootBlockStatus.IsCapacityExhausted` (says a rescan grows the exhausted region), `CHANGELOG.md` (0.3.0 section).

- [x] **Step 1: Run the docs-update check.** Rewrite the Capacity section: the volume plan (`S`, `E = S`, `P`), the
  estimate clamp at 1,717,986,917 rows, the three exhaustion tests of decision 1 with the 510-byte margin and why
  rows are the fallback, rescan growth for both producers (doubling only exhausted regions, the minimum the index
  sends to the MFT producer), and mid-walk growth doubling only exhausted regions with ids preserved and buckets
  rebuilt for the new `E`. Delete "the entry capacity follows the slot capacity". `grep -n "follows the slot capacity\|both capacities doubled" docs/*.md MFTLib/**/*.cs`
  returns nothing.
- [x] **Step 2: CHANGELOG:** "A rescan of a capacity-exhausted block doubles only the exhausted region (rows, name
  entries or name bytes), for MFT volumes and directory walks alike."
- [x] **Step 3: Commit** `docs: describe per-region capacity growth`.

### Task 3.6: Slice verification and PR

- [x] Full build `.\scripts\build-windows.ps1 -NoRestore`; full suite
  `dotnet test MFTLib.Tests/MFTLib.Tests.csproj -c Release -p:Platform=x64 --no-build`: 0 failed. Linux through CI
  (`test / linux`); `./scripts/coverage-linux.sh` under WSL when available.
- [x] `--filter "FullyQualifiedName~PublicSurfaceTests|FullyQualifiedName~PublicMemberCallerTests"` passes with no
  approved-file change.
- [x] `.\scripts\run-coverage.ps1 -NonInteractive`, then `pwsh -NoProfile -File scripts/test-coverage-status.ps1`.
- [x] `aislop scan .` is 100 with zero unsuppressed findings; `git diff --check` clean; `AGENTS.md` untouched.
- [x] PR body starts `## Problem` (an MFT rescan after exhaustion recreates a block of the same size, so a watch
  that exhausted entries or rows exhausts again; and a rename-heavy watch can only grow every region together),
  cites MFTLib issue 844, states "no public member added; no approved file changed", and lists the shared files above.

# Slice 4: Producer selection and directory MFT views without sharing

**Branch:** `feature/844-producer-selection` from main after slice 1 merged (main at `b96f7daf` or later). One PR.
Runs in parallel with slice 3. **First commit:** replace this plan file's "# Slice 4" section with this text (slice 3
deletes slices 1 and 2 and replaces its own section; the second to merge resolves the plan file).

## Orchestrator rulings for this slice

1. **B1 accepted.** `SyntheticVolumeFacts.Apply(FileIndexOptions options, uint volumeSerial, bool isNtfs = true,
   bool isLocal = true, VolumeKey? volume = null, string? volumeRootDirectory = null)`: with both optional parameters
   set, synthetic volume `volume` is rooted at host directory `volumeRootDirectory` (a volume key for it resolves there,
   and a directory key under that host path belongs to it); unset, the facts describe the real volume of
   `options.Root`. The PR amends the spec's section 1 "Added (test package)" row with this signature.
2. **Decision 3 below is overruled for the null-source case.** A null `MftSource` is not a volume fact: it is checked
   last, and only when the open must produce. An eligible volume (every other check passes) with a cached MFT block that
   the cache mode admits adopts it with `Producer = Mft` and no source; a later rescan or catch-up that needs the
   source fails with `RootFailureKind.MftUnavailable` and `Status.MftUnavailable = NoMftSource`, keeps the retained
   block, and never walks (the producer is fixed). Only when there is no adoptable block does a null source make
   Automatic walk with `NoMftSource` (and MftOnly fail with it). Reason: file-wizard's cache-only host
   (`FileWizard/FileIndexHost.cs`) opens with `CacheMode.ExistingOnly` and a null source to read cached MFT blocks;
   it must keep doing so, and its `MftSourceMissing` display maps to `MftUnavailable` with `NoMftSource`. Record this
   as part of the new "producer selection" ruling in `api-decisions.md`, with tests for: cached block plus null source
   adopts as Mft; its rescan fails `NoMftSource` without walking; no block plus null source walks under Automatic;
   `ExistingOnly` plus null source plus no block declines without walking.

## Goal

A consumer names a `RootKey` and a `ProducerSelection`. The library reads the volume facts once at open, before
cache lookup, and picks the producer: Automatic takes the MFT when eligible and otherwise walks with the reason on
`Status`; MftOnly fails with `RootFailureKind.MftUnavailable`; WalkOnly never touches the MFT source. A directory root
on an eligible volume is an MFT view of the whole-volume block, rooted at its resolved row. `IndexedRoot` is deleted.
A second index on the same volume still contends for the block's owner lock (private fallback) until slice 5.

## Public surface changes (approved files updated in the commit that makes them)

`MFTLib.Tests/PublicSurface/MFTLib.Index.approved.txt`:

| Change | Approved lines | Consumer production caller of the added member (spec section 1, third column) |
| --- | --- | --- |
| Add `ProducerSelection` | `MFTLib.Index.ProducerSelection :: type public enum : uint, System.IComparable, System.IConvertible, System.IFormattable, System.ISpanFormattable` and fields `Automatic = 1`, `MftOnly = 2`, `WalkOnly = 3` | git-wizard `EnumerationDiscovery.cs` and `MftIndexSession.cs` (`--no-mft` is WalkOnly, otherwise Automatic); file-wizard `ScannerState.CreateRoot`; Watch sample `--producer` |
| Add `FileIndexOptions.Producer` | `MFTLib.Index.FileIndexOptions :: property public Producer { get; init; } : MFTLib.Index.ProducerSelection` | same callers |
| Add `RootStatus.Producer` | `MFTLib.Index.RootStatus :: property public Producer { get; init; } : MFTLib.Index.ProducerKind?` | git-wizard `MftIndexCacheStatusFormatter.cs` and discovery output; file-wizard `FileIndexHost.cs` drive card |
| Add `RootStatus.MftUnavailable` | `MFTLib.Index.RootStatus :: property public MftUnavailable { get; init; } : MFTLib.Index.MftUnavailableReason?` | file-wizard `DriveDiscovery.SkipReason` text; git-wizard discovery diagnostics |
| Add `MftUnavailableReason` | type line as for `ProducerSelection`; fields `PlatformWithoutMft = 1`, `VolumeNotReady = 2`, `NotLocalVolume = 3`, `NotNtfs = 4`, `NoMftSource = 5`, `BrokerUnavailable = 6`, `ElevationDeclined = 7` | same callers |
| Replace `RootFailureKind.MftSourceMissing = 4` | `MFTLib.Index.RootFailureKind :: field public const MftUnavailable : MFTLib.Index.RootFailureKind = 4` | git-wizard and file-wizard failure display (switch on `RootFailureKind`) |
| Add `WindowsVolume.EnumerateAll` | `MFTLib.Index.WindowsVolume :: method public static EnumerateAll() : System.Collections.Generic.IReadOnlyList<MFTLib.Index.WindowsVolume>` | file-wizard `DriveDiscovery.cs:61`; Watch `SampleHost.Drives.cs:14` |
| Change `FileIndexOptions.Root` | `... property public Root { get; init; } : MFTLib.Index.RootKey?` | |
| Delete `IndexedRoot` | every `MFTLib.Index.IndexedRoot ::` line (15 lines, including `EnumerateWindowsVolumes`, `FromDirectory`, `FromWindowsDrive`) | |

`MFTLib.Tests/PublicSurface/MFTLibTestExtensions.approved.txt`: delete both `MFTLibTestExtensions.SyntheticIndexedRoot ::`
lines; add `MFTLibTestExtensions.SyntheticVolumeFacts :: type public static class` and
`MFTLibTestExtensions.SyntheticVolumeFacts :: method public static Apply(MFTLib.Index.FileIndexOptions options, uint volumeSerial, bool isNtfs = true, bool isLocal = true, MFTLib.Index.VolumeKey? volume = default, string? volumeRootDirectory = default) : MFTLib.Index.FileIndexOptions`
(per B1; callers MFTLib.Tests, and git-wizard and file-wizard tests replacing `SyntheticIndexedRoot.Create`).

`MFTLib.Tests/PublicMemberCallerTests.cs`: delete the `IndexedRoot.FromDirectory` exemption. Every added member has a
sample caller (Task 4.7), so no exemption is added.

## Decisions this slice makes (spec silent)

1. **Order of eligibility checks** (first failure wins): `PlatformWithoutMft`; no drive letter for the root's volume
   (UNC, unlettered mount point) is `NotLocalVolume`; `VolumeNotReady`; drive type other than fixed or removable is
   `NotLocalVolume`; format not `NTFS` by `OrdinalIgnoreCase` is `NotNtfs`; null `MftSource` is `NoMftSource`.
   `WindowsVolume.IsIndexable` is the same function with a source assumed present.
2. **Existence before selection.** A root directory missing at open settles `Offline` with `Producer` null, exactly
   as today; selection runs only for an existing root. `Producer` is null only in that state. Unreadable volume facts
   (an `IOException`, `UnauthorizedAccessException` or `SecurityException` from the facts reader) are
   `VolumeNotReady` with serial 0, never an exception from `OpenAsync`.
3. **MftOnly reports `Producer = Mft` even when ineligible**, because the producer is fixed for the index lifetime.
   Its open fails before cache lookup; its rescan returns the same `MftUnavailable` failure without touching the
   source (eligibility is decided once). The null-source case follows orchestrator ruling 2 above.
4. **A broker launch or elevation failure during a rescan** (producer already fixed to MFT) settles as
   `RootFailureKind.MftUnavailable` with `Status.MftUnavailable` set to `BrokerUnavailable` or `ElevationDeclined`,
   keeping any retained block; it never walks. `RootStatus.MftUnavailable` therefore means "the MFT could not be
   used at the last selection or scan attempt"; a successful MFT publication clears it. `ProducerFailed` stays "the
   producer ran and failed".
5. **Cache file names:** an MFT block is named for its volume (`<letter>-<serial>.mlix`) whatever the view's root; a
   walk block keeps today's name for its root key (`<letter>-<serial>` for a walked volume root, `D<hash>-<serial>`
   for a directory). Warm start still discards a block whose stored producer differs. The Automatic fallback of a
   volume root therefore keeps its cache slot; a directory root's fallback releases the volume slot and takes its
   directory slot.
6. **Events on a directory view are volume-wide in this slice.** `Changed` delivers every change the watch applies;
   routing to view scopes is slice 5 ("event routing" is in its file list). The ruling text says so until slice 5
   rewrites it.
7. **The anchor ruling is unchanged here.** With one index per block, "the anchor's block is the borrowed block"
   already rejects every foreign anchor; slice 5 rewrites it per view when two views share a block.
8. **The verdict cache is shared by a query's parallel partitions**: a row's `inside` bit is published with
   `Interlocked.Or` before its `known` bit, and readers read `known` with `Volatile.Read` before `inside`; verdicts
   are deterministic, so concurrent writers agree. The same membership test serves an explicit `Subtree` anchor,
   replacing `IndexNavigation.IsUnder` in the query engines.

## Shared files with slice 3 (parallel)

| File | Slice 4 change | Slice 3 change |
| --- | --- | --- |
| `docs/superpowers/plans/2026-10-27-issue-844-implementation-plan.md` | "# Slice 4" section | slices 1 to 3 |
| `MFTLib/Index/FileIndex.RootRuntime.Production.cs` | `ProduceRootBlockCoreAsync`, `Configuration.*` reads | capacity planning; `EnumerationBlockGrowth` moves to `FileIndex.RootRuntime.EnumerationGrowth.cs` |
| `MFTLib/Broker/Client/BrokerMftBlockProducer.cs` | connect-failure mapping | `MinimumCapacities` onto the target |
| `Benchmark/BenchmarkRunner.Index.cs` | line 191 root construction | line 129 `NameEntryCapacity` |
| `MFTLib.Tests/Index/FileIndexMftRescanGrowthTests.cs` (slice 3, new) | migrate its MFT opens to `SyntheticVolumeFacts.Apply` | creates it |
| `docs/index-format.md`, `CHANGELOG.md` | file name, cache, root key text; changelog lines | Capacity section; one changelog line |

**Merge order:** slice 3 merges first; this slice rebases onto it and migrates `FileIndexMftRescanGrowthTests`
with Task 4.3's recipe. If this slice lands first, slice 3 rebases instead (its plan says how).

## Review Focus

1. **A removable NTFS drive (USB disk):** Automatic scans its MFT, and `WindowsVolume.IsIndexable` is true for it.
   Task 4.1 (`ReadVolumeVerdicts_RemovableNtfsIsIndexable`), Task 4.2 (`Automatic_EligibleRemovableVolumeScansTheMft`).
2. **The user declines the UAC prompt under Automatic:** the index walks, reports `Producer = Enumeration` and
   `MftUnavailable = ElevationDeclined`, and concurrent opens see one prompt. Task 4.4.
3. **The directory a view is rooted at is renamed away during a watch, then back:** the view reads `Offline` with no
   block and empty queries, then `Ready` again, with no exception from any query. Task 4.5.
4. **An unscoped search of a directory view on a large volume:** cost stays linear in rows, because each
   directory's ancestry is climbed once per query. Task 4.6 (`Contains_ClimbsEachDirectoryOncePerQuery`).
5. **A root path typed with other separators or casing** (`t:/SRC/` against the stored `T:\src`): it names the
   same view on Windows, and `Find` accepts either spelling under the view. Task 4.5.

## File map

| File | Responsibility |
| --- | --- |
| Create `MFTLib/Index/ProducerSelection.cs`, `MftUnavailableReason.cs` | Public enums |
| Create `MFTLib/Index/VolumeFacts.cs` | `VolumeFacts`, `VolumeFactsReader` and the platform reader |
| Create `MFTLib/Index/MftEligibility.cs` | The one eligibility predicate |
| Create `MFTLib/Index/WindowsVolume.Discovery.cs` (from `IndexedRoot.Discovery.cs`); modify `WindowsVolume.cs` | `EnumerateAll`, verdicts through the predicate |
| Create `MFTLib/Index/ResolvedRoot.cs`, `RootResolution.cs` | What an open resolved: key, directories, volume, serial, producer, reason |
| Create `MFTLib/Index/MftUnavailableException.cs`, `MFTLib/Broker/Client/BrokerLaunchDeclinedException.cs`, `BrokerEndedException.cs` | Typed launch failures |
| Create `MFTLib/Index/ViewRoot.cs`, `SubtreeMembership.cs` | View root binding; two-bitset verdict cache |
| Delete `MFTLib/Index/IndexedRoot.cs`, `IndexedRoot.Directory.cs`, `IndexedRoot.Windows.cs`, `IndexedRoot.Discovery.cs`, `MFTLibTestExtensions/SyntheticIndexedRoot.cs` | |
| Create `MFTLibTestExtensions/SyntheticVolumeFacts.cs` | Test-package forwarding to the facts seam |
| Modify `FileIndexOptions.cs`, `RootKind.cs`, `RootStatus.cs`, `RootFailureKind.cs`, `RootFailure.cs`, `ScanShape.cs`, `MftDumpSourceIdentity.cs`, `RootBlock.cs`, `FileIndex.cs`, `FileIndex.Queries.cs`, `FileIndex.PendingRootResult.cs`, `FileIndex.RootRuntime*.cs`, `LookupEngine.cs`, `SearchEngine.cs`, `IndexRowEnumerable.cs`, `IndexNavigation.cs`, `FileEntry.Navigation.cs`, `FileIndexSet.cs`, `FileIndex.JournalSettings.cs` | Selection, views, implicit subtree |
| Modify `MFTLib/Broker/Client/BrokerProcess.Launch.cs`, `BrokerSession.cs`, `BrokerMftBlockProducer.cs` | Launch failures become eligibility reasons |
| Modify samples, `Benchmark/BenchmarkRunner.Index.cs`, `BenchmarkRunner.Memory.cs` | Roots as `RootKey`, `--producer` |

**Commands:** as slice 3 (`.\scripts\build-windows.ps1 -NoRestore`, `dotnet build` of the test project, `dotnet test
... --no-build --filter`).

### Task 4.1: Volume facts, the eligibility predicate and `WindowsVolume.EnumerateAll`

**Files:**
- Create: `MFTLib/Index/MftUnavailableReason.cs`, `MFTLib/Index/VolumeFacts.cs`, `MFTLib/Index/MftEligibility.cs`,
  `MFTLib/Index/WindowsVolume.Discovery.cs`
- Modify: `MFTLib/Index/WindowsVolume.cs` (partial; XML for `IsIndexable`, `IsReady`, `Format`), delete
  `MFTLib/Index/IndexedRoot.Discovery.cs`, `SampleProgram.Watch/SampleHost.Drives.cs:14`, both approved files' rows
  for `EnumerateAll`, `EnumerateWindowsVolumes` and `MftUnavailableReason`
- Test: create `MFTLib.Tests/Index/MftEligibilityTests.cs`, `MFTLib.Tests/Index/WindowsVolumeDiscoveryTests.cs` (move
  `ReadVolumeVerdicts_*` out of `IndexedRootTests.cs`)

**Interfaces:**
- `public enum MftUnavailableReason : uint` with the seven values above, each with an XML line.
- `internal readonly record struct VolumeFacts(bool PlatformHasMft, VolumeKey? Volume, string? VolumeRootDirectory,
  uint VolumeSerial, bool IsReady, DriveType DriveType, string? Format)`.
- `internal delegate VolumeFacts VolumeFactsReader(RootKey root, string rootDirectory);`
- `static class PlatformVolumeFacts { internal static VolumeFacts Read(RootKey root, string rootDirectory) }`:
  Windows reads the volume path (`HandleBoundFile.VolumePathOf`), its letter, `DriveInfo` type, readiness and format,
  and the serial (`GetVolumeInformationW`, moved from `IndexedRoot.Windows.cs`); the three exception kinds of
  decision 2 give `IsReady = false`. Elsewhere `PlatformHasMft = false`, serial 0. Reads no `MftSource`, opens no
  volume handle.
- `static class MftEligibility { internal static MftUnavailableReason? Evaluate(in VolumeFacts facts, bool hasMftSource) }`
  in decision 1's order.
- `public static IReadOnlyList<WindowsVolume> WindowsVolume.EnumerateAll()` (`[SupportedOSPlatform("windows")]`) and
  `internal static IReadOnlyList<WindowsVolume> ReadVolumeVerdicts<T>(...)` (moved). Readiness and format are read for
  fixed and removable roots; `IsIndexable = MftEligibility.Evaluate(facts, hasMftSource: true) is null`; the
  `removable` reason is deleted.

- [ ] **Step 1: Write the failing tests**

```csharp
// MftEligibilityTests: one eligible baseline (Windows, T, ready, Fixed, "NTFS", source) and each departure
[DataRow("Platform", MftUnavailableReason.PlatformWithoutMft)]
[DataRow("NoLetter", MftUnavailableReason.NotLocalVolume)]
[DataRow("NotReady", MftUnavailableReason.VolumeNotReady)]
[DataRow("Network", MftUnavailableReason.NotLocalVolume)]
[DataRow("Optical", MftUnavailableReason.NotLocalVolume)]
[DataRow("ExFat", MftUnavailableReason.NotNtfs)]
[DataRow("NoSource", MftUnavailableReason.NoMftSource)]
public void Evaluate_ReportsTheFirstFailedFact(string departure, MftUnavailableReason expected)
public void Evaluate_EligibleFixedAndRemovableNtfs_IsNull()        // "ntfs" lower case included
public void Evaluate_PlatformBeatsEveryOtherReason()                 // every fact bad -> PlatformWithoutMft
// WindowsVolumeDiscoveryTests (moved cases keep their expectations except removable)
public void ReadVolumeVerdicts_RemovableNtfsIsIndexable()            // Review Focus 1: IsReady true, Format "NTFS", Reason null
public void ReadVolumeVerdicts_RemovableNotReadyReportsNotReady()
[TestCategory("Windows")] public void EnumerateAll_ReportsTheSystemVolumeIndexable()   // Windows-only, Inconclusive elsewhere
```

- [ ] **Step 2: Run, expect FAIL**: `--filter "FullyQualifiedName~MftEligibilityTests|FullyQualifiedName~WindowsVolumeDiscoveryTests"`
- [ ] **Step 3: Implement**; the Watch sample calls `WindowsVolume.EnumerateAll()`.
- [ ] **Step 4: Run, expect PASS** with the filter plus `FullyQualifiedName~PublicSurfaceTests`.
  `PublicMemberCallerTests` reports `MftUnavailableReason` unreferenced until Task 4.2's sample edit; run it from
  Task 4.2 on.
- [ ] **Step 5: Commit** `feat: read volume facts and decide MFT eligibility in one predicate` (with this plan edit).

### Task 4.2: Producer selection replaces `IndexedRoot` (production code)

One compile unit with Task 4.3: do both, build, then commit once.

**Files:**
- Create: `MFTLib/Index/ProducerSelection.cs`, `MFTLib/Index/ResolvedRoot.cs`, `MFTLib/Index/RootResolution.cs`,
  `MFTLibTestExtensions/SyntheticVolumeFacts.cs`
- Delete: `MFTLib/Index/IndexedRoot.cs`, `IndexedRoot.Directory.cs`, `IndexedRoot.Windows.cs`,
  `MFTLibTestExtensions/SyntheticIndexedRoot.cs`
- Modify: `MFTLib/Index/FileIndexOptions.cs`, `RootKind.cs:89-106`, `RootStatus.cs`, `RootFailureKind.cs`,
  `RootFailure.cs:18`, `ScanShape.cs:23`, `MftDumpSourceIdentity.cs:27`, `FileIndex.cs:46-55,105-125`,
  `FileIndex.PendingRootResult.cs`, `FileIndex.RootRuntime.cs:70-85`, `FileIndex.RootRuntime.Open.cs:13-20`,
  `FileIndex.RootRuntime.Production.cs:14-58,73,125,131,151`, `FileIndex.RootRuntime.CacheOwnership.cs:33,80`,
  `FileIndex.RootRuntime.ScanFiles.cs:24,36,75`, `FileIndex.RootRuntime.Publication.cs:15`,
  `FileIndex.RootRuntime.Status.cs` (`DescribeLocked`, `QueryUsnJournalSettings`),
  `FileIndex.RootRuntime.WarmStart.cs:25-61`, `Benchmark/BenchmarkRunner.Index.cs:191`,
  `Benchmark/BenchmarkRunner.Memory.cs:90`, `SampleProgram.Shared/SampleHost.Drive.cs`,
  `SampleProgram.Direct/SampleHost.Run.cs:94-111`, `SampleProgram.Direct/SampleHost.Verbs.cs:16`,
  `SampleProgram.Watch/SampleHost.Broker.cs:24,56-60,124,153`, `SampleProgram.Watch/SampleHost.Run.cs`, both approved
  files (rows in the table above except `EnumerateAll`), `MFTLib.Tests/PublicMemberCallerTests.cs:49`
- Test: rewrite `MFTLib.Tests/Index/FileIndexProducerSelectionTests.cs` (keep its rescan and invalid-block cases,
  migrated); create `MFTLib.Tests/Index/FileIndexOptionsValidationTests.cs` (absorbs `IndexedRootTests` admission cases)

**Interfaces:**
- `public enum ProducerSelection : uint { Automatic = 1, MftOnly = 2, WalkOnly = 3 }`; XML says the default value is
  invalid.
- `FileIndexOptions`: `public RootKey? Root { get; init; }`; `public ProducerSelection Producer { get; init; } =
  ProducerSelection.Automatic;` `internal VolumeFactsReader? VolumeFacts { get; init; }` (null reads
  `PlatformVolumeFacts.Read`). `MftSource` XML: a null source is checked last, only when the open must produce (orchestrator ruling 2).
  `ValidateRoot` returns `RootKey`.
- `RootKind.PrepareOpenOptions`, all before any side effect: `!Enum.IsDefined(options.Producer)` throws
  `ArgumentOutOfRangeException` (0 included); a dump source runs `MftDumpSourceIdentity.ValidateOptions`, then
  `Producer == WalkOnly` throws `ArgumentException("A dump source is an MFT inventory, so FileIndexOptions.Producer cannot be WalkOnly.")`;
  otherwise a null `Root` throws (today's message) and a default `RootKey` throws `ArgumentException` through
  `RootKey.Validated()`.
- `internal sealed record ResolvedRoot`: `RootKey Root`, `string RootDirectory` (normalized host path of the root;
  dump: its virtual root), `VolumeKey? Volume`, `string? VolumeRootDirectory`, `uint VolumeSerial`,
  `ProducerKind Producer`, `MftUnavailableReason? MftUnavailable`, `MftIndexSource? MftSource` (non-null exactly when
  `Producer == Mft` and `MftUnavailable` is null), `bool IsDump`; `RootKey BlockRoot` (MFT: `Volume`; walk: `Root`);
  `string RenderRootDirectory` (MFT: `VolumeRootDirectory`; walk and dump: `RootDirectory`).
- `static class RootResolution { internal static ResolvedRoot Resolve(FileIndexOptions prepared) }`: dump from its
  identity (serial 0, MFT); otherwise `rootDirectory` is the facts' `VolumeRootDirectory` for a volume key and the
  key text for a directory key, then WalkOnly selects Enumeration with `MftUnavailable` null, Automatic selects MFT or
  Enumeration with the reason, MftOnly selects MFT with the reason (or null). The facts reader is called once.
- `RootRuntime(FileIndex index, ResolvedRoot resolved)`; property `ResolvedRoot Resolved` replaces `Configuration`;
  `Volume => Resolved.Volume ?? throw`; `Kind` and `MatchableRootDirectoryPath` move from `FileIndex`'s constructor
  to the runtime (Task 4.4 can swap the resolved root during open), and `FileIndex.Kind` and
  `FileIndex.MatchableRootDirectoryPath` forward to them.
- `AddRootAsync`: the existence check uses `Resolved.RootDirectory` (dumps skip it, as today); then
  `Resolved is { Producer: Mft, MftUnavailable: { } reason }` records `Failed` with `RootFailureKind.MftUnavailable`,
  message `Root {root}: the MFT cannot serve this root ({reason}) and FileIndexOptions.Producer is MftOnly.`, before
  any cache work. `ProduceRootBlockCoreAsync` returns that same failure for a rescan, picks the walk for Enumeration,
  and otherwise reads `Resolved.MftSource`; the `MftSourceMissing` branch is deleted.
- Block naming and creation use `Resolved.BlockRoot` and `Resolved.VolumeSerial`; the MFT request's `Volume` is
  `Resolved.Volume`; `RootBlock`'s `rootDirectoryPath` is `Resolved.RenderRootDirectory`; `ScanShape.For(ResolvedRoot
  root, FileIndexOptions options)`; warm start compares with `Resolved.Producer` and `Resolved.RootDirectory`.
- `RootStatus`: `public ProducerKind? Producer { get; init; }`, `public MftUnavailableReason? MftUnavailable { get; init; }`;
  `DescribeLocked` sets both (Producer null only for the open-time Offline of decision 2; MftUnavailable per decision 4).
  `PendingRootResult.MftUnavailable : MftUnavailableReason?`. Class XML lists `Failed` with `MftUnavailable` and a
  retained block with `ProducerFailed` or `MftUnavailable`.
- `RootFailureKind.MftUnavailable` (4): "the MFT could not serve this root: MftOnly found it ineligible, or a broker
  launch or elevation failed; `RootStatus.MftUnavailable` says why".
- `QueryUsnJournalSettings`: `Resolved is { Producer: Mft, IsDump: false, Volume: { } volume }` queries that volume;
  a walked root throws `InvalidOperationException` naming the root as walked; a dump keeps today's reason.
- `SyntheticVolumeFacts.Apply(...)` per B1: returns `options with { VolumeFacts = reader }` (an internal init the test
  package reaches through `InternalsVisibleTo`, as other forwarders do); `reader` reports `PlatformHasMft` true,
  ready, `DriveType.Fixed` or `DriveType.Network` from `isLocal`, `"NTFS"` or `"exFAT"` from `isNtfs`, the serial,
  and the mapping. Throws `ArgumentNullException` for null options and `ArgumentException` when only one of
  `volume` and `volumeRootDirectory` is set.
- Samples compile against the new options: Direct opens `Root = new VolumeKey(letter)` with
  `Producer = ProducerSelection.MftOnly` (it demonstrates MFT scans and never walks) and prints
  `Producer {status.Producer}`; Watch opens `Root = new VolumeKey(letter)` with Automatic and prints
  `producer {status.Producer?.ToString() ?? "none"}` plus `MFT unavailable: {reason}` when set. The `resolveRoot`
  seams (`Func<string, IndexedRoot>`) become `Func<FileIndexOptions, FileIndexOptions> prepareOptions`; production
  passes `options => options`.

- [ ] **Step 1: Write the failing tests** (`FileIndexProducerSelectionTests`, `FileIndexOptionsValidationTests`; a
  recording `MftIndexSource` counts producer and watch-source calls; facts through `SyntheticVolumeFacts.Apply` or,
  for platform and readiness, an internal `VolumeFacts` reader)

```csharp
[DataRow(MftUnavailableReason.PlatformWithoutMft)] [DataRow(MftUnavailableReason.VolumeNotReady)]
[DataRow(MftUnavailableReason.NotLocalVolume)] [DataRow(MftUnavailableReason.NotNtfs)]
[DataRow(MftUnavailableReason.NoMftSource)]
public async Task Automatic_AnIneligibleVolumeWalksAndReportsTheReason(MftUnavailableReason reason)
// Ready; Producer Enumeration; MftUnavailable == reason; CurrentBlock header ProducerKind Enumeration; zero source calls
// (same rows)
public async Task MftOnly_AnIneligibleVolumeFailsBeforeCacheWork(MftUnavailableReason reason)
// Failed; Failure.Kind MftUnavailable; MftUnavailable == reason; Producer Mft; no *.mlix in the cache directory; zero calls
public async Task WalkOnly_TouchesNoMftSource()            // eligible facts, source present: Producer Enumeration, zero calls
public async Task Automatic_EligibleVolumeScansTheMft()    // Producer Mft, MftUnavailable null, one producer call
public async Task Automatic_EligibleRemovableVolumeScansTheMft()   // Review Focus 1 (facts DriveType.Removable)
public async Task Automatic_MidScanFailureFailsAndNeverWalks()
// producer throws IOException: Failed, ProducerFailed, Producer Mft, MftUnavailable null, no walk block written
public async Task Rescan_KeepsTheWalkOfAnAutomaticIndex()  // NotNtfs walk; RescanAsync; still Enumeration; zero calls
public async Task Rescan_OfAnIneligibleMftOnlyIndex_ReturnsTheSameFailureWithoutTheSource()
public async Task Offline_AtOpen_HasNoProducer()           // missing directory: Offline, Producer null, MftUnavailable null
public async Task Mft_CacheFileIsNamedForTheVolume()       // "T-<serial>.mlix" for a volume root
// FileIndexOptionsValidationTests: each throws before the cache directory exists
[DataRow(0u)] [DataRow(4u)] public async Task UndefinedProducerSelection_IsRejected(uint value)   // ArgumentOutOfRangeException
public async Task NullRoot_IsRejected()  public async Task DefaultRootKey_IsRejected()
public async Task Dump_WithWalkOnly_IsRejected()           // ArgumentException naming Producer
[DataRow(ProducerSelection.Automatic)] [DataRow(ProducerSelection.MftOnly)]
public async Task Dump_WithAnMftSelection_Opens(ProducerSelection selection)   // Producer Mft
public void Apply_RejectsAVolumeWithoutItsRootDirectory()  // SyntheticVolumeFacts guard
```

- [ ] **Step 2: Continue into Task 4.3** (the suite does not compile until the tests migrate).

### Task 4.3: Migrate the suite off `IndexedRoot`

**Files:** every test file the compiler lists (a grep finds about 74 that reference `IndexedRoot`,
`SyntheticIndexedRoot`, `MftSourceMissing` or a `ProducerKind` producer argument; the largest are `IndexedRootTests` 32,
`FileIndexScanFinishedTests` 13, `FileIndexProducerSelectionTests` 9, `FileIndexPerRootProducerKindTests` 8,
`DirectHostTests` 8, `WatchHostModeTests` 8), plus `MFTLib.Tests/TestSupport/IndexTreeFixture.cs`,
`CheckpointLossFixture.cs`, `TestMftSources.cs`, `TwoRootQueryFixture.cs`, and slice 3's
`FileIndexMftRescanGrowthTests.cs` when it is on main. Delete `MFTLib.Tests/Index/IndexedRootTests.cs` (its cases live
in Tasks 4.1 and 4.2).

**Recipe** (keep every behavior assertion unless the row says otherwise):

| Old | New |
| --- | --- |
| `SyntheticIndexedRoot.Create(volume, directory, serial, ProducerKind.Mft)` | `Root = volume, Producer = ProducerSelection.MftOnly`, then `SyntheticVolumeFacts.Apply(options, serial, volume: volume, volumeRootDirectory: directory)` |
| the same with `ProducerKind.Enumeration` | `Producer = ProducerSelection.WalkOnly`, same `Apply` |
| `SyntheticIndexedRoot.Create(RootKey.FromDirectory(d), d, serial, Enumeration)` | `Root = RootKey.FromDirectory(d), Producer = WalkOnly`, `Apply(options, serial)` |
| `IndexedRoot.FromDirectory(d)` | `Root = RootKey.FromDirectory(d), Producer = WalkOnly` |
| `IndexTreeFixture.Root(...)`, `Options(char, uint, ProducerKind, CacheMode?)` | `Root(...)` deleted; `Options(char drive = 'T', uint volumeSerial = 0x0BADF00D, ProducerSelection producer = ProducerSelection.WalkOnly, CacheMode? cacheMode = null)` applies the mapping to `TreeRoot` |
| `RootFailureKind.MftSourceMissing` | `RootFailureKind.MftUnavailable` with `Status.MftUnavailable == NoMftSource` |
| a null `MftSource` warm-adopting a cached MFT block (`FileIndexPerRootProducerKindTests.WarmMftRoot_NullSourceNeedsNeitherScanNorWatchUntilStartIsRequested`, `MixedOpen_NullMftSourceFailsOnlyTheRootNeedingAScan`) | keep their behavior under orchestrator ruling 2 (adopt as Mft; the root needing a scan fails `MftUnavailable` with `NoMftSource`, never walks); migrate only the option and failure-kind spelling |
| `MixedOpen_MftFailureNeverWalksItsTreeAndEnumerationSiblingIsReady` | keep, under MftOnly and under Automatic (both never walk on a producer failure) |
| `OpenAsync_DirectoryWithMftProducer_IsRejectedBeforeSideEffects` | delete; a directory MFT view is legal (Task 4.5) |

- [ ] **Step 1: Migrate** with the recipe; `grep -rn "IndexedRoot\|MftSourceMissing" --include=*.cs MFTLib MFTLibTestExtensions MFTLib.Tests SampleProgram.* Benchmark`
  returns nothing.
- [ ] **Step 2: Build and run**: `.\scripts\build-windows.ps1 -NoRestore`, then the full suite. Expected: 0 failed;
  `PublicSurfaceTests`, `PublicMemberCallerTests`, `NamespaceBoundaryTests` and `NativeSeamIsolationTests` pass.
- [ ] **Step 3: Commit** `feat!: select the producer from RootKey and ProducerSelection; delete IndexedRoot`.

### Task 4.4: Broker launch failures are eligibility reasons; Automatic falls back at open

**Files:**
- Create: `MFTLib/Index/MftUnavailableException.cs`, `MFTLib/Broker/Client/BrokerLaunchDeclinedException.cs`,
  `MFTLib/Broker/Client/BrokerEndedException.cs`
- Modify: `MFTLib/Broker/Client/BrokerProcess.Launch.cs:53-57`, `MFTLib/Broker/Client/BrokerSession.cs` (`ThrowIfEnded`),
  `MFTLib/Broker/Client/BrokerMftBlockProducer.cs:38-53`, `MFTLib/Index/FileIndex.RootRuntime.Production.cs`
  (`ProduceRootBlockCoreAsync`), `FileIndex.RootRuntime.Open.cs` (`AddRootAsync`), `ResolvedRoot.cs`
- Test: create `MFTLib.Tests/Index/FileIndexMftFallbackTests.cs`; add to `MFTLib.Tests/BrokerMftBlockProducerTests.cs`

**Interfaces:**
- `internal sealed class MftUnavailableException(MftUnavailableReason reason, string message, Exception? innerException = null) : InvalidOperationException`
  with `Reason`. Lives in `MFTLib.Index` so the index catches it without referencing the broker.
- `internal sealed class BrokerLaunchDeclinedException(string message) : InvalidOperationException`, thrown by
  `BrokerProcess.LaunchAsync` when the launcher returns false (today's message);
  `internal sealed class BrokerEndedException(string message) : InvalidOperationException`, thrown by
  `BrokerSession.ThrowIfEnded` (today's message). Public callers still see `InvalidOperationException`.
- `BrokerMftBlockProducer.ProduceAsync` wraps only the `_connectAsync` call: `BrokerLaunchDeclinedException` becomes
  `MftUnavailableException(ElevationDeclined, ...)`; `TimeoutException` and `BrokerEndedException` become
  `BrokerUnavailable`; every other exception and everything after connect propagate unchanged (`ProducerFailed`).
- `ProduceRootBlockCoreAsync` catches `MftUnavailableException` ahead of its general catch and returns
  `ScanFailureKind = MftUnavailable`, `MftUnavailable = exception.Reason`, the exception's message.
- `ResolvedRoot AsWalk(MftUnavailableReason reason)`: Enumeration, the reason, `MftSource` null.
- `AddRootAsync`: when the open's scan settles with no block, `ScanFailureKind == MftUnavailable` and
  `Options.Producer == Automatic`, release canonical ownership, replace `Resolved` with `AsWalk(reason)` (which also
  resets `Kind` and the matchable path), clear the recorded failure, and run the open once more as a walk (one
  retry, never a loop). A rescan never swaps (decision 4).

- [ ] **Step 1: Write the failing tests** (`BrokerSession`'s internal launch-delegate constructor with
  `CreateIndexSource()`; eligible facts through `SyntheticVolumeFacts.Apply`)

```csharp
// BrokerMftBlockProducerTests
[DataRow("Declined", MftUnavailableReason.ElevationDeclined)]
[DataRow("Timeout", MftUnavailableReason.BrokerUnavailable)]
[DataRow("Ended", MftUnavailableReason.BrokerUnavailable)]
public async Task Produce_AConnectFailureIsAnEligibilityReason(string failure, MftUnavailableReason expected)
public async Task Produce_AnIOExceptionFromConnectPropagatesUnchanged()
// FileIndexMftFallbackTests
public async Task Automatic_ElevationDeclined_WalksAndReportsIt()
// Ready, Producer Enumeration, MftUnavailable ElevationDeclined, Failure null, block header ProducerKind Enumeration
public async Task Automatic_ConnectTimeout_WalksAsBrokerUnavailable()
public async Task MftOnly_ElevationDeclined_Fails()        // Failed, MftUnavailable kind and reason, Producer Mft
public async Task Automatic_ConcurrentOpensThatFallBack_ShareOneLaunch()
// volumes T and U, launcher gated on a TaskCompletionSource then throwing BrokerLaunchDeclinedException:
// launcher invoked once; both indexes walk with ElevationDeclined (Review Focus 2)
public async Task Automatic_DirectoryRootFallback_TakesItsWalkSlotAndReleasesTheVolumeSlot()
// root T\src: the cache directory holds D<hash>-<serial>.mlix and no T-<serial>.mlix; a second MftOnly open of T owns
// the volume slot (no private fallback)
public async Task Automatic_WarmMftBlock_LaunchesNoBroker()   // cached MFT block: Producer Mft, launcher never invoked
public async Task Rescan_LaunchFailureOnAnMftIndex_KeepsTheBlockAndNeverWalks()
// Ready, retained MFT block, Failure.Kind MftUnavailable, MftUnavailable ElevationDeclined; a later successful rescan
// clears Failure and MftUnavailable
```

- [ ] **Step 2: Run, expect FAIL**: `--filter "FullyQualifiedName~FileIndexMftFallbackTests|FullyQualifiedName~BrokerMftBlockProducerTests"`
- [ ] **Step 3: Implement** the Interfaces.
- [ ] **Step 4: Run, expect PASS** with the filter plus `FullyQualifiedName~BrokerSession|FullyQualifiedName~BrokerProcessTests`.
- [ ] **Step 5: Commit** `feat: fall back to a walk when Automatic cannot launch the broker`.

### Task 4.5: Directory roots are MFT views of the volume block

**Files:**
- Create: `MFTLib/Index/ViewRoot.cs`
- Modify: `MFTLib/Index/RootBlock.cs`, `LookupEngine.cs` (`Root`, `SelectPathBlock`, `Find`), `FileEntry.Navigation.cs`
  (`Parent`), `FileIndex.Queries.cs` (`BorrowBlock`, `AdmitQuery`), `FileIndex.RootRuntime.Publication.cs`
  (`BuildRootBlock`), `FileIndex.RootRuntime.Status.cs` (`DescribeLocked`), `FileIndex.RootRuntime.JournalMutation.cs`
  (after each applied batch), `FileIndexSet.cs` (constructor)
- Test: create `MFTLib.Tests/Index/FileIndexDirectoryViewTests.cs`; add to `MFTLib.Tests/Index/FileIndexSetTests.cs`,
  `MFTLib.Tests/Index/CacheDirectoryInspectionTests.cs`

**Interfaces:**
- `internal sealed class ViewRoot`: `static ViewRoot ForBlockRoot(uint rootRow)` (volume roots, walks, dumps; always
  present); `static ViewRoot ForDirectory(string path, RootKind kind)`; `bool IsDirectoryView`; `string? MatchablePath`;
  `bool IsPresent`, `uint Row`, `ushort Sequence` (volatile reads); `bool Refresh(RootBlock rootBlock,
  CancellationToken cancellationToken)`: keeps the binding when the row is in use, not deleted, its sequence is
  unchanged and `IndexNavigation.BuildPath(rootBlock, row)` equals the path under `kind.Comparison` (cost: path
  depth); otherwise re-resolves by descending the path below the volume root from the header's `RootRow`, and
  publishes row, sequence and presence. Returns presence.
- `RootBlock(RootKey root, BlockFile block, ViewRoot view, string? rootDirectoryPath = null, MftIndexSource? mftSource = null)`;
  `RootBlock.View`; `MatchableRootDirectoryPath` is the view's path (what `Find` accepts), `RootDirectoryPath` stays
  the rendering root (the volume root for MFT, so paths are real full paths).
- `BuildRootBlock` binds `ForDirectory(Resolved.RootDirectory, kind)` for an MFT root whose key is a directory and
  `ForBlockRoot` otherwise, and refreshes it once at publication. The pump refreshes after each applied batch under
  the write gate. `AdmitQuery` refreshes before borrowing (outside the state lock, holding a borrow).
- `BorrowBlock` lends no block while `View.IsPresent` is false; `DescribeLocked` then reports `RootState.Offline` with
  `Block` null, the producer facts and the watch status (the block stays mapped and watched).
- `LookupEngine.Find` returns null for an absent view, matches `View.MatchablePath`, and descends from `View.Row`;
  `LookupEngine.Root` returns `View.Row`; `FileEntry.Parent` is null when `RowIndex == RootBlock.View.Row`.
- `FileIndexSet` rejects a member whose root directory lies under another member's (prefix test with the outer
  member's `RootKind`, equal paths included): `ArgumentException("Root {inner} lies inside root {outer}; the indexes of a FileIndexSet must not nest.")`.
  Dump members never nest with host paths.

- [ ] **Step 1: Write the failing tests** (`FileIndexDirectoryViewTests`: volume `T` mapped to `TreeRoot`, which
  holds real directories `src` and `other`; a `SyntheticMftProducer` writes rows 5 (root), 30 `src`, 31 `src\a.txt`,
  32 `other`, 33 `other\b.txt`; the view is `RootKey.FromDirectory(Path.Combine(TreeRoot, "src"))` with Automatic)

```csharp
public async Task Open_ADirectoryOnAnEligibleVolume_IsAnMftViewOfTheVolumeBlock()
// Producer Mft; Status.Root is the directory key; block header RootRow 5 and root key T; cache file T-<serial>.mlix
public async Task Find_AcceptsOnlyPathsUnderTheViewRoot()
// Find(TreeRoot/src/a.txt).Path == that path; Find(TreeRoot/other/b.txt) null; Find(TreeRoot) null
public async Task Root_IsTheViewRootAndItsParentIsNull()
public async Task Children_OfTheViewRootAreItsDirectChildren()
public async Task Watch_RenamingTheViewRootAwayAndBack_GoesOfflineThenReady()
// scripted journal batches rename row 30 to "src2", then back: Status Offline with Block null and empty queries, then
// Ready and Find works; no query throws (Review Focus 3)
public async Task Open_DirectoryAbsentFromTheBlock_IsOfflineUntilTheJournalCreatesIt()
public async Task RootTextWithOtherSeparatorsAndCase_NamesTheSameView()   // Review Focus 5; casing on Windows only
public async Task QueryUsnJournalSettings_ServesADirectoryViewAndRefusesAWalk()
// through UsnJournalSettingsQuery.Dependencies._queryOverride: process-global, so this test lives in a class with
// [DoNotParallelize] and a cleanup reset (docs/test-isolation.md)
// FileIndexSetTests
public async Task Constructor_RejectsNestedRoots()   // T with T\src, and src with src\deeper: ArgumentException naming both
public async Task Constructor_AcceptsSiblingViews()  // src and other
// CacheDirectoryInspectionTests
public async Task InspectCached_ListsADirectoryViewsMftBlockUnderItsVolumeOnly()
// InspectCached under VolumeKey T lists the block; under the src directory key lists none
```

- [ ] **Step 2: Run, expect FAIL**: `--filter "FullyQualifiedName~FileIndexDirectoryViewTests|FullyQualifiedName~FileIndexSetTests|FullyQualifiedName~CacheDirectoryInspectionTests"`
- [ ] **Step 3: Implement** the Interfaces. Unscoped queries are narrowed in Task 4.6, which owns those tests.
- [ ] **Step 4: Run, expect PASS** with the same filter plus `FullyQualifiedName~FileIndexWatch`.
- [ ] **Step 5: Commit** `feat: open a directory root on an NTFS volume as a view of the volume block`.

### Task 4.6: Implicit subtree with the two-bitset verdict cache

**Files:**
- Create: `MFTLib/Index/SubtreeMembership.cs`
- Modify: `MFTLib/Index/SearchEngine.cs` (`RootScan`, `SearchOneRoot`, the partition loop at 170-200, `EnumerateRoot`),
  `MFTLib/Index/IndexRowEnumerable.cs:70-130,214` (membership and the unfiltered fast path), `MFTLib/Index/IndexNavigation.cs`
  (delete `IsUnder` once unused)
- Test: create `MFTLib.Tests/Index/SubtreeMembershipTests.cs`

**Interfaces:**
- `internal sealed class SubtreeMembership(BlockFile block, uint anchorRow) : IDisposable`: captures `RowCount`, rents
  two `ulong[]` of `(RowCount + 63) / 64` words (`known`, `inside`); `bool Contains(uint row)` climbs with
  `IndexNavigation.TryGetParentRow` up to `BlockLayout.MaximumPathDepth` hops, stopping at the anchor (inside), a
  known row (its verdict), no parent, or the depth limit (outside), then records the verdict for every row it visited
  per decision 8; a row at or past the captured count is answered by climbing without caching;
  `internal long ParentReads` (plain increment, exact single-threaded). `Dispose` returns the arrays.
- The anchor row is `query.Subtree`'s resolved row when set, else `rootBlock.View.Row` for a directory view, else no
  membership (every row qualifies). One membership per root scan, shared by its partitions. `IndexRowEnumerable`'s
  fast path also requires `!View.IsDirectoryView`.

- [ ] **Step 1: Write the failing tests**

```csharp
public void Contains_AgreesWithTheParentChainOnEveryRow()
// 2000 synthetic rows with a detached row, a two-row cycle and a 130-deep chain; oracle: a test-local climb
public void Contains_ClimbsEachDirectoryOncePerQuery()
// a 100-deep directory chain holding 1000 files at its deepest level; Contains over every row: ParentReads <= rows + 100
// (Review Focus 4)
public void Contains_ConcurrentPartitionsAgreeWithASequentialPass()   // Parallel.For over rows vs a fresh sequential pass
public void Contains_RowPastTheCapturedCount_IsAnsweredWithoutCaching()
// FileIndexDirectoryViewTests (Task 4.5's fixture)
public async Task UnscopedQueries_SeeOnlyTheView()   // Search, Enumerate, EnumerateRows for "*.txt": only a.txt
public async Task ExplicitSubtreeInsideTheView_Narrows()
```

- [ ] **Step 2: Run, expect FAIL**: `--filter "FullyQualifiedName~SubtreeMembershipTests|FullyQualifiedName~FileIndexDirectoryViewTests"`
- [ ] **Step 3: Implement**; every existing `Subtree` test keeps passing.
- [ ] **Step 4: Run, expect PASS**: the filter plus `FullyQualifiedName~SearchEngine|FullyQualifiedName~Subtree|FullyQualifiedName~FileIndexEnumerateRowsTests|FullyQualifiedName~FileIndexSetTests`.
- [ ] **Step 5: Commit** `feat: scope unscoped queries on a directory view with a verdict cache`.

### Task 4.7: Samples select the producer and accept any root

**Files:** `SampleProgram.Watch/WatchArguments.cs`, `WatchArguments.Parse.cs`, `SampleHost.Broker.cs`,
`SampleHost.Drives.cs`, `SampleHost.Verbs.cs`, `SampleHost.WatchDrives.cs`, `SampleHost.Run.cs`;
`SampleProgram.Direct/SampleHost.Run.cs`; tests `MFTLib.Tests/WatchArgumentsTests.cs`, `WatchHostModeTests.cs`,
`WatchFirstRunTests.cs`, `DirectHostTests.cs`.

**Interfaces:**
- `WatchArguments(WatchVerb Verb, IReadOnlyList<RootKey> Roots)` (`Drives` renamed; tokens parse with
  `RootKey.TryParse`, duplicates removed, an unparsable token reports `Root 'X' is neither a drive nor a full path.`);
  `internal ProducerSelection Producer { get; init; } = ProducerSelection.Automatic`; `--producer automatic|mft|walk`
  is read by scan, watch, rescan and journal (`VerbFlags`); another value reports
  `Option --producer must be automatic, mft or walk.`; usage shows `[root ...]` and the flag.
- Each open sets `Root = root` and `Producer = parsed.Producer`; status lines print the producer and, when set,
  `MFT unavailable: {reason}`. With no root named, the host picks `WindowsVolume.EnumerateAll()` volumes whose
  `IsIndexable` is true and says "every ready local NTFS drive". `SMPW` stays 3.

- [ ] **Step 1: Write the failing tests**: `Parse_ProducerFlag_SelectsTheProducer` (three `DataRow`s),
  `Parse_UnknownProducer_IsAUsageError`, `Parse_ADirectoryRoot_ParsesAsADirectoryKey` (a temp directory path),
  `Parse_AnUnparsableRoot_IsAUsageError`, `Watch_PrintsTheProducerAndTheUnavailableReason` (WatchHostModeTests with a
  NotNtfs facts reader through `prepareOptions`: output contains `producer Enumeration` and `MFT unavailable: NotNtfs`),
  `Direct_PrintsTheProducer`.
- [ ] **Step 2: Run, expect FAIL**: `--filter "FullyQualifiedName~WatchArguments|FullyQualifiedName~WatchHostModeTests|FullyQualifiedName~DirectHostTests|FullyQualifiedName~WatchFirstRunTests"`
- [ ] **Step 3: Implement.**
- [ ] **Step 4: Run, expect PASS** with the filter plus `FullyQualifiedName~PublicMemberCallerTests` (no unreferenced
  member, no stale exemption).
- [ ] **Step 5: Commit** `feat: the Watch sample takes any root and a --producer choice`.

### Task 4.8: Documentation and rulings

**Files:** `docs/api-decisions.md`, `docs/architecture.md` (lines 11, 12, 18, 151-164, 210),
`docs/broker-integration.md` (80, 114, 129-156), `docs/broker-testing.md`, `docs/index-format.md` (11, 223, and the
"File name" section: MFT blocks are named for their volume), `docs/test-isolation.md:326` (delete the
`IndexedRoot.Dependencies` row; the facts seam is per options and needs no isolation), `docs/watch-lifetime.md:7`,
`docs/query-lifetime.md` (implicit subtree; verdict cache), `README.md` (62, 76, 102, 170, 210, 211), `CHANGELOG.md`
(0.3.0 lines naming `IndexedRoot`, `MftSourceMissing` and `EnumerateWindowsVolumes`), `AGENTS.md:111`
(`` `IndexedRoot` `` to `` `RootKey` ``, net shorter), the spec's section 1 row for `SyntheticVolumeFacts` (B1).

- [ ] **Step 1: Run the docs-update check**; `grep -rn "IndexedRoot\|MftSourceMissing\|EnumerateWindowsVolumes\|FromWindowsDrive" --include=*.md --include=*.cs .`
  returns only `docs/superpowers/` lines.
- [ ] **Step 2: Supersede in `api-decisions.md`** (each rewritten in place with the new decision, its reason from
  MFTLib issue 844 rulings 2 and 3 and owner answers 1 to 3, and a revisit condition):
  - "One producer enum, selection and observation" becomes **"ProducerSelection selects, ProducerKind observes"**:
    the three selections, the default invalid, eligibility facts and order (decision 1), fallback only for
    eligibility reasons including `BrokerUnavailable` and `ElevationDeclined` at an open's cold scan, a mid-scan
    failure never walks, the producer fixed for the index lifetime, `RootStatus.Producer` (null only when Offline at
    open) and `MftUnavailable` (decisions 3 and 4), and `IndexedRoot` deleted (owner answer 1). Its old revisit
    condition ("a choice that cannot identify a block's producer") is the reason.
  - "OpenAsync validates root keys and producer choices before filesystem side effects": default `RootKey`, undefined
    `ProducerSelection`, dump with WalkOnly.
  - "Cold production failures settle per root across both producers" and "Source absence is typed; applications own
    their wording": `MftUnavailable` with `NoMftSource` replaces `MftSourceMissing`.
  - "The enumeration producer is a product feature, chosen per root": its rule becomes the eligibility-only fallback.
  - "Two key types: VolumeKey and RootKey, one method per lifecycle verb": `FileIndexOptions.Root` is a `RootKey?`.
  - "Windows volume discovery reports every volume with an MFT verdict": `WindowsVolume.EnumerateAll()`;
    `IsIndexable` is Automatic's predicate, removable NTFS included (owner answer 2); the `removable` reason goes.
  - "Windows-only annotations sit on the member that reaches Windows": the `FromWindowsDrive` clause goes;
    `EnumerateAll` carries the annotation.
  - "Windows volume construction is named for its volume key": deleted (no factory remains; the open reads the serial).
  - "WindowsVolume.Reason stays as display-only text": the reason list.
  - "One root per FileIndex, and FileIndexSet for multi-index queries": `Root` is `RootKey?`; `SyntheticVolumeFacts`
    replaces `SyntheticIndexedRoot`; nested members are rejected (owner answer 3).
  - Leave "A subtree anchor belongs to the queried index's current root block" (decision 7) and "Block Generation is a
    per-root change version" for slice 5.
- [ ] **Step 3: Add rulings** "A directory root on an NTFS volume is an MFT view of the volume block" (view root row
  and sequence rechecked at publication, after each batch and at query admission; `Offline` and back; `Find` under the
  view; implicit subtree with the verdict cache; `Parent` null at the view root; real full paths; MFT blocks named for
  their volume; `Changed` reports every change on the volume) and "Every MFT block is named for its volume"
  (decision 5), each with reason and revisit condition.
- [ ] **Step 4: Check budgets**: `wc -m AGENTS.md` below 15000, then
  `--filter FullyQualifiedName~AgentInstructionsTests`.
- [ ] **Step 5: Commit** `docs: record producer selection and directory MFT views`.

### Task 4.9: Slice verification and PR

- [ ] Full build and full suite: 0 failed; `NativeSeamIsolationTests` on Windows; Linux through CI and
  `./scripts/coverage-linux.sh` under WSL when available (on Linux, Automatic over a real directory reports
  `PlatformWithoutMft` and walks; synthetic facts drive every MFT case).
- [ ] `--filter "FullyQualifiedName~PublicSurfaceTests|FullyQualifiedName~PublicMemberCallerTests|FullyQualifiedName~NamespaceBoundaryTests"`:
  approved files differ from main by exactly the rows in "Public surface changes".
- [ ] Coverage gate (`.\scripts\run-coverage.ps1 -NonInteractive`, then `pwsh -NoProfile -File scripts/test-coverage-status.ps1`).
- [ ] `aislop scan .` 100 with zero unsuppressed findings; `git diff --check` clean; `wc -m AGENTS.md` below 15000.
- [ ] PR body: starts `## Problem` (a consumer must classify each root and pick the producer itself, git-wizard's
  `WindowsRootClassifier` already disagrees with MFTLib on removable NTFS, and a directory root can only be walked),
  cites MFTLib issue 844; a **Public surface** section copying the table above, naming the consumer production caller
  of every added member; the rulings superseded and added; and **Consumer migration** by file, from spec section 6:
  git-wizard `WindowsRootClassifier.cs` (delete) and `RepositoryDiscoveryDependencies.cs:31`, `DiscoveryScope.cs`,
  `MftIndexSession.cs`, `MftIndexSession.Windows.cs`, `EnumerationDiscovery.cs`, `RepositoryDiscoveryCoordinator.cs`,
  `RepositoryDiscoveryCoordinator.Watch.cs:67`, `Watch/IndexVolumeChangeSource.*`, `GitWizardApi.IndexDiscovery.cs`,
  `MftIndexCacheStatusFormatter.cs`, and every `SyntheticIndexedRoot.Create` test caller; file-wizard
  `FileIndexHost.cs`, `IndexSession.cs:37`, `DriveDiscovery.cs`, `ScannerState.cs:28-46`, `DirectoryWalkRoot.cs`,
  `FileIndexHostOptions.cs:11`, `ScanOperation.HostReplacement.cs:66`, `IndexHostOpening.cs:8`,
  `RecentChangesBenchmarks.cs:39-43`. Note that a second index on one volume uses the private fallback until slice 5,
  and that directory views report volume-wide `Changed` events until then.

# Slice 5: Volume scan sharing

Expand into tasks when slices 3 and 4 merge.

- **Goal:** all MFT views of one share key in one process attach to one `VolumeScan` (block, owner lock, production,
  watch, catch-up, checkpoint-loss facts, write gate); each `FileIndex` is an `IndexView` with its own root row,
  borrows, disposal, watch request, events and anchors.
- **Depends on:** slices 3 and 4.
- **Files likely touched:** split `MFTLib/Index/FileIndex.RootRuntime*.cs` into `VolumeScan*.cs` and `IndexView*.cs`,
  new `VolumeScanRegistry.cs`, `FileIndex.cs`, `FileIndex.Watch*.cs` (requests per view, one watch per scan),
  event routing in the pump, `FileIndex.Rescan*.cs` (coalescing), `RootBlock.cs` and borrow counting,
  `FileIndexSet.cs`, `FileEntry.cs` (carries its view; equality compares view, block, row, sequence),
  `NativeSeamIsolationTests`, `MFTLibTestExtensions` registry reset helper, `SampleProgram.Watch` `add-root <path>` console command.
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
