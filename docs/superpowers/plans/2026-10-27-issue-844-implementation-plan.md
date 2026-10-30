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

# Slice 5: Volume scan sharing

**Branch:** `feature/844-volume-scan-sharing` from main after slice 4 (PR 849) merged. One PR, one implementer, its own
worktree. **First commit (Task 5.1):** delete the completed "# Slice 3" and "# Slice 4" sections of this plan file and
replace this "# Slice 5" section with this text.

## Blockers for the orchestrator

1. **The pre-merge A/B gate needs a git-wizard build against slice 5's API.** The verification bar says the slice-6 A/B
   second run must report warm client peak working set at or below 1,359 MB before this slice merges (spec section 8,
   "A/B re-measurement"). The harness (`scripts/measure-retention-ab.ps1`) runs git-wizard discovery, and git-wizard
   main does not compile against MFTLib after slices 2 and 4 (`RetainedFileNames` and `IndexedRoot` are deleted); the
   migration is slice 6's pin-bump work. Decide one: (a) before this PR merges, a git-wizard measurement branch migrated
   to slice 5's API (spec section 6 list) is built against this branch and the run is made; or (b) the owner agrees to
   move the gate to slice 6. No task below depends on the answer; Task 5.12 Step 5 carries whichever you choose.

No other blocker: the spec, rulings 1 to 16 and the slice 4 code agree on every point a task needs. The decisions below
fill the places where the spec is silent.

## Goal

All MFT views of one share key in one process attach to one `VolumeScan`: one block, one owner lock, one production
(cold scan, warm adoption, rescan, recovery), one journal watch and pump, one set of catch-up and checkpoint-loss
facts, one write gate. Each `FileIndex` is an `IndexView` with its own root row, borrows, disposal, watch request,
events and anchors. A second index on the same volume no longer takes the private fallback; directory views receive
only the changes inside them.

## Public surface changes

**None.** No `*.approved.txt` file changes, no `PublicMemberCallerTests` exemption is added or removed, and no
test-package member is added (decision 9). Behavior changes on existing members, all already listed in spec section 1
"Changed": `FileEntry` equality compares view, root block, row and sequence; `RootBlockStatus.Generation`,
`LiveRowCount`, `IsCapacityExhausted` and `SkippedRecordCount` are facts of the shared block; `FileIndex.Changed`
on a directory view reports only changes inside the view.

**Rulings superseded** (Task 5.11, each rewritten in place with reason and revisit condition):
"Block Generation is a per-root change version" (per volume block); "A subtree anchor belongs to the queried index's
current root block" (per view); "A directory root on an NTFS volume is an MFT view of the volume block" (its `Changed`
sentence and its revisit condition, which this slice meets); "Every MFT block is named for its volume" (its "until
slice 5" reason sentence); "A retained FileEntry keeps only its own root block mapped" (the handle holds its view's
`RootBlock`, which holds the shared `MappedBlock`); "A root watch's pump owns fault attribution" (its revisit
condition, "one watch stream can represent several independently owned root runtimes", is now met); "FileEntry
handles are bound to the row's sequence number" (the equality sentence); "One root per FileIndex, and FileIndexSet
for multi-index queries" (its reentrancy paragraph's "indices share no state" and its reason's "every per-root
runtime... was already independent"). **Added:** "Volume scan sharing".

## Decisions this slice makes (spec silent)

1. **What attaches.** A view attaches to a registered scan exactly when its resolved root is
   `{ Producer: Mft, IsDump: false, MftUnavailable: null }` and its directory exists at open. Everything else gets a
   private, unregistered `VolumeScan` with one view: walks (spec: never shared), dumps (no cache and no journal, so
   sharing saves one parse; a dump requires null `Root`), an MftOnly root found ineligible at selection, and a root
   Offline at open. A null `MftSource` is a key value like any other (file-wizard's cache-only host shares cached
   blocks through it; orchestrator ruling 2 of slice 4 holds per scan).
2. **Share key equality.** `MftIndexSource` by reference; `VolumeKey`; serial; cache directory as
   `Path.TrimEndingDirectorySeparator(Path.GetFullPath(directory))` compared `OrdinalIgnoreCase` on Windows and
   `Ordinal` elsewhere, or null for `CacheMode.Disabled`; `CacheTag` exactly. `ExistingOnly` and `Persistent` opens of
   one directory share a key.
3. **Settle modes.** A settle (warm adoption, then cold scan when needed) runs on the scan under its lifecycle gate,
   shared by every view awaiting it. It runs `Persistent` when any view joined to it writes (`CacheMode` other than
   `ExistingOnly`), else `ExistingOnly`, and that mode drives today's mode-dependent rules (owner-lock `InUse`, leaving
   rejected files in place, cache-only adoption of an unresumable checkpoint). An `ExistingOnly` view waits for the warm
   verdict, never for a cold scan: when the settle proceeds to a cold scan, the view settles at once with a view-level
   `CacheDeclined` (or `CacheIdentityMismatch` for a tag or shape rejection, or `InUse`), stays attached, and reads
   `Failed` with no block until its own `RescanAsync` succeeds. A writing view that joins a non-writing settle waits for
   it and, when it ended without a block, starts one writing settle (coalesced among writers).
4. **A blockless scan re-settles for a new opener.** A view that attaches to a scan whose last settle ended `Failed`
   runs (or joins) a new settle, as a fresh open retries today; already attached views report its outcome, since state
   is the scan's.
5. **Automatic fallback is per view.** When a settle ends with `RootFailureKind.MftUnavailable`, each Automatic view
   detaches and settles a private walk (one retry, as slice 4); MftOnly views stay attached and read the scan's failure.
6. **Fan-out of per-view options.** `Diagnostics` lines and `ScanProgress` samples of the scan go to every attached
   view's sink (a delegate attached twice receives a line once); each progress sample is copied with the receiving
   view's `Root`. The scan reads `TimeProvider`, `DirectoryWalk` and the internal `IndexHooks` of the view that created
   it. Scan-level messages (production failures, journal errors) name the scan's block root (the volume key for a
   registered scan); view-level messages (`Root()` refusal, MftOnly ineligibility, view-level declines) name the view's key.
7. **Changed routing is by path containment.** A change reaches a view when its `Path` or its `PreviousPath` lies under
   the view's matchable path, by `LookupEngine.TryMatchRootPrefix` with the view's `RootKind` (equal paths included);
   a block-root view receives every change. This is "the row before or after the change" (spec section 2) computed from
   the paths the mutator already built, and it is the matcher `Find` and `FileIndexSet` containment use (ruling 15).
   Each routed change is re-minted onto the receiving view's `RootBlock` with the sequence number captured at apply
   time. `Changed` reaches every attached view in scope, requested or not; `WatchStateChanged` reaches every attached
   view (its `Status` changed); `WatchFaulted` for `Volume`, `Apply`, `Channel`, `Recovery`, `RescanRestart` and
   `CatchUpLost` reaches every view holding a watch request at settlement (spec); `Subscriber` reaches only the view
   whose handler threw, once per watch instance per view.
8. **Outstanding faults are per view.** At a scan fault's settlement every requesting view records it as its own
   outstanding fault; that view's stop returns it once as `Failed`. A consumer start that supersedes the faulted
   instance clears every view's copy of that instance's fault. A stop that withdraws a request while other requests
   remain returns at once without retiring the watch: `Failed` with its own outstanding fault when it holds one, else
   `Succeeded`; "stop wins" applies to the stop that withdraws the last request.
9. **Test isolation by an injectable registry.** `VolumeScanRegistry.Process` is the process registry;
   `FileIndexOptions.ScanRegistry` (internal init, null means `Process`) lets a test class use its own instance, so
   sharing tests run in parallel and count deterministically. A reference to `VolumeScanRegistry.Process` is a process
   seam (`NativeSeamIsolationTests` enforces `[DoNotParallelize]`), and those classes check in cleanup that their keys
   left no live scan. Ordinary tests stay isolated through their own source instances, unique serials
   (`TestVolumeSerial.GetNext()`) and temporary cache directories. This replaces the spec's "reset it in cleanup" with
   a cleanup check, because a reset of live scans would unmap blocks other code still reads; Task 5.11 writes the rule
   into `docs/test-isolation.md`.
10. **Coalesced rescans.** At most one rescan of a scan runs and at most one is pending. A `RescanAsync` call joins the
    pending rescan, or creates it; the pending rescan stops accepting joiners when it takes the lifecycle gate, so a
    caller never receives a result produced from a scan that started before its call. A caller's cancellation or its
    view's disposal ends only its own wait; the production is cancelled when its last joined caller has gone or the scan
    closes. Automatic recovery stays one ticketed recovery per scan and does not join manual rescans; a manual commit
    clears the recovery ticket as today.
11. **Offline at open stays terminal; mid-life Offline follows the resolved path.** A directory missing at open
    settles `Offline` on a private scan and never attaches (spec "as today"; ruling "an Offline root is never
    rescanned"). A view whose directory leaves the block later reads `Offline` and returns to `Ready` when the journal
    brings a directory back at its resolved final path; the link is not re-resolved (ruling 16: "the result is read once
    per open: a re-targeted link needs a reopen").
12. **Reopen during the last detach owns the canonical slot.** The registry keeps a closing scan's `Closed` task until
    it completes, and a new scan for the same key awaits it before taking the owner lock, so a reopen that races the
    last disposal never takes the private fallback.

## Shared-state invariants (each is stated in a comment where its state lives)

- **I1 Registry** (`VolumeScanRegistry`): a key maps to a live scan exactly while at least one view is attached; the
  view count and the key's removal change in one registry-lock section. The registry lock is never held across an
  `await` or while acquiring a gate. Lock order: registry lock, then a scan's state lock; never the reverse.
- **I2 Closing** (`VolumeScanRegistry`, `VolumeScan.Closed`): a scan for a key starts its settle only after the
  previous scan of that key has released its owner lock.
- **I3 Views list** (`VolumeScan._views`): changes only under the scan's state lock; an attach to a published block
  also holds the write gate, so every batch is either applied before the new view's binding is refreshed or routed to
  that view.
- **I4 Publication** (`PublishRescannedBlockAsync`, `AdoptOpenedRoot`): one `RootBlock` per attached view is built and
  its binding refreshed under the write gate, outside the state lock; the scan block and every view's block are then
  swapped in one state-lock section, so no reader sees two views on different blocks of one scan.
- **I5 Borrows** (`RootBlock.TryTakeBorrow`): a borrow takes the view's `ViewBorrows` first and the `MappedBlock`'s
  release state second, and returns them in reverse. Closing a view refuses new borrows on every `RootBlock` of that
  view and waits for its own outstanding borrows only.
- **I6 Mapping** (`MappedBlock`): a mapped block is released by scan teardown (current block), by retirement draining
  or by its finalizer once no `RootBlock` reaches it; never by a view's disposal.
- **I7 Watch requests** (`VolumeScan._requesters`, `IndexView.WatchRequested`): the scan's watch is wanted exactly
  while `_requesters` is non-empty; a view's flag and its membership change together under the state lock.
- **I8 Faults** (`RecordPumpFault`, `AnnounceSubscriberFault`, rescan and recovery fault sites): a scan fault is copied
  to each requesting view under the state lock at settlement and delivered after the lock is released; a subscriber
  fault stays on its view.
- **I9 Delivery** (`IndexView.Deliveries`): a view's handlers never start after its `DisposeAsync` has closed its
  delivery gate, and `DisposeAsync` waits for a delivery already inside it.
- **I10 Rescans** (`VolumeScan._pendingRescan`): at most one running and one pending; joiners are counted under the
  state lock; the pending request is detached from `_pendingRescan` in the same state-lock section that marks it started.
- **I11 Settles** (`VolumeScan._settle`): at most one settle runs; its mode is fixed when it takes the lifecycle gate
  (decision 3).
- **I12 Teardown** (`VolumeScan.CloseAsync`): cancel the scan's closing token, stop and drain the watch and every
  recovery, take the lifecycle gate then the write gate, drain and release retired then current mapped blocks, release
  the owner lock, then complete `Closed`.

## Review Focus

1. **git-wizard adds a search root while the volume is Live** (spec section 2 "Open, block current and watch Live"):
   the new view is `Ready` at once, sees every row a concurrent batch wrote, and receives every later change under it
   exactly once. Task 5.4 (`AttachDuringLive_...`), Task 5.6 (`Route_ABatchConcurrentWithAttach_...`).
2. **A consumer disposes one of two indexes while a long query of the other runs:** the disposal returns without
   waiting for the other view's borrow, and the other view keeps answering. Task 5.5
   (`Dispose_NotLast_DoesNotWaitForAnotherViewsBorrow`).
3. **A move between two watched roots on one drive (`D:\src\a.txt` to `D:\work\a.txt`):** both views receive one
   `Renamed`, and a `FileIndexSet` over both lists the file once, under its new root. Task 5.6
   (`Route_AMoveAcrossViews_ReachesBothAsRenamed`), Task 5.8.
4. **Repeated `RescanAsync` calls (a UI button pressed several times):** at most two productions run, and every call
   gets a result. Task 5.7 (`Rescan_ThreeCallsDuringOneProduction_RunAtMostOneMore`).
5. **Dispose then reopen at once, or a reopen racing the last disposal:** the reopen owns the canonical cache slot,
   never `PrivateFallback`. Task 5.5 (`Reopen_RacingTheLastDetach_OwnsTheCanonicalSlot`).

## File map

| File | Responsibility |
| --- | --- |
| Create `MFTLib/Index/MappedBlock.cs` | One mapped block file, its release state, reported cycles and finalizer, shared by every view |
| Create `MFTLib/Index/ViewBorrows.cs` | One view's borrow counter, closed by its disposal |
| Modify `MFTLib/Index/RootBlock.cs`, `SnapshotBorrow.cs`, `FileEntry.cs`, `FileEntry.Navigation.cs`, `SubtreeAnchor.cs` | `RootBlock` is one view's handle on a `MappedBlock` |
| Rename `MFTLib/Index/FileIndex.RootRuntime*.cs` to `FileIndex.VolumeScan*.cs` (`git mv`, 27 files) | The shared owner |
| Create `MFTLib/Index/FileIndex.IndexView.cs`, `FileIndex.IndexView.Delivery.cs`, `FileIndex.IndexView.Lifecycle.cs` | The per-index view |
| Create `MFTLib/Index/FileIndex.VolumeScan.Views.cs`, `FileIndex.VolumeScan.Settle.cs`, `FileIndex.VolumeScan.Routing.cs`, `FileIndex.VolumeScan.RescanCoalescing.cs`, `FileIndex.VolumeScan.Teardown.cs` | Attach and detach, shared settle, change routing, coalescing, last-detach teardown |
| Create `MFTLib/Index/VolumeScanKey.cs`, `VolumeScanRegistry.cs` | The share key and the process registry |
| Modify `MFTLib/Index/FileIndex.cs`, `FileIndex.Disposal.cs`, `FileIndex.Queries.cs`, `FileIndex.Watch.cs`, `FileIndex.Roots.cs`, `FileIndex.Scanning.cs`, `FileIndex.Hooks.cs`, `FileIndex.CatchUpWait.cs`, `FileIndexOptions.cs` | `FileIndex` forwards to its view |
| Modify `MFTLib.Tests/NativeSeamIsolationTests.cs` | `VolumeScanRegistry.Process` is a process seam |
| Create `MFTLib.Tests/Index/MappedBlockTests.cs`, `FileIndexSharedOpenTests.cs`, `FileIndexSharedDisposalTests.cs`, `FileIndexSharedWatchTests.cs`, `FileIndexSharedRoutingTests.cs`, `FileIndexSharedRescanTests.cs`, `FileIndexSharedViewTests.cs`, `MFTLib.Tests/TestSupport/SharedScanFixture.cs` | Sharing tests |
| Modify `SampleProgram.Watch/SampleHost.WatchDrives.cs`; create `SampleProgram.Watch/SampleHost.AddRoot.cs` | `add-root <path>` |
| Modify docs (Task 5.11) | Contracts and rulings |

Files over 500 lines: none planned. `FileIndex.VolumeScan.WatchStart.cs` (275 lines) and `.WatchPump.cs` (255) grow
by the per-view loops; split a file that crosses 500 rather than adding a note.

**Commands used below:**

```powershell
.\scripts\build-windows.ps1 -NoRestore
dotnet build MFTLib.Tests/MFTLib.Tests.csproj -c Release -p:Platform=x64
dotnet test MFTLib.Tests/MFTLib.Tests.csproj -c Release -p:Platform=x64 --no-build --filter "<filter>"
```

**`SharedScanFixture`** (Task 5.4, used by Tasks 5.4 to 5.8): an `IndexTreeFixture` subclass whose tree holds real
directories `src`, `src\deeper` and `work`; a private `VolumeScanRegistry` instance; one shared
`SyntheticIndexSource.Create(producer, watches)` whose `SyntheticMftProducer` counts its calls (`ProductionCount`)
and can be gated on a `TestGate` (`HoldProduction()`); rows 5 (root), 30 `src`, 31 `src\a.txt`, 34 `src\deeper`,
35 `src\deeper\c.txt`, 32 `work`, 33 `work\b.txt`; volume T mapped to the tree root through
`SyntheticVolumeFacts.Apply(..., volume: 'T', volumeRootDirectory: TreeRoot)` with a unique serial;
`Options(RootKey root, CacheMode? mode = null, CacheTag tag = default)`; `OpenAsync(root, ...)`; `Volume`, `Source`,
`Work` paths. Every option set carries `ScanRegistry = Registry`.

---

### Task 5.1: Plan bookkeeping

**Files:** modify `docs/superpowers/plans/2026-10-27-issue-844-implementation-plan.md`.

- [x] **Step 1:** delete the "# Slice 3" and "# Slice 4" sections (both merged) and replace "# Slice 5" with this text.
  Leave the header sections, "# Slice 6" and "Execution notes" unchanged.
- [x] **Step 2:** `git diff --check`; `LC_ALL=C.UTF-8 grep -cP '\x{2014}' docs/superpowers/plans/2026-10-27-issue-844-implementation-plan.md`
  prints 0; `aislop scan --staged` 100.
- [x] **Step 3: Commit** `docs: expand slice 5 of the issue 844 plan`.

### Task 5.2: A mapped block shared by per-view root blocks

Behavior-preserving: every index still has one view, so nothing outside the new tests observes the split.

**Files:**
- Create: `MFTLib/Index/MappedBlock.cs`, `MFTLib/Index/ViewBorrows.cs`
- Modify: `MFTLib/Index/RootBlock.cs` (finalizer moves out), `SnapshotBorrow.cs:16,43`, `FileEntry.cs` (rebinding
  factory, equality XML), `FileEntry.Navigation.cs:64-75` (`Children` borrows through the root block),
  `SubtreeAnchor.cs` (`ForeignOrDisposed` message), `FileIndex.RootRuntime.Disposal.cs` (`_retiredBlocks` holds
  `MappedBlock.ReleaseState`, unchanged type)
- Test: create `MFTLib.Tests/Index/MappedBlockTests.cs`; extend `MFTLib.Tests/Index/FileEntryDisposalTests.cs`

**Interfaces:**
- `internal sealed class MappedBlock(BlockFile block)`: `BlockFile Block`, `RootBlockRelease ReleaseState`,
  `ProducerKind ProducerKind`, `ReportedReasonCycles ReportedCycles`, `bool IsReleased => ReleaseState.IsReleaseStarted`;
  the finalizer and its `RCS1075` suppression move here verbatim from `RootBlock`. Invariant I6 in its class comment.
- `internal sealed class ViewBorrows`: `bool TryTake()` (false once closed; counted under a `Lock`), `void Return()`,
  `bool IsClosed` (volatile), `Task CloseAndDrainAsync()` (closes, completes when the count reaches zero,
  continuations asynchronous), `int OutstandingCount` (read under the lock, for tests).
- `RootBlock`: keep `RootBlock(RootKey root, BlockFile block, string? rootDirectoryPath = null, MftIndexSource?
  mftSource = null, ViewRoot? view = null)` (a block-root handle owning a fresh `MappedBlock` and no `ViewBorrows`; the
  59 test call sites and the scan's own handle use it) and add `RootBlock(RootKey root, MappedBlock mapped, ViewBorrows
  viewBorrows, string? rootDirectoryPath, MftIndexSource? mftSource, ViewRoot? view)`. `MappedBlock Mapped`;
  `Block`, `ReleaseState`, `ProducerKind`, `ReportedCycles` forward to `Mapped`; `IsReleased => Mapped.IsReleased ||
  _viewBorrows is { IsClosed: true }`; `bool TryTakeBorrow()` and `void ReturnBorrow()` per invariant I5.
- `SnapshotBorrow` calls `RootBlock.TryTakeBorrow`/`ReturnBorrow`; `FileEntry.Children` borrows through
  `SnapshotBorrow`, which it already does.
- `internal FileEntry FileEntry.WithRootBlock(RootBlock rootBlock)`: same row index and sequence number, another
  handle; used by routing (Task 5.6).
- `SubtreeAnchor.ForeignOrDisposed` text: "...it came from another index, another view of the same volume, or a block
  a rescan replaced."

- [x] **Step 1: Write the failing tests** (`MappedBlockTests`; two view handles over one `MappedBlock` built from
  `TestBlocks`, each with its own `ViewBorrows`)

```csharp
public void TwoViewHandles_ShareOneMapping()             // AreSame(first.Block, second.Block); first.Equals is reference
public void ClosingOneView_RefusesItsBorrowsOnly()      // CloseAndDrainAsync on A: A.TryTakeBorrow false, B true
public async Task CloseAndDrain_WaitsForItsOwnBorrowsOnly()
// A borrow on A and one on B; A's drain task incomplete until A's borrow returns; B's borrow still out afterwards
public void ClosedView_EntryThrowsObjectDisposed_WhileTheOtherViewReads()
// FileEntry.Create(A, 31).Name throws ObjectDisposedException after closing A; FileEntry.Create(B, 31).Name reads
public void EntriesOfTwoViews_OfOneRow_AreNotEqual()     // same row and sequence, different handles: not Equal
public void WithRootBlock_KeepsTheCapturedSequence()      // a row reused after minting: the rebound entry reads IsDeleted
public void Anchor_FromAnotherViewOfTheBlock_IsRejected() // SubtreeAnchor.Resolve(B, query anchored on A) throws ArgumentException
public void ReleasingTheMapping_ReleasesEveryView()       // Mapped.ReleaseState.ReleaseUnborrowed: A and B IsReleased
```

- [x] **Step 2: Run, expect FAIL**: `--filter "FullyQualifiedName~MappedBlockTests"` (compile errors).
- [x] **Step 3: Implement** the Interfaces.
- [x] **Step 4: Run, expect PASS**: the filter plus `FullyQualifiedName~FileEntry|FullyQualifiedName~SubtreeAnchor|FullyQualifiedName~Disposal|FullyQualifiedName~RootBlock`, then Smoke (`--filter TestCategory=Smoke`).
- [x] **Step 5: Commit** `refactor: share one mapped block between per-view root blocks`.

### Task 5.3: `RootRuntime` splits into `VolumeScan` and `IndexView`

Use the `effective-refactor` skill: move by compiler errors, no behavior change. Every scan still has exactly one view
and no registry exists yet, so the full suite is the test. All later tasks build on the names fixed here.

**Files:**
- Rename: `git mv MFTLib/Index/FileIndex.RootRuntime.cs MFTLib/Index/FileIndex.VolumeScan.cs`, and the same for
  every `FileIndex.RootRuntime.*.cs`
- Create: `MFTLib/Index/FileIndex.IndexView.cs`, `FileIndex.IndexView.Delivery.cs` (from
  `FileIndex.RootRuntime.Notifications.cs`'s raise methods), `FileIndex.VolumeScan.Views.cs`
- Modify: `MFTLib/Index/FileIndex.cs`, `FileIndex.Disposal.cs`, `FileIndex.Queries.cs`, `FileIndex.Watch.cs`,
  `FileIndex.Roots.cs`, `FileIndex.Scanning.cs`, `FileIndex.Hooks.cs`, `FileIndex.CatchUpWait.cs`, `FileIndex.Events.cs`;
  `docs/architecture.md:18` and `docs/watch-lifetime.md:75,120,155` only where the type name appears (full doc pass in
  Task 5.11)

**Interfaces:**
- `internal sealed partial class FileIndex.VolumeScan(VolumeScanCreation creation)`, nested in `FileIndex` as
  `RootRuntime` is today. `internal sealed record VolumeScanCreation(ResolvedRoot Resolved, FileIndexOptions Options,
  string CacheDirectoryPath, IndexHooks Hooks)`, taken from the creating view (decision 6).
  - `Lock StateLock { get; } = new()`: the scan's state lock, innermost, never held across an `await`. It replaces
    `FileIndex._stateLock`; `FileIndex` reads it as `StateLock => _view.Scan.StateLock`.
  - `CancellationTokenSource _closing`, `CancellationToken ClosingToken`, `bool IsClosing`, and
    `void ThrowIfClosing(CancellationToken)` replace `Index.DisposalToken`, `Index._disposed` and
    `Index.ThrowIfCancelledByDisposal` inside scan code. Production, pumps, recoveries and restarts observe the closing
    token; a caller-facing wait links the caller's token with its view's disposal token (Task 5.4 and Task 5.7 rely on
    this split).
  - `RootBlock? Block` is the scan's own block-root handle (block-root `RootBlock` ctor over the current
    `MappedBlock`, key `Resolved.BlockRoot`); the mutator, `BuildPath` for changes and warm-start checks use it.
  - `readonly List<IndexView> _views` (invariant I3) with `void AttachFirstView(IndexView view)` (this task: called once,
    from the open) and `IReadOnlyList<IndexView> ViewsLocked()`.
  - Everything else `RootRuntime` owns stays here unchanged in this task, including `_watchRequested`, the watch
    instances, faults and checkpoint facts.
- `internal sealed partial class FileIndex.IndexView(FileIndex index, ResolvedRoot resolved)`:
  `ResolvedRoot Resolved` (moved from the runtime, with `Root`, `Kind`, `RootDirectory`, `MatchableRootDirectoryPath`
  and the open-time `AsWalk` swap); `VolumeScan Scan` (assigned only during `OpenAsync`, before the index is returned;
  say so in its comment); `RootBlock? Block` (this view's handle on the scan's current `MappedBlock`, built through the
  view ctor of Task 5.2 with `Borrows` and this view's `ViewRoot`); `ViewBorrows Borrows`; `bool IsDisposed`
  (written under `Scan.StateLock`); `CancellationToken DisposalToken` (the index's); the delivery state moved from
  `Notifications.cs`: `DeliveryLock`, `FaultDeliveryLock`, `_pendingWatchStates`, `ContendedWatchStateDeliveries`.
- `IndexView.Delivery`: `void QueueWatchStateLocked(WatchStateChange change)`, `void RaiseWatchStateChanged()`,
  `void RaiseWatchFaulted(WatchFault fault)`, `void RaiseChanged(IReadOnlyList<FileChange> changes)` (bodies moved; they
  read `Index.ChangedSubscribers` and the other two events). The scan's `NoteWatchStateLocked` queues one change on
  every view in `_views` built with `DescribeWatchLocked(view)`; the scan's raise sites call a `ForEachView` helper that
  snapshots `_views` under the state lock and raises outside it.
- `BuildRootBlock` returns the scan handle and, per view, `IndexView.BindBlock(MappedBlock mapped)` builds and
  refreshes the view's handle (invariant I4: under the write gate, outside the state lock); `AdoptOpenedRoot` and
  `PublishRescannedBlockAsync` set `Block` on the scan and every view in one state-lock section.
- `DescribeLocked(IndexView view)` reads the view's handle for presence (`View.IsPresent`) and the view's `Resolved`
  for `Producer` and `MftUnavailable`; scan facts unchanged.
- `FileIndex`: `readonly IndexView _view`; every member that read `_runtime` reads `_view` or `_view.Scan`;
  `BorrowBlock` lends `_view.Block`; `DisposeAsync` runs today's sequence against the view and its (only) scan.
- `IndexHooks` stays one class: `RootsReadBeforeLock`, `ReaderAdmitted` and `DisposedFlagSet` are read through the
  view's index; every other hook through `VolumeScan.Hooks` (the creating view's).

- [x] **Step 1: Rename and move** by compiler errors. `grep -rn "RootRuntime" --include=*.cs MFTLib MFTLib.Tests MFTLibTestExtensions`
  returns nothing (test helpers that named it in strings or comments follow).
- [x] **Step 2: Build and run the full suite**: `.\scripts\build-windows.ps1 -NoRestore`, then the full suite.
  Expected: 0 failed, the same pass count as main.
- [x] **Step 3: Commit** `refactor: split the root runtime into a volume scan and an index view`.

### Task 5.4: The registry and shared opens

**Files:**
- Create: `MFTLib/Index/VolumeScanKey.cs`, `MFTLib/Index/VolumeScanRegistry.cs`, `MFTLib/Index/FileIndex.VolumeScan.Settle.cs`,
  `MFTLib.Tests/TestSupport/SharedScanFixture.cs`
- Modify: `MFTLib/Index/FileIndexOptions.cs` (internal `ScanRegistry`), `FileIndex.cs` (`OpenWithHooksAsync`),
  `FileIndex.Scanning.cs`, `FileIndex.VolumeScan.Open.cs` (`AddRootAsync`, `SettleResolvedRootAsync`,
  `TakeFallbackRequest`, `RecordFailedRoot`), `FileIndex.VolumeScan.CacheOwnership.cs:14-30` (mode from the settle),
  `FileIndex.VolumeScan.WarmStart.cs:122,158` (mode from the settle), `FileIndex.VolumeScan.Views.cs` (attach, detach),
  `FileIndex.VolumeScan.ScanProgress.cs` and every `DiagnosticsReport.Send(Options.Diagnostics, ...)` site in scan code
  (fan-out), `FileIndex.VolumeScan.Status.cs` (view-level failure), `FileIndex.Disposal.cs` (detach; teardown only when last)
- Test: create `MFTLib.Tests/Index/FileIndexSharedOpenTests.cs`; triage the suite (Step 5)

**Interfaces:**
- `internal readonly record struct VolumeScanKey(MftIndexSource? Source, VolumeKey Volume, uint VolumeSerial,
  string? CacheDirectory, CacheTag CacheTag)` with `Equals`/`GetHashCode` per decision 2 (`RuntimeHelpers.GetHashCode`
  for the source). `static VolumeScanKey? For(ResolvedRoot resolved, FileIndexOptions options, string cacheDirectoryPath)`:
  null when decision 1 gives a private scan (the directory-exists check stays in the open, before this call).
- `internal sealed class VolumeScanRegistry` (invariants I1 and I2 in its class comment):
  - `internal static VolumeScanRegistry Process { get; } = new();`
  - `internal FileIndex.VolumeScan Attach(VolumeScanKey key, Func<Task, FileIndex.VolumeScan> create)`: under the
    registry lock, returns the live scan and increments its view count, or calls `create(previousClosed)` (the closing
    scan's `Closed`, or a completed task) and registers it with count 1.
  - `internal bool Detach(VolumeScanKey key, FileIndex.VolumeScan scan)`: decrements; at zero removes the key, records
    `scan.Closed` as the key's closing task (removed by a continuation when it completes, only if still the same task)
    and returns true.
  - `internal int LiveScanCount`, `internal bool IsLive(VolumeScanKey key)` (tests).
- `FileIndexOptions`: `internal VolumeScanRegistry? ScanRegistry { get; init; }`.
- `VolumeScan` gains `VolumeScanKey? Key` (null for a private scan), `Task Closed`, `Task PreviousClosed` (awaited at
  the top of the first settle, before `ResolveCanonicalOwnership`).
- Settle (`FileIndex.VolumeScan.Settle.cs`), invariant I11:
  - `internal Task<ViewSettleOutcome> SettleViewAsync(IndexView view, CancellationToken cancellationToken)`;
    `internal enum ViewSettleOutcome { Settled, FallBackToWalk }`.
  - Under the state lock: a scan with a block attaches the view at once (`AttachToCurrentBlockAsync`, below); else the
    view joins `_settle` (a `SettleRun` with `Task Completion`, `bool Writes`, `TaskCompletionSource<bool> WarmVerdict`
    completed with true when warm adoption published a block and false when the settle moves on to a cold scan), or
    creates one and runs it on the thread pool under the lifecycle gate with the closing token. The view awaits
    `Completion.WaitAsync` linked with its own token; an `ExistingOnly` view awaits `WarmVerdict` instead and on false
    records its view-level decline (decision 3).
  - The run's body is today's `SettleResolvedRootAsync` with `Options.CacheMode == ExistingOnly` replaced by
    `!run.Writes` in `ResolveCanonicalOwnership`, `RejectUnresumableCheckpoint`, `DiscardRejectedBlock` and the
    cold-scan decision. A run that ends blockless leaves `_settle` null, so the next opener runs a new one (decision 4);
    a writing joiner of a non-writing run that ended blockless starts one writing run.
  - `internal Task AttachToCurrentBlockAsync(IndexView view, CancellationToken cancellationToken)`: takes the write
    gate, binds the view's handle and refreshes its `ViewRoot`, then adds the view to `_views` and sets its block in
    one state-lock section (invariant I3); releases the gate.
  - Fallback: `SettleViewAsync` returns `FallBackToWalk` for an Automatic view when the run ended blockless with
    `RootFailureKind.MftUnavailable` (`TakeFallbackRequest` reads the scan's recorded reason); the open then detaches,
    swaps `Resolved` to `AsWalk(reason)`, attaches a private scan and settles it.
- View-level failure: `IndexView.OpenFailure : (RootFailure Failure, PendingRootResult Result)?`; `DescribeLocked`
  reports it as `Failed` with no block before reading scan facts; a successful rescan through the view clears it
  (Task 5.7 coalesces that rescan).
- `OpenWithHooksAsync`: prepare, resolve, build the index and its view; `key = VolumeScanKey.For(...)`; the scan is
  `registry.Attach(key, previousClosed => new VolumeScan(creation with the view's values, key, previousClosed))` or a
  private `new VolumeScan(...)`; then `SettleViewAsync`. On any exception: detach (`IndexView.DetachAsync`, the last
  detach awaiting teardown, Task 5.5 completes it) and rethrow; a cancelled open still throws
  `OperationCanceledException` with the caller's token.
- Fan-out (decision 6): `VolumeScan.Diagnostics` (`Action<string>`) and `VolumeScan.Progress`
  (`IProgress<IndexScanProgress>`) iterate a `_views` snapshot; `BlockFile.Diagnostics` is set to `Diagnostics`.
- `FileIndex.DisposeAsync` in this task: when other views remain, marks the view disposed, cancels its token, drains
  its `Borrows`, clears its watch request if it holds one without retiring a watch other views need, removes it from
  `_views` and detaches; the last view runs today's full sequence as `VolumeScan.CloseAsync` (Task 5.5 owns the tests).

- [x] **Step 1: Write the failing tests** (`FileIndexSharedOpenTests` on `SharedScanFixture`; parallel-safe: private
  registry, own source, unique serial, temporary cache directory)

```csharp
public async Task TwoViews_OneProduction()
// open T and T\src: ProductionCount 1; both Ready, Producer Mft; AreSame(a.CurrentBlock.Mapped, b.CurrentBlock.Mapped);
// equal Generation and LiveRowCount; Registry.LiveScanCount 1; one *.mlix and one owner lock in the cache directory
public async Task ConcurrentOpens_ShareOneProduction()
// HoldProduction; start src, work and T; release; ProductionCount 1; all three Ready
public async Task SecondOpen_DuringProduction_WaitsForIt()   // second open incomplete while held; Ready after release
public async Task CancelOneOfTwoOpens_ProductionContinues()
// both waiting on a held production; cancel the second: it throws OperationCanceledException; the producer's token is
// not cancelled at release; the first is Ready; IsLive(key) true until the first is disposed, false after
public async Task CancelTheOnlyOpen_CancelsProduction()
// producer observes its token cancelled; IsLive false; a reopen reads CacheLocation OwnedCanonical
public async Task AttachDuringLive_IsReadyAtOnceAndSeesTheBatch()
// view on T watching and CaughtUp; ApplyJournalEntriesInsideWriteGate holds a batch that creates work\new.txt; open a
// work view: incomplete while the batch is held; after release Ready, Find(work\new.txt) not null, ProductionCount 1
public async Task ExistingOnlyView_OnAColdScan_DeclinesWithoutWaiting()
// writing open of T held in production; ExistingOnly open of src completes while held: Failed, CacheDeclined,
// Status.Block null; release: T Ready, src still Failed; src RescanAsync Succeeded, then src Ready
public async Task ExistingOnlyView_JoiningAWarmAdoption_IsReady()   // cached block present: both Ready, ProductionCount 0
public async Task WritingView_JoiningADeclinedCacheOnlySettle_ScansOnce()
// ExistingOnly open first (no cache) and a Persistent open racing it: ExistingOnly Failed CacheDeclined, Persistent
// Ready, ProductionCount 1
public async Task BlocklessScan_ResettlesForANewOpener()
// producer fails once: first open Failed ProducerFailed; second open of src while the first is attached: Ready, and
// the first now reads Ready (ProductionCount 2)
[DataRow("Tag")] [DataRow("CacheDirectory")] [DataRow("Source")] [DataRow("Serial")]
public async Task DifferentKeys_DoNotShare(string difference)
// ProductionCount 2 across both producers; LiveScanCount 2; for Tag and Source the second reads PrivateFallback
public async Task WalkAndDump_NeverRegister()               // WalkOnly root and a dump: LiveScanCount 0
public async Task AutomaticFallback_EachViewWalks_MftOnlySiblingFails()
// source null, no cache: Automatic T and Automatic src walk (Producer Enumeration, NoMftSource); MftOnly work Failed
// MftUnavailable NoMftSource; LiveScanCount 1 while work is open, 0 after
public async Task Diagnostics_AndProgress_ReachEveryAttachedView()
// two views with distinct sinks: each receives the cache-validation line once; each progress sample carries its own Root
public async Task Junction_ToAnotherVolume_AttachesToThatVolumesScan()
// FinalPath seam maps T\link to U\data; a U view and the T\link view share one scan (ruling 16); ProductionCount 1
```

- [x] **Step 2: Run, expect FAIL**: `--filter "FullyQualifiedName~FileIndexSharedOpenTests"`.
- [x] **Step 3: Implement** the Interfaces, with invariants I1 to I3 and I11 in comments at their state.
- [x] **Step 4: Run, expect PASS**: the filter plus `FullyQualifiedName~FileIndexProducerSelectionTests|FullyQualifiedName~FileIndexMftFallbackTests|FullyQualifiedName~FileIndexDirectoryViewTests|FullyQualifiedName~FileIndexRootResolutionTests`.
- [x] **Step 5: Triage the full suite.** Run it; every new failure is one of: (a) a test that opens two MFT indexes with
  one source instance, volume, serial, cache directory and tag and now shares by design: assert the sharing instead
  (expected in `FileIndexOwnerLockTests`, `CacheDirectoryDeletionLiveOwnerTests`, `FileIndexCacheTagTests` for its
  equal-tag cases); (b) a test about cross-key contention: give its second open a distinct source
  (`SyntheticIndexSource.Create(...)` again) so it keeps testing contention; (c) a message that now names the scan's
  block root (decision 6): update the expectation. List each changed test with its letter in the commit body. Expected
  afterwards: 0 failed.
- [x] **Step 6: Commit** `feat: share one volume scan between the MFT indexes of a share key`.

### Task 5.5: View disposal and the last detach

**Files:**
- Create: `MFTLib/Index/FileIndex.VolumeScan.Teardown.cs` (from `FileIndex.Disposal.cs`'s gate and release section),
  `MFTLib/Index/FileIndex.IndexView.Lifecycle.cs`
- Modify: `MFTLib/Index/FileIndex.Disposal.cs`, `FileIndex.IndexView.Delivery.cs` (delivery gate),
  `FileIndex.VolumeScan.Disposal.cs`, `VolumeScanRegistry.cs`
- Test: create `MFTLib.Tests/Index/FileIndexSharedDisposalTests.cs`

**Interfaces:**
- `IndexView.Deliveries` (invariant I9): `bool TryEnterDelivery()`, `void ExitDelivery()`, `Task CloseDeliveriesAsync()`;
  every raise on a view (`Changed`, `WatchFaulted`, `WatchStateChanged`) runs inside it; a closed gate drops the raise.
- `IndexView.DisposeAsync()` (the spec's order): under the state lock set `IsDisposed` (later `Status` throws
  `ObjectDisposedException`); cancel the view's disposal token (its queries, catch-up waits, rescan waits and open
  wait); `Borrows.CloseAndDrainAsync()`; withdraw its watch request (Task 5.6 gives the full per-view rule; here: when
  it is the last requester, today's `PrepareDisposalLocked` and drain); `CloseDeliveriesAsync()`; under the state lock
  remove it from `_views`; `registry.Detach` (private scans count as last); when last, `await Scan.CloseAsync()`.
- `VolumeScan.CloseAsync()` (invariant I12): today's `StopWatchForDisposalAsync` plus `ReleaseBlocksForDisposalAsync`
  plus `ReleaseCanonicalOwnershipLocked`, cancelling `_closing` first, then completing `Closed`; idempotent; a release
  failure still completes `Closed` (in a `finally`) and propagates from the disposing view only.
- `IndexHooks.LifecycleGatesTakenForDisposal` fires only in `CloseAsync`; `DisposedFlagSet` fires per view.
- The final `WatchStateChanged` before `DisposeAsync` returns is delivered only by a disposal that stopped the watch
  (watch-lifetime "Disposal delivers the final change").

- [x] **Step 1: Write the failing tests** (`FileIndexSharedDisposalTests` on `SharedScanFixture`)

```csharp
[DataRow(true)] [DataRow(false)]
public async Task DisposeOrder_EitherWay_KeepsTheOtherViewServing(bool volumeViewFirst)
// T and src: after the first dispose the other is Ready, Find and Search work, its Watch still CaughtUp when requested,
// the mapping is not released and the owner lock file is held; after the second the mapping is released
// (ReleaseState.IsReleaseComplete), IsLive false, and a new open reads OwnedCanonical
public async Task Dispose_NotLast_DoesNotWaitForAnotherViewsBorrow()
// src holds a suspended Enumerate enumerator; T.DisposeAsync completes; src's enumerator then advances
public async Task Dispose_Last_UnmapsAfterItsBorrowsDrain()
// T disposed; src holds a suspended enumerator; src.DisposeAsync incomplete and mapping not released until the
// enumerator is disposed, then complete and released
public async Task Dispose_HandlesOfTheDisposedView_ThrowWhileTheOtherViewsRead()
// entry from T: Name throws ObjectDisposedException, IsDisposed true; the same row through src reads its Name
public async Task Dispose_CancelsOnlyItsOwnQueries()
// ReaderAdmitted hook parks a Search on each view; disposing T cancels T's search (OperationCanceledException) and
// leaves src's running to completion
public async Task Dispose_WaitsForADeliveryInsideItsHandler_AndNoHandlerRunsAfter()
// a TestGate blocks T's Changed handler mid-delivery; T.DisposeAsync incomplete until released; a later batch reaches
// src only (T's handler count unchanged)
public async Task Reopen_RacingTheLastDetach_OwnsTheCanonicalSlot()
// ReleaseState hook parks the last disposal before unmapping; a reopen started then completes after it with
// CacheLocation OwnedCanonical and ProductionCount unchanged (warm adoption)
public async Task Dispose_TwiceAndConcurrently_IsIdempotent()   // two concurrent DisposeAsync calls on one view, one teardown
```

- [x] **Step 2: Run, expect FAIL**: `--filter "FullyQualifiedName~FileIndexSharedDisposalTests"`.
- [x] **Step 3: Implement**; invariants I5, I6, I9 and I12 in comments.
- [x] **Step 4: Run, expect PASS**: the filter plus `FullyQualifiedName~Disposal|FullyQualifiedName~FileIndexDisposeCancelsQueryTests|FullyQualifiedName~FileEntryDisposalTests`.
- [x] **Step 5: Commit** `feat: dispose one view of a shared scan without disturbing the others`.

### Task 5.6: Watch requests per view, fault fan-out and event routing

**Files:**
- Create: `MFTLib/Index/FileIndex.VolumeScan.Routing.cs`
- Modify: `MFTLib/Index/FileIndex.VolumeScan.cs` (`_watchRequested` becomes `_requesters`),
  `FileIndex.VolumeScan.WatchStart.cs`, `.WatchStop.cs`, `.WatchPump.cs` (`ApplyWatchBatchAsync`,
  `AnnounceSubscriberFault`, `RecordPumpFault`), `.WatchCatchUp.cs` (`GetCatchUpWaitLocked`), `.RescanRestart.cs`,
  `.Recovery.cs`, `.CheckpointLoss.cs` (fault sites), `.Status.cs` (`DescribeWatchLocked(view)`, `IsWatchingLocked(view)`),
  `.JournalMutation.cs` (refresh every view's binding under the write gate), `FileIndex.Watch.cs`
  (`ApplyJournalEntriesAsync` returns the caller's routed changes), `FileIndex.IndexView.cs`,
  `MFTLibTestExtensions/FileIndexTestAccess.cs` (XML only: returns the changes routed to `index`)
- Test: create `MFTLib.Tests/Index/FileIndexSharedWatchTests.cs`, `MFTLib.Tests/Index/FileIndexSharedRoutingTests.cs`

**Interfaces:**
- `HashSet<IndexView> _requesters` and `IndexView.WatchRequested` (invariant I7). `bool AnyRequestLocked` replaces
  every read of `_watchRequested` (rescan restart decision, restart-pending waiter, recovery restart, state derivation).
- Start: sets the view's request before invoking the source, as today; when the scan's watch already holds readiness
  the call returns `Succeeded` without touching the source; a refusal over an unresumable block keeps this view's request
  and throws to this caller. A start its own caller cancelled clears only its own request (and the instance when it was
  the only requester).
- Stop (decision 8): `IsWatchingLocked(view) => view.WatchRequested || (_requesters.Count == 0 && (Current is not null
  || _retiring is not null))`. Withdrawing a request while others remain returns at once with the view's outstanding
  fault; withdrawing the last runs today's stop (stop wins, drain, outstanding fault once).
- Faults (invariant I8): `IndexView.OutstandingFault`; scan fault sites copy the fault to each view in `_requesters`
  under the state lock and raise `WatchFaulted` on that same set after releasing it; a superseding consumer start
  clears every view's copy of the superseded instance's fault. `AnnounceSubscriberFault(instance, view, exception)`
  keeps `WatchInstance.SubscriberFaultAnnounced` per view (`HashSet<IndexView>`).
- `DescribeWatchLocked(IndexView view)`: the scan's watch facts with `IsRequested = view.WatchRequested`.
  `WaitForCatchUpAsync` follows the scan's current or pending instance for any view; the restart-pending waiter exists
  while `AnyRequestLocked`.
- Routing (decision 7), in `Routing.cs`: `IReadOnlyList<(IndexView View, IReadOnlyList<FileChange> Changes)>
  RouteLocked(IReadOnlyList<FileChange> changes)`, computed at the end of the apply under the write gate (the views in
  `_views` then are exactly those whose bindings saw the batch); `static bool Contains(IndexView view, FileChange change)`
  (`view.Block.View.IsDirectoryView` false means true; otherwise `TryMatchRootPrefix` of `Path`, then of
  `PreviousPath`, with one reused fold buffer per batch); each routed change is `change with { Entry =
  change.Entry.WithRootBlock(view.Block) }`. The pump raises each view's list on that view, in `_views` order, with no
  gate held; a subscriber exception is attributed to that view.
- `JournalMutation`: after the apply, `ViewRoot.Refresh` for every directory view's handle, still under the write gate
  (cost per batch: path depth per directory view).

- [x] **Step 1: Write the failing tests** (`SharedScanFixture` with a `ScriptedWatchSource` that counts
  `StartAsync` calls and handle disposals)

```csharp
// FileIndexSharedWatchTests
public async Task SecondStart_WhileReady_ReturnsWithoutASecondWatch()   // starts counted 1; both IsRequested true
public async Task NonRequestingView_ReportsTheScansStateWithIsRequestedFalse()
public async Task Stop_WithAnotherRequest_KeepsTheWatch()
// T and src start; T stops: Succeeded, handle not disposed, src still CaughtUp; src stops: handle disposed, NotStarted
public async Task Stop_OfANonRequestingView_IsNotApplicableWhileAnotherWatches()
public async Task Stop_OfTheLastRequesterDuringARescan_PreventsTheRestart()   // stop wins: starts counted 1 after commit
public async Task VolumeFault_ReachesEveryRequestingView_AndOneRecoveryRuns()
// scripted Volume fault: WatchFaulted(Volume) once on T and on src, none on work (not requesting); ProductionCount +1
public async Task OutstandingFault_IsReturnedOncePerRequestingView()
// Channel fault: T.Stop Failed with it, src.Stop Failed with it, a second stop of either NotApplicable
public async Task SubscriberFault_StaysOnTheThrowingView()
// src's handler throws: WatchFaulted(Subscriber) on src only, once; T keeps receiving; src's stop returns it once
public async Task RescanRestartFault_ReachesEveryRequestingView()
public async Task WatchStateChanged_ReachesEveryAttachedView_InVersionOrder()   // WatchStateRecorder on both views
public async Task WaitForCatchUp_OnANonRequestingView_FollowsTheScansInstance()
// FileIndexSharedRoutingTests (direct apply through FileIndexTestAccess and through the pump)
public async Task Route_AChangeInsideAView_ReachesItAndTheVolumeView()     // src\a.txt modified: src and T, not work
public async Task Route_AChangeOutsideEveryDirectoryView_ReachesOnlyTheVolumeView()
public async Task Route_AMoveAcrossViews_ReachesBothAsRenamed()
// src\a.txt to work\a.txt: one Renamed on src, one on work, one on T; Path and PreviousPath identical on all three
public async Task Route_TheViewRootRenamedAway_ReachesTheViewThenItGoesOffline()
public async Task Route_NestedViews_BothReceive()                         // src and src\deeper: c.txt reaches both
public async Task Route_EntriesAreMintedOnTheReceivingView()
// change.Entry.Root is the view's key; usable as that view's Subtree anchor; rejected as the other view's
public async Task Route_ABatchConcurrentWithAttach_IsSeenExactlyOnce()
// Review Focus 1: a held batch and a pending attach of work; after both, work's Find sees the batch's file and work's
// Changed received no change of that batch; the next batch's change under work reaches it once
public async Task ApplyJournalEntries_ReturnsTheCallersRoutedChanges()   // through src: only src's share of the batch
```

- [x] **Step 2: Run, expect FAIL**: `--filter "FullyQualifiedName~FileIndexSharedWatchTests|FullyQualifiedName~FileIndexSharedRoutingTests"`.
- [x] **Step 3: Implement**; invariants I7 and I8 in comments.
- [x] **Step 4: Run, expect PASS**: the filter plus `FullyQualifiedName~FileIndexWatch|FullyQualifiedName~FileIndexPerRootWatchTests|FullyQualifiedName~FileIndexDirectoryViewTests`.
  Update `FileIndexDirectoryViewTests`' volume-wide `Changed` expectation (slice 4 decision 6) to the routed one.
- [x] **Step 5: Commit** `feat: per-view watch requests, fault fan-out and change routing`.

### Task 5.7: Coalesced rescans, one recovery per scan, checkpoint loss on every view

**Files:**
- Create: `MFTLib/Index/FileIndex.VolumeScan.RescanCoalescing.cs`
- Modify: `MFTLib/Index/FileIndex.VolumeScan.Rescan.cs` (`RescanRootAsync` becomes the coalescing entry),
  `.Recovery.cs` (ticket stays per scan), `.CatchUp.cs` and `.CheckpointLoss.cs` (facts reported per view through
  `DescribeLocked`), `FileIndex.Watch.cs` (`RescanAsync` passes the view)
- Test: create `MFTLib.Tests/Index/FileIndexSharedRescanTests.cs`

**Interfaces:**
- `sealed class RescanRequest`: `TaskCompletionSource Completion` (asynchronous continuations), `int Waiters`,
  `bool Started`, `CancellationTokenSource Abandoned` (cancelled when `Waiters` reaches zero before completion).
- `internal async Task RescanAsync(IndexView view, CancellationToken cancellationToken)` (invariant I10): under the state
  lock join `_pendingRescan` or create it (`Waiters++`); the creator starts `RunRescanAsync(request)` on the thread
  pool: take the lifecycle gate with `request.Abandoned` linked to the closing token; under the state lock set
  `Started` and clear `_pendingRescan`; run today's `RescanWithGateHeldAsync` with that linked token; complete
  `Completion` with its outcome. The caller awaits `Completion.Task` through `AwaitQueuedAsync` with its token linked to
  its view's disposal token; on its cancellation it decrements `Waiters` under the state lock and cancels `Abandoned` at
  zero. Every waiter of one request observes the same outcome (`RootOperationResult` per view, same failure instance).
- A successful rescan clears the `OpenFailure` (Task 5.4) of every view that joined it; every other view fact derives
  from the scan and needs no clearing.
- Recovery: unchanged ticketing; `QueueRecovery` runs once per scan fault; the restart restores the watch when
  `AnyRequestLocked`.
- `RootStatus.Watch.CheckpointLoss`, `ConsecutiveLostCatchUps`, `IsRecoveryStopped` and `JournalCheckpointLoss.DetectedDuring`
  are scan facts, identical on every view; `CatchUpLost` faults follow Task 5.6's requesting-view fan-out.

- [x] **Step 1: Write the failing tests** (`FileIndexSharedRescanTests`; production gated through the fixture)

```csharp
public async Task Rescan_TwoConcurrentCallsWhileIdle_ProduceOnce()          // T and src call together: ProductionCount +1, both Succeeded
public async Task Rescan_ThreeCallsDuringOneProduction_RunAtMostOneMore()
// first rescan held; three more calls from T, src, work: ProductionCount +2 in total, all four Succeeded (Review Focus 4)
public async Task Rescan_LateCaller_GetsAProductionStartedAfterItsCall()
// a call made after the pending request took the gate joins a new request: its result comes from a later production
public async Task Rescan_OneOfTwoCallersCancels_ProductionContinues()       // canceller OperationCanceledException; other Succeeded
public async Task Rescan_EveryCallerCancels_CancelsTheProduction()           // producer token cancelled; block unchanged
public async Task Rescan_ViewDisposedWhileWaiting_EndsOnlyItsWait()
public async Task Rescan_FailedProduction_EveryCallerGetsTheSameFailure()   // AreSame on both results' Failure
public async Task CheckpointLoss_IsReportedOnBothViews()
// CheckpointLossFixture: lost catch-up during a rescan; both views' Watch.CheckpointLoss equal, DetectedDuring equal;
// CatchUpLost raised once on each requesting view
public async Task ThreeConsecutiveLosses_StopRecoveryOnEveryView()          // IsRecoveryStopped true on both
public async Task Recovery_AfterAVolumeFault_RunsOncePerScan()             // two requesting views: ProductionCount +1
public async Task Generation_IsOneValuePerBlock()
// a batch through src bumps Generation seen by T; a rescan through T gives both the same new value
```

- [x] **Step 2: Run, expect FAIL**: `--filter "FullyQualifiedName~FileIndexSharedRescanTests"`.
- [x] **Step 3: Implement**; invariant I10 in comments.
- [x] **Step 4: Run, expect PASS**: the filter plus `FullyQualifiedName~Rescan|FullyQualifiedName~Recovery|FullyQualifiedName~CheckpointLoss|FullyQualifiedName~FileIndexConcurrentRescanTests`.
- [x] **Step 5: Commit** `feat: coalesce rescans of a shared scan`.

### Task 5.8: Views, anchors and sets over a shared block

Most behavior here already follows from Tasks 5.2 to 5.6; this task proves it and fixes what the proofs find.

**Files:**
- Modify (as the tests require): `MFTLib/Index/SubtreeAnchor.cs`, `FileIndexSet.cs` (`ResolveSubtreeOwner` matches the
  anchor's `RootBlock` by reference against each member's handle), `FileIndex.Queries.cs`
- Test: create `MFTLib.Tests/Index/FileIndexSharedViewTests.cs`; add to `MFTLib.Tests/Index/FileIndexSetTests.cs`

**Interfaces:** none new. `FileIndexSet` keeps ruling 15 (outermost available member, containment by
`TryMatchRootPrefix`) unchanged; with shared members each member still borrows its own view's handle.

- [x] **Step 1: Write the failing tests**

```csharp
// FileIndexSharedViewTests
public async Task Anchor_FromAnotherViewOfTheVolume_IsRejected()
// src's entry for src\deeper as Subtree on T's Search: ArgumentException naming another view; on src's Search accepted
public async Task Anchor_AfterAShareRescan_IsRejectedOnBothViews()          // a rescan through T replaces src's handle too
public async Task ViewOfflineAndBack_WhileTheOtherStaysReady()
// rename src away and back through the journal: src Offline (Block null, empty queries) then Ready; T Ready throughout
public async Task ViewKeyedByAnAlias_ComesBackAtItsResolvedPath()
// FinalPath seam maps T\alias to T\src: src renamed away and back, the alias view returns Ready (decision 11)
public async Task OfflineAtOpen_NeverAttaches()                             // missing T\gone: Offline, LiveScanCount unchanged
public async Task Root_AndParent_ArePerView()                               // src.Root().Parent null; T's src entry has a Parent
// FileIndexSetTests
public async Task NestedSharedMembers_ReturnEachFileOnceUnderTheOuter()
// one source: set of T and src; Search "*.txt" lists a.txt once with Root T; Find(src\a.txt).Root T
public async Task NestedSharedMembers_OuterDisposed_InnerCovers()          // after T disposes, a set of src alone answers
public async Task SharedMembers_SubtreeQuery_GoesToTheAnchorsMember()
```

- [x] **Step 2: Run, expect FAIL**: `--filter "FullyQualifiedName~FileIndexSharedViewTests|FullyQualifiedName~FileIndexSetTests"`.
- [x] **Step 3: Implement** what fails.
- [x] **Step 4: Run, expect PASS**: the filter plus `FullyQualifiedName~Subtree|FullyQualifiedName~FileIndexDirectoryViewTests`.
- [x] **Step 5: Commit** `test: anchors, views and sets over a shared volume block`.

### Task 5.9: The process registry is a guarded seam

**Files:**
- Modify: `MFTLib.Tests/NativeSeamIsolationTests.cs` (`IsNativeSeam`: a reference to
  `VolumeScanRegistry.get_Process`, matched by declaring type and name, is a seam; add one non-executed violation
  control and one isolated control that uses `new VolumeScanRegistry()` and stays parallel)
- Create: `MFTLib.Tests/Index/VolumeScanRegistryProcessTests.cs` (`[DoNotParallelize]`)

- [x] **Step 1: Write the failing tests**

```csharp
// NativeSeamIsolationTests
public void ProcessRegistryReference_RequiresClassLevelDoNotParallelize()   // violation control is reported by name
public void PrivateRegistry_StaysParallel()                                  // isolated control is not reported
// VolumeScanRegistryProcessTests (no ScanRegistry option: the default path)
public async Task DefaultOptions_ShareThroughTheProcessRegistry()           // two opens, one production, IsLive(key) on Process
[TestCleanup] public void NoScanOfThisClassStaysLive()                       // Assert.IsFalse(Process.IsLive(key)) for each key used
```

- [x] **Step 2: Run, expect FAIL**, then implement, then **PASS**: `--filter "FullyQualifiedName~NativeSeamIsolationTests|FullyQualifiedName~VolumeScanRegistryProcessTests"`.
- [x] **Step 3: Stress** (docs/test-isolation.md): three consecutive full runs with
  `-- MSTest.Parallelize.Workers=32 MSTest.Parallelize.Scope=ClassLevel` on Windows, and on Linux through
  `./scripts/coverage-linux.sh`'s filter under WSL; no exclusion added. Record serial-before and parallel-after wall
  clock and per-run results for the PR body (counts only, never logs).
- [x] **Step 4: Commit** `test: guard the process volume scan registry as a process seam`.

### Task 5.10: Watch sample `add-root <path>`

**Files:**
- Create: `SampleProgram.Watch/SampleHost.AddRoot.cs`
- Modify: `SampleProgram.Watch/SampleHost.WatchDrives.cs` (`WaitWhileWatchingAsync`, `WatchIndexAsync`),
  `SampleProgram.Watch/SampleHost.Broker.cs` (`OptionsFor` reused), usage text in `WatchArguments.cs`
- Test: add to `MFTLib.Tests/WatchHostModeTests.cs`

**Interfaces:**
- Attended `watch` with no `--seconds` reads lines from `_input` instead of one key: prints
  `Watching {roots}. Type add-root <path> to watch another root, or press Enter to stop.`; an empty line or end of input
  stops; `add-root <path>` parses with `RootKey.TryParse`, opens with `OptionsFor(source, parsed, root, ...)` (same
  source, so it attaches to the volume's scan), subscribes `ReportIndexChanges`, starts its watch, waits for catch-up and
  prints `Added root {root}: producer {status.Producer}`; another line prints
  `Unknown command '{line}'. Type add-root <path>, or press Enter to stop.` and keeps watching; an unparsable path prints
  `Root '{text}' is neither a drive nor a full path.`. Unattended and `--seconds` runs read no input (unchanged).
- Added indexes join the final stop, the change count and a new `FileIndexSet` over every watched index for
  `ReportSetQueriesAsync` (nested members allowed, ruling 15), and are disposed with the others.

- [x] **Step 1: Write the failing tests** (`WatchHostModeTests`, interactive host, scripted broker, volume T mapped
  through `prepareOptions`)

```csharp
public async Task Watch_AddRoot_AttachesAViewWithoutASecondScan()
// input "add-root {T\src}\n\n": output contains "Added root" and "producer Mft"; the scripted broker saw one scan of T
public async Task Watch_UnknownCommand_KeepsWatching()          // "frobnicate\n\n": the unknown-command line, then the stop summary
public async Task Watch_AddRootWithAnUnparsablePath_ReportsIt()
public async Task Watch_EndOfInput_Stops()
```

- [x] **Step 2: Run, expect FAIL**: `--filter "FullyQualifiedName~WatchHostModeTests|FullyQualifiedName~WatchArgumentsTests"`.
- [x] **Step 3: Implement.**
- [x] **Step 4: Run, expect PASS** with the filter plus `FullyQualifiedName~PublicMemberCallerTests`.
- [x] **Step 5: Smoke**: run the built `SampleProgram.Watch.exe watch C: --seconds 5` attended once on Windows (it
  self-elevates through the broker) and type nothing; then once without `--seconds`, `add-root C:\Windows`, Enter.
  Expected: one scan, `Added root C:\Windows: producer Mft`, changes under both roots. Record the outcome in the PR body.
- [x] **Step 6: Commit** `feat: the Watch sample attaches roots while watching`.

### Task 5.11: Documentation and rulings

**Files:** `docs/api-decisions.md`, `docs/architecture.md` (line 18 and the runtime and lock-order sections),
`docs/watch-lifetime.md` (watch request, watch state events, runtime state machine, lock order, callback reentrancy),
`docs/query-lifetime.md` (borrows per view and per block; a view's disposal drains only its own; the last detach
drains all), `docs/block-ownership.md:32,69,76` (one owner lock per scan, `RootRuntime` to `VolumeScan`),
`docs/broker-lifetime.md` (one scan channel per production and one watch channel per instance, owned by the scan),
`docs/checkpoint-loss.md` (facts per scan, reported on every view), `docs/test-isolation.md` (decision 9: the private
registry seam, the process seam rule and the cleanup check), `README.md` (where it says one scan per index),
`CHANGELOG.md` (0.3.0: volume scan sharing, routed `Changed`, Watch `add-root`), the spec's section 7 test-isolation
row (decision 9).

- [x] **Step 1: Run the docs-update check**; `grep -rn "RootRuntime\|private fallback until slice 5\|volume-wide\|until slice 5" --include=*.md --include=*.cs .`
  returns only `docs/superpowers/` lines.
- [x] **Step 2: Supersede** the rulings listed under "Public surface changes", each rewritten in place with its reason
  (MFTLib issue 844 rulings 5 and 15, spec section 2) and a revisit condition. Generation: "identical on every view of a
  scan; strictly increases on every applied batch and rescan of the scan; comparable while the consumer holds any index
  of the scan; a new scan after the last detach adopts the cached value". Anchor: "an anchor is valid when its
  `RootBlock` is the borrowed one, which names both the block and the querying view".
- [x] **Step 3: Add the ruling "Volume scan sharing"**: the share key (decision 2), what attaches (decision 1), the
  lifetime table of spec section 2 with decisions 3, 4, 5 and 12, routing and fault fan-out (decisions 7 and 8),
  coalescing (decision 10), Offline rules (decision 11), per-view option fan-out (decision 6). Reason: one MFT pass and
  one journal watch per volume however many roots a consumer registers (rulings 3 and 5); revisit only if sharing is
  wanted across processes or across share keys.
- [x] **Step 4: Check budgets**: `AGENTS.md` is not edited (its rules stay true); `wc -m AGENTS.md` below 15000 and
  `--filter FullyQualifiedName~AgentInstructionsTests` pass.
- [x] **Step 5: Commit** `docs: record volume scan sharing`.

### Task 5.12: Slice verification and PR

- [x] Full build and full suite: 0 failed; `NativeSeamIsolationTests` on Windows and Linux; Task 5.9's stress results.
- [x] `--filter "FullyQualifiedName~PublicSurfaceTests|FullyQualifiedName~PublicMemberCallerTests|FullyQualifiedName~NamespaceBoundaryTests"`:
  approved files identical to main.
- [x] Coverage gate (`.\scripts\run-coverage.ps1 -NonInteractive`, then `pwsh -NoProfile -File scripts/test-coverage-status.ps1`);
  every new file at or near its namespace's coverage, MFTLibTestExtensions 100%.
- [x] `aislop scan .` 100 with zero unsuppressed findings; `git diff --check` clean; `LC_ALL=C.UTF-8 grep -rnP '\x{2014}'`
  over changed files empty.
- [x] A/B handoff: **A/B pending (orchestrator)**. The orchestrator measures the final head against today's filtered build in the same session (five iterations) and records counts plus the reproduction command in the PR body; the owner accepts a warm working set a few percent over that control.
- [x] Whole-branch self-review, as directed by the lane brief, against invariants I1 to I12 and
  the Review Focus list; publish/acquire order, cancellation cadence and per-call cost (routing is one prefix match per
  change per view; publication one binding refresh per view) are the known risk classes.
- [x] PR body drafted in the lane report (no PR opened): starts `## Problem` (two indexes on one volume take the private fallback and scan the volume twice, and
  a directory view reports every change on its volume), cites MFTLib issue 844; **Public surface: none**, with the
  behavior changes above; the rulings superseded and added; the stress and smoke results; and **Consumer migration**, by
  file: git-wizard `MftIndexSession.cs` (one `FileIndex` per search root now shares one scan per volume; nothing to
  change beyond slice 4's list), `GitWizardApi.IndexDiscovery.cs` (per-root clipping goes, spec section 6), and
  `Watch/IndexVolumeChangeSource.*` (a directory view's `Changed` is already scoped; drop any filter by root);
  file-wizard `FileIndexHost.cs` (a drive and a directory on it share a scan; no code change required).

---

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
