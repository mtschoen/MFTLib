# Issue 844: one MFT scan per volume, every name stored, deduplicated name encoding

Status: approved by the owner 2026-10-27 (chain mftlib-030-closeout link 12), with the recommended answer to
each open question at the end. Source: MFTLib issue 844
rulings 1 to 12, the name-encoding consult synthesis (converged rulings 1 to 4, majority rulings 5 and 6,
findings 7 to 10), and the `spike/name-dedup` branch (local commits 6d3ac61a to 8dca9d42).

## Goal

A consumer names a drive or a path and a producer preference; MFTLib decides whether the MFT can serve it,
falls back to a directory walk when allowed, and reports what it did. Every MFT index stores the whole
volume with every name, so all indexes on one volume in one process are views over one scan, one block
and one journal watch. Names are stored once per block in a dual-width pool, which keeps the all-names
block smaller on disk than today's name-filtered one.

Rulings 5 and 6 predate ruling 4. With no storage filter there is no "same filter", no union filter and no
wider-filter case: every MFT index on a volume attaches to the one scan, `add-root` attaches a view, and
`--keep-name`, `--directories-only` and `RetainedFileNames` are deleted.

## 1. Public surface

### Selection and observation

Selection gains a choice (Automatic) that cannot identify a block's producer, which is the "revisit only
if" condition of the ruling "One producer enum, selection and observation". Selection and observation
therefore split: `ProducerSelection` selects, `ProducerKind` (unchanged, `Mft = 1`, `Enumeration = 2`)
observes in `RootStatus`, `IndexRecordKey`, `CachedBlockStatus` and the block header.

`IndexedRoot` is deleted. It existed to carry the consumer's producer choice and the library-derived
volume serial; both are now the library's. A root is a `RootKey` (`RootKey.TryParse` already accepts `C`,
`C:`, `C:\` and any full path), and the producer preference is an option. The library derives the volume,
serial and root directory at open, as `FromDirectory` and `FromWindowsDrive` do today.

| Change | Member | Consumer production caller of an added member |
| --- | --- | --- |
| Added | `enum ProducerSelection : uint { Automatic = 1, MftOnly = 2, WalkOnly = 3 }` (default value invalid) | git-wizard `EnumerationDiscovery.cs` and `MftIndexSession.cs` (`--no-mft` is WalkOnly, otherwise Automatic); file-wizard `ScannerState.CreateRoot` (the directory-walk setting is WalkOnly); Watch sample `--producer` |
| Added | `FileIndexOptions.Producer : ProducerSelection`, default `Automatic` | same callers |
| Added | `RootStatus.Producer : ProducerKind?`, the producer this index uses; null only before selection (Offline at open, admission failure) | git-wizard `MftIndexCacheStatusFormatter.cs` and discovery output; file-wizard `FileIndexHost.cs` drive card |
| Added | `RootStatus.MftUnavailable : MftUnavailableReason?`, non-null when Automatic walked or MftOnly failed | file-wizard `DriveDiscovery.SkipReason` text; git-wizard discovery diagnostics (replacing the classifier verdict) |
| Added | `enum MftUnavailableReason : uint { PlatformWithoutMft = 1, VolumeNotReady, NotLocalVolume, NotNtfs, NoMftSource, BrokerUnavailable, ElevationDeclined }` | same callers |
| Added | `RootFailureKind.MftUnavailable` (MftOnly could not use the MFT) | git-wizard and file-wizard failure display, which switch on `RootFailureKind` |
| Added | `static WindowsVolume.EnumerateAll() : IReadOnlyList<WindowsVolume>` (moved from `IndexedRoot.EnumerateWindowsVolumes`) | file-wizard `DriveDiscovery.cs:61`; Watch `SampleHost.Drives.cs:14` |
| Changed | `FileIndexOptions.Root : IndexedRoot?` becomes `RootKey?` | all three |
| Changed | `WindowsVolume.IsIndexable` is the same predicate Automatic uses (see open question 2) | |
| Changed | `FileIndex.QueryUsnJournalSettings()` serves any MFT view, including a directory root; walked roots still refuse | |
| Changed | `FileEntry.Parent` is null at the view's root row, not only at the volume root | |
| Changed | `IndexRowEnumerator` copies share one resource owner: advancing any copy invalidates older copies, whose `Current` and `MoveNext` throw `InvalidOperationException` (slice 1; see [API decisions](../../api-decisions.md)) | |
| Changed | `RootBlockStatus.Generation`, `LiveRowCount`, `IsCapacityExhausted`, `SkippedRecordCount`: facts of the shared volume block, identical across views of one volume | |
| Deleted | `FileIndexOptions.RetainedFileNames` | |
| Deleted | `IndexedRoot` and its members `FromWindowsDrive`, `FromDirectory`, `EnumerateWindowsVolumes`, `Producer`, `Root`, `RootDirectory`, `VolumeSerial` | |
| Deleted | `RootFailureKind.MftSourceMissing` (now `MftUnavailable` with `NoMftSource`) | |
| Deleted (test package) | `InProcessBrokerScan.RetainedFileNames`, `SyntheticIndexedRoot` | |
| Added (test package) | `SyntheticVolumeFacts.Apply(FileIndexOptions, uint volumeSerial, bool isNtfs = true, bool isLocal = true, VolumeKey? volume = null, string? volumeRootDirectory = null)`, forwarding to an internal volume-facts seam so tests choose the serial and eligibility a synthetic root reports; with both optional parameters set, synthetic volume `volume` is rooted at host directory `volumeRootDirectory` (a volume key for it resolves there, and a directory key under that host path belongs to it), and unset the facts describe the real volume of `options.Root` | MFTLib.Tests; git-wizard and file-wizard tests replacing `SyntheticIndexedRoot.Create` |

Internal deletions that exist only for retention: `MftRowSelection` (its `IncludeFreed` moves onto the scan
request), `FileIndexOptions.ValidateRetainedFileNames`, `MftIndexSource`'s `validateRetainedFileNames`
callback, `BrokerMftBlockProducer.ValidateRetainedFileNames`, the retained-name fields of
`MftBlockProduceRequest`, `EnumerationProducerOptions`, `BlockScanTarget`, `BrokerProcess.ScanOnChannelAsync`,
`BrokerFrame` and the `ArmAndScan` payload (its nullable name count goes; the frame is section name plus
the include-freed flag), the host hook argument in `JournalBrokerHost.Scan.cs:103`, the filter in
`MftBlockRowWriter` and `EnumerationDirectoryReader`, and `ScanShape.WriteRetention`.

### Producer selection rules

Validation before side effects is unchanged in spirit: a default or undefined `ProducerSelection`, a
default `RootKey` on a non-dump open, and `WalkOnly` with a dump source throw before any cache work. A dump
source accepts Automatic or MftOnly and still requires null `Root`.

Eligibility is decided once, at open, before cache lookup, from facts that need no elevation: platform
(MFT only on Windows), volume ready, local (fixed or removable), `NTFS` by `OrdinalIgnoreCase`, and a
non-null `MftSource`. Automatic then tries the MFT; it falls back to a walk only for an eligibility reason,
which includes `BrokerUnavailable` and `ElevationDeclined` raised when a cold MFT scan cannot start. A read
error or failure during an MFT scan is `Failed` with `ProducerFailed`, never a walk. MftOnly turns every
eligibility reason into `Failed` with `RootFailureKind.MftUnavailable` and the same `MftUnavailable` value.
WalkOnly never touches `MftSource`, the broker or the volume handle.

The producer is chosen once per `FileIndex` and is fixed for its lifetime: `RescanAsync` reuses it, so a
walked index never silently becomes MFT-backed or back. A consumer that wants to retry the MFT reopens.
Concurrent opens that fall back together share one broker launch through the existing `BrokerSession`
launch sharing, so they see one UAC prompt.

## 2. Volume scan sharing

### Ownership

Today's `RootRuntime` splits in two internal owners:

- **`VolumeScan`**, the shared owner: the `RootBlock`, the canonical owner lock, production (cold scan,
  warm adoption, rescan, recovery), the journal watch instance and pump, catch-up state, checkpoint-loss
  facts, the write gate and the retired-block release states. It is everything `RootRuntime` owns except
  view facts.
- **`IndexView`**, one per `FileIndex`: its `RootKey`, resolved root row and sequence number, its own borrow
  counter, its disposal token, its watch request flag, its event subscriptions and reentrancy marker
  delivery, and the producer facts it reports.

A process-wide `VolumeScanRegistry` maps a share key to a live `VolumeScan`. The share key is the `MftIndexSource`
instance, the `VolumeKey`, the volume serial, the cache directory (full path, platform comparison) or
`Disabled`, and the `CacheTag`. `IncludeFreed` comes from the source, so the source instance covers it.
Different keys on one volume form separate scans; the second contends for the owner lock exactly as a
second process does today (private fallback, or `InUse` for a cache-only open). Walked indexes never
share; each walk block holds only its own path.

### Lifetime

| Event | Behavior |
| --- | --- |
| Open, no scan for the key | Create the `VolumeScan` under the registry lock, attach the view, run warm adoption or a cold scan |
| Open, scan exists | Attach the view; await the scan's in-flight production if any; no second scan. Concurrent opens are the batch open, so no batch API is added |
| Open, block current and watch Live | Attach; resolve the root row under the volume write gate so no mutation interleaves; ready immediately |
| Cache-only open (`ExistingOnly`) on a scan that needs a cold scan | The view fails `CacheDeclined` without waiting; the scan continues for writing views |
| Caller cancels an open | Only that view detaches; production continues while any view remains, and is cancelled by the last detach |
| View `DisposeAsync` | Cancel the view's linked queries and waits, drain the view's own borrows, withdraw its watch request, detach |
| Last view detaches | The registry removes the key; the scan stops its watch, cancels production, drains every block borrow and writer scope, unmaps, releases the owner lock |

### Roots, queries and anchors

A view's root row is resolved from the volume root (row 5) by path at attach. The view caches the row and
its sequence number; each query admission rechecks them by sequence and by climbing the row's parents to
compare with the root key (cost: path depth). A mismatch re-resolves; a directory that no longer exists
makes the view `Offline` with no block, and it returns to `Ready` when the directory reappears. A missing
directory at open settles `Offline` as today.

- `Find` accepts only paths under the view root and descends from the view's root row.
- An unscoped `Search`, `Enumerate` or `EnumerateRows` on a directory view applies the view root as an
  implicit subtree. Each query keeps a two-bitset verdict cache (known, inside) over directory rows, so each
  directory's ancestry is climbed once per query and a full scan stays linear in rows.
- `Root(...)` returns the view's root row. `FileEntry.Parent` is null at that row. `Children` is unchanged.
  Paths are built from the volume root, so they are the real full paths.
- A `FileEntry` carries its view. Equality compares view, root block, row and sequence; `FileEntry.Root`
  is the view's key. The anchor ruling becomes: an anchor is valid when its block is the borrowed block and
  its view is the querying index's view; an anchor from another view of the same volume is rejected with
  `ArgumentException`, as an anchor from another index is today.
- `FileIndexSet` is unchanged: one member borrow at a time; members of one volume share a block, but each
  member borrows through its own view. Nested roots in one set are open question 3.

### Watch, rescan, checkpoint loss and broker

- **Watch.** `StartWatchingAsync` sets the view's request; the scan starts its one watch when the first
  request arrives and stops it when the last is withdrawn. Readiness (channel connected and `StartWatch`
  written) is the scan's; a second view's start returns at once when it already holds. `RootWatchStatus`
  is the scan's state with the view's own `IsRequested`. `WaitForCatchUpAsync` follows the scan's current
  or pending watch instance, as today per root.
- **Events.** The scan's pump applies each batch once under the write gate, then routes each change to every
  view whose scope contains the row before or after the change, with the kind unchanged (a move across a
  view boundary reaches both views as `Renamed`). Subscriber faults stay on the view whose handler threw.
  `Volume`, `Apply`, `Channel`, `Recovery`, `RescanRestart` and `CatchUpLost` faults reach every view with a
  watch request. The process-wide reentrancy rule is unchanged.
- **Rescan.** `RescanAsync` on any view rescans the volume; concurrent rescans of one scan coalesce into one
  production and every caller receives its result. Healthy-watch survival until replacement commit,
  identity-checked publication and failed-production preservation apply to the scan unchanged. Automatic
  recovery is one rescan for the scan, not one per view.
- **Checkpoint loss.** Detection, the three-loss refusal and `IsRecoveryStopped` are scan facts reported on
  every view; labels and preservation rules in [checkpoint loss](../../checkpoint-loss.md) hold per scan.
- **Broker.** One scan channel per production and one watch channel per watch instance, both owned by the
  scan: "one channel per volume operation" still holds and the protocol changes only by losing the
  retained names. Session, heartbeat and parse-budget rules are untouched.
- **Block ownership.** One owner lock per scan; validation, sweep, rename and delete stay under it.
  `CacheLocation` is the scan's backing, read by every view.

## 3. Cache

- **File name.** Every MFT block is `<letter>-<serial>.mlix`, whatever the view's root. Directory-shaped
  `D<hash>-<serial>.mlix` names are walk blocks only. Cross-process sharing is out of scope: a second
  process takes the private fallback as today.
- **Scan shape.** MFT: producer kind (1) and `IncludeFreed`. Enumeration: producer kind (2), the pruning
  rules and the pruned paths within the root. Retention leaves both encodings.
- **`CacheTag`.** Unchanged in meaning, part of the share key, compared after structural validation.
  Consumers bump their tag versions in their pin bumps (git-wizard `GWMI` 3, Watch `SMPW` 3, file-wizard
  `FILE` 2) only to make their own intent visible; the format bump already discards every old block.
- **Format version 8.** Version 7 blocks fail the version check, are deleted under the owner lock and the
  root cold-scans; a cache-only open reports them as today. No migration.
- `CacheDirectory.InspectCached` and `DeleteCached` list MFT blocks under their `VolumeKey`; a directory
  `RootKey` selects only a walk block.

## 4. Name encoding (format 8)

### Layout carried over from the spike

Header (160 declared bytes, root key text after it), rows (32 bytes, name id at offset 8), sequence region,
name-entry region, bucket directory and name byte pool, each region 4096-aligned, as in the spike's
`SPIKE-REPORT.md` tables. Entry zero is reserved; a row's 4-byte name id replaces offset and length; a
rename publishes the new id and flags in one aligned 64-bit store. The 12-byte entry stays exactly as
the spike has it: `PoolOffsetBytes` (4), `LengthAndEncoding` (15-bit length, high bit UTF-16), `HashTag`
(upper 16 hash bits), `Next` (4). ASCII names are one byte per character; any non-ASCII character makes the
whole name UTF-16LE at an even offset. Publication order is pool bytes, entry, counts, bucket head last
(volatile). No entropy coding and no stem or extension split (ruling 11; the consult found 4 to 8 percent).

One change to the header: entry capacity (offset 128) becomes an independent capacity `E`, initialized to
`S`, and the bucket count derives from `E`. This gives entries their own growth path (below) without
changing the initial reservation the majority chose.

### Corrections to the spike

| Area | Spike | Design |
| --- | --- | --- |
| Identity | case-insensitive, first spelling wins (`NamePool.cs:79`) | exact ordinal UTF-16. The folded hash is unchanged, so every casing shares one chain and one tag |
| Rename | case-only rename skipped (`BlockWriter.cs:146-151`) | compared ordinally; a case-only rename interns the new spelling and swaps the id |
| Case-sensitive exact | one id, then reject on mismatch | walk the chain, take the single ordinal match |
| Case-insensitive exact (`NameQueryMatches`, `LookupEngine.TryFindChild`) | one id | walk the one chain, collect every ignore-case match (inline set of four, then a rented array); rows test membership. `Find` under ignore-case prefers the child whose name matches ordinally, else the lowest row |
| Case folding | `ToUpperInvariant` in `NameHash`, `OrdinalIgnoreCase` and `Ascii.EqualsIgnoreCase` in `NameView` | one `NameFolding` routine (invariant simple uppercase, per UTF-16 unit, `Rune` for pairs) used by the hash and by every ignore-case comparison of non-ASCII text, including across widths. The ASCII fast path uses `System.Text.Ascii`, which equals the routine on ASCII; an exhaustive test pins that |
| Substring (`NameMatchMode.Substring`) | scalar per-position loop for ASCII (`NameView.cs:60-76`) | split by `CaseSensitive`. When `CaseSensitive` is true, retain the original unfolded pattern: if the pattern is ASCII, ASCII entries use ordinal `ReadOnlySpan<byte>.IndexOf` on the original pattern bytes; if the pattern contains non-ASCII characters, ASCII entries are skipped in constant time; UTF-16 entries use `MemoryExtensions.IndexOf` with `StringComparison.Ordinal`. When `CaseSensitive` is false, fold and encode the pattern once per query with `NameFolding` (folding first matters because `ſ` folds to ASCII `S`; .NET invariant casing leaves `ı` unchanged): if the folded pattern is ASCII, ASCII entries use `IndexOfAny` on both ASCII cases of the first byte followed by `Ascii.EqualsIgnoreCase`; if it is not ASCII, ASCII entries are skipped in constant time; UTF-16 entries scan against the folded pattern with `NameFolding` |
| Glob (`NameMatchMode.Glob`) | scalar wildcard loop (`NameView.cs:78-127`) | keep iterative `*` and `?` wildcard matching (optionally vectorizing literal runs between wildcards with the substring `IndexOf` / `IndexOfAny` recipe above), split by `CaseSensitive`. When `CaseSensitive` is true, retain original pattern bytes/units for ordinal literal comparison and skip ASCII entries in constant time when any literal is non-ASCII. When `CaseSensitive` is false, fold only the pattern's literal runs once per query with `NameFolding` while keeping `*` and `?` as wildcards; ASCII entries use `Ascii` ignore-case literal comparison (skipping in constant time when any folded literal is non-ASCII) and UTF-16 entries compare folded units with `NameFolding` |
| Pool reservation | `ComputeNamePoolCapacity(48 * S)`, about 60 bytes per slot | `P = pageAlign(max(1 MiB, 24 * S))` with `S` the headroomed slot capacity and no second headroom. One `BlockLayout` routine serves both producers; `EnumerationProducer.EstimatedNameBytesPerRow` is deleted |
| Bucket count | power of two at or above `S / 2`, minimum 1024 | power of two at or above `max(1024, ceil(E / 4))` |
| MFT rescan growth | only enumeration doubles exhausted capacities (`FileIndex.RootRuntime.Production.cs:94-110`) | `PlanCapacities` serves both producers: each region whose use reached its capacity (rows at `S`, entries at `E`, pool within 510 bytes of `P`) doubles, the rest keep the volume plan; the plan flows to the client-side capacity planning in `BrokerProcess.Scan.cs` and `MftBlockProducerPipeline.cs` as a minimum |
| Open-time validation | `ValidateNameDescriptors` checks every entry, row and chain and rehashes every name (`BlockFile.cs:357`, `NamePool.cs:191-232`) | split below |

Entries are never reclaimed, so a long rename-heavy watch can reach `E`; that sets the capacity-exhausted
flag and the rescan grows `E` alone. Enumeration's mid-walk growth (`EnumerationBlockGrowth.Grow`) is kept,
doubling only exhausted regions and rebuilding buckets for the new `E` with ids preserved.

### Validation split and ruling 9

| Stage | Checks |
| --- | --- |
| Eager at open, constant cost | header magic and version, every region offset equal to the layout for the stored capacities, total size, counts within capacities (rows, entries, pool), bucket count equal to the formula for `E`, root key, file name, producer, cache tag, scan shape. The broker's section validation is the same set on a private header copy |
| Lazy at each access, read safety | `NamePool.Read`: id within the published entry count, length nonzero and within the maximum, offset plus bytes within pool used, UTF-16 even offset; chain walks: id bounds and strictly decreasing `Next` (termination). A failed read throws `InvalidDataException` to the query and writes a `Diagnostics` line; a failed chain step ends the walk as not found |
| Not run in production | tag correctness, bucket membership, reachability, ASCII validity, per-name rehash. A corrupt chain can only cause a missed exact match or a duplicate entry, never an out-of-bounds read. Tests run the full pass through an internal integrity checker reached from `MFTLibTestExtensions` |

Nothing is rebuilt or rehashed at open, which is ruling 9. The writer trusts the chains it finds; a block
written by MFTLib under the owner-only cache directory is the only input, and the next rescan rewrites it.

### Expected sizes

Under the final formulas (`S` recovered from the spike's measured files):

| Volume | Today (filtered) | Spike (all names) | Design (all names) |
| --- | ---: | ---: | ---: |
| C: | 1,760 MB | 2,052 MB | 1,345 MB |
| D: | 112 MB | 131 MB | 86 MB |
| E: | 87 MB | 100 MB | 66 MB |
| F: | 401 MB | 469 MB | 307 MB |
| G: | 950 MB | 1,105 MB | 724 MB |
| Total | 3,310 MB | 3,856 MB | 2,527 MB |

The pool reserve is 24 bytes per slot against a measured 2.7 to 15.2 bytes per slot (1.6 times headroom on
E:, the most distinct volume). Exact spelling adds one entry per extra casing; the consult estimates 1 to 3
percent of distinct names, and the re-measurement records the real count.

## 5. Walk producer

A walked index stores only its directory tree, rooted at row 0, in a directory-shaped block, and is never
shared. It uses the same format 8 writer: dedup, dual width, the same reservation routine and growth. Its
ignore-case policy stays the root comparison (ignore case on Windows, ordinal elsewhere) and uses the same
`NameFolding` routine. Pruning rules are unchanged; a pruned `.git` is still written as a row, so
git-wizard's marker query keeps working. Live watch stays MFT-only for 0.3.0.

## 6. Consumer migrations

**git-wizard** (main already on 0.3.0):

- Delete `GitWizard/Discovery/WindowsRootClassifier.cs` and its wiring in `RepositoryDiscoveryDependencies.cs:31`; close git-wizard issue 442.
- `DiscoveryScope.cs`: one search-root list; `RequestedNtfsDrives`, `RecursiveRoots` and the Unknown error collapse; `--no-mft` sets `ProducerSelection.WalkOnly` on every root.
- `MftIndexSession.cs` and `MftIndexSession.Windows.cs`: open one `FileIndex` per search root by `RootKey`; delete `RetainedFileNames` (L161), the `acquireIdentity` seam and `_identityFailures` (failures arrive in `RootStatus`).
- `EnumerationDiscovery.cs`: `Root = RootKey`, `Producer = WalkOnly`; delete L100. The walk path and the MFT path become one open path whose producer the status reports.
- `RepositoryDiscoveryCoordinator.cs` (L42, L63-65), `.Watch.cs:67`, `Watch/IndexVolumeChangeSource.*`: drop the drive-set gate; journals map from `RootStatus.Root` and `Producer`.
- `GitWizardApi.IndexDiscovery.cs`: per-root clipping (`BuildCandidateScopes`, `TranslateToRealRoot`) goes, since each index is already its root's view.
- `MftIndexCachePolicy.cs`: delete `DirectoryScanFileNames`; tag `GWMI` 3. `MftIndexCacheInspection.cs`, `MftIndexCacheStatusFormatter.cs` (the "Retention:" line), `MftIndexCacheSnapshot.cs`: drop retention.
- The `blocks-all-names` cache folder exists only on the local `spike/retain-ab` branch; main keeps the one `blocks` folder.
- Tests: `EnumerationDiscoveryTests.cs:41-44`, `CliDiscoveryRoutingTests.cs:81` (asserts WalkOnly instead of "MFT not opened"), every `SyntheticIndexedRoot.Create` caller.

**file-wizard** (on `lane/mftlib-030-pin-bump`, PR 766, or main once merged):

- `FileWizard/FileIndexHost.cs`: always create the `MftSource` on Windows (the broker launches only when a scan needs it); delete the `Producer` inspection at L48 and L62 and `RootFor` (L186-190) with its `with { Producer }`.
- `FileWizard/IndexSession.cs:37`: the elevation need comes from `RootStatus.Producer`, not the requested producer.
- `FileWizard/DriveDiscovery.cs`: the indexability gate goes. `IsWalkCandidate` and `includeNonNtfs` go; every ready local drive is offered and Automatic decides; the card text reads `RootStatus.MftUnavailable`. `WindowsVolume.EnumerateAll()` feeds the list.
- `FileWizardMaui.Logic/ScannerState.cs:28-46`: `UsesDirectoryWalk` maps to `WalkOnly`, otherwise Automatic; `DirectoryWalkRoot.cs` reduces to a `RootKey`.
- `FileIndexHostOptions.cs:11`, `ScanOperation.HostReplacement.cs:66`, `IndexHostOpening.cs:8`, `RecentChangesBenchmarks.cs:39-43`: `IndexedRoot` becomes `RootKey` plus `ProducerSelection`. `ContentHashSidecar.cs` keeps reading `ProducerKind`.

**Watch sample:** delete `--keep-name` and `--directories-only` (`WatchArguments.cs:21,31-34,39`,
`WatchArguments.Parse.cs:21,27,63-64`) and `SampleHost.Broker.cs:167`; add `--producer automatic|mft|walk`;
add an `add-root <path>` console command in the `watch` verb that opens and attaches a view while Live;
roots parse as `RootKey`; print `Producer` and `MftUnavailable`; tag `SMPW` 3.
**Direct sample:** `SampleProgram.Shared/SampleHost.Drive.cs:14` builds a `RootKey`; `SampleHost.Run.cs:94-111` prints `Status.Producer`.
**Docs:** `docs/architecture.md:59,134,236-238`, `docs/index-format.md`, `docs/broker-lifetime.md:129`,
`docs/broker-integration.md`, `docs/broker-testing.md`, `README.md:41`, `CHANGELOG.md`.

## 7. Contracts and rulings

| Document | Effect |
| --- | --- |
| [architecture](../../architecture.md) | `RootRuntime` splits into `VolumeScan` and `IndexView`; ABI, stride and native string copying unchanged |
| [index format](../../index-format.md) | format 8 layout, reservation, growth and validation split; retention text deleted |
| [query lifetime](../../query-lifetime.md) | borrows counted per view and per block; cancellation cadence also every 4096 name entries in the entry pass |
| [watch lifetime](../../watch-lifetime.md) | one watch per scan, requests per view, fault fan-out rules above |
| [block ownership](../../block-ownership.md) | one lock per scan; unchanged otherwise |
| [broker lifetime](../../broker-lifetime.md) | `ArmAndScan` loses the retained-name count |
| [test isolation](../../test-isolation.md) | sharing tests inject private registries; explicit Process references require class-level isolation and cleanup checks that their keys are no longer live; live scans are never reset; journal isolation unchanged |
| [API decisions](../../api-decisions.md) | superseded: "One producer enum, selection and observation", "The enumeration producer is a product feature, chosen per root" (its no-fallback rule becomes the eligibility-only fallback), "Retention is an index option applied by every producer", "Retained names and pruning share the root comparison" (retained-name half), "Windows volume discovery" (moves to `WindowsVolume.EnumerateAll`), "Block Generation is a per-root change version" (per volume block), the anchor ruling (per view). New: producer selection, volume scan sharing, exact-spelling name identity |

## 8. Verification

- **Original-spelling oracle** replacing `NameDedupSearchTests.cs:22`: the expected name of each row is its
  input spelling. Cases: case variants across directories, case-sensitive siblings (`Makefile`, `makefile`),
  case-only rename, peers sharing the old name after a rename, names whose fold is ASCII (`ſ`) and `ı`, which does not fold,
  UTF-16 and ASCII widths; every query mode (exact and substring and glob, both case settings, `Find`,
  `Children`), pinning case-sensitive substring (`readme` matches `readme.txt` and rejects `README.txt` and
  `other.txt`) and glob wildcard matching (`*.cs` matches `Program.cs` rather than searching for a literal
  `*.cs`, rejects `Program.txt` and `Program.cs.bak`, and under `CaseSensitive = true` rejects `Program.CS`);
  reopen and growth round trips preserving ids and spelling.
- **Fold routine:** exhaustive over ASCII against `Ascii`; every BMP unit and supplementary scalar against
  the hash fold.
- **Validation:** corrupt-block cases (out-of-range id, odd UTF-16 offset, pool overrun, cyclic `Next`, wrong
  tag) open, then fail safely on access; an open of a large synthetic block touches no entry or pool page
  (asserted through an internal access counter, not timing).
- **Reservation and growth:** formulas pinned in `BlockLayoutTests`; MFT rescan after entry, pool and row
  exhaustion grows only the exhausted region.
- **Producer selection:** each `MftUnavailableReason` through Automatic (walk) and MftOnly (Failed);
  WalkOnly touches no source (a recording source); mid-scan failure stays `Failed`; producer fixed across
  rescan; dump with WalkOnly rejected.
- **Sharing:** two views one scan (one production counted); concurrent opens; attach during Live; cancel one
  open of two; dispose order both ways; last detach unmaps after borrows drain; event routing in, out and
  across view boundaries; fault fan-out; coalesced rescans; checkpoint loss on both views; view going
  `Offline` and back; foreign-view anchors rejected; `ExistingOnly` view on a cold scan.
- **Existing tests that change:** `FileIndexRetainedFileNamesAdmissionTests` and `BrokerProtocolTests.Retention`
  are deleted; `MftBlockRowWriterTests`, `EnumerationProducerTests`, `FileIndexScanShapeTests`,
  `BrokerFrameLengthTests`, `BrokerProtocolTests` (frames, scan, malformed payloads), `JournalBrokerHostScanTests`,
  `JournalBrokerHostChannelTests`, `MftProducerEndToEndTests`, `WatchArgumentsTests` drop retention;
  `FileIndexProducerSelectionTests` is rewritten for `ProducerSelection`; the spike's layout tests move to
  the new formulas; public-surface approvals and `PublicMemberCallerTests` follow the table in section 1;
  `NativeSeamIsolationTests` and the Linux run cover the registry.
- **A/B re-measurement** with the git-wizard harness (`measure-retention-ab.ps1`, three iterations, C: to
  G:): today against the final build after slice 1 and again after slice 5. Record block bytes (expected
  about 2.53 GB), cold and warm wall, peak working set and private bytes, and the ordinal-distinct against
  ignore-case-distinct name count on C:. Expectation: warm client peak working set at or below today's
  1,359 MB once eager validation is gone; a miss goes to the owner with the profile before slice 5 merges.

## 9. Implementation slicing

Each slice is one pull request, green on `test / windows`, `test / linux`, `test / linux-package` and
`aislop / quality-gate`, with its `api-decisions.md` rulings and public-surface approvals in the same PR.

1. **Format 8 name encoding.** Port the spike's block layer (`NamePool`, `NameEntry`, `NameHash`, `NameView`,
   `BlockFile.Names`, `BlockRowSnapshot`, `RowEnumerationResources`, the two-pass `NameQueryMatches`, the
   `index-storage` benchmark verb) with every correction in section 4, the oracle and the validation tests.
   No public change.
2. **Delete retention.** `RetainedFileNames`, the broker field, the filters, the shape bits, the Watch flags.
   Runs in parallel with slice 1 (they meet in `ScanShape` and `MftBlockRowWriter`; the second to merge rebases).
3. **Capacity growth for MFT rescans and independent entry capacity.** After slice 1.
4. **Producer selection and directory MFT views without sharing.** `ProducerSelection`, `RootKey` options,
   `IndexedRoot` deletion, eligibility, status fields, the view root row, implicit subtree, `Parent` rule;
   a second same-volume index uses the private fallback until slice 5. Samples' `--producer`. After slice 2;
   parallel with slices 1 and 3.
5. **Volume scan sharing.** `VolumeScanRegistry`, `VolumeScan` and `IndexView`, watch requests, routing,
   coalesced rescans, Watch `add-root`. After slices 3 and 4.
6. **A/B re-measurement and docs close-out.** After slice 5, then the git-wizard and file-wizard pin-bump
   PRs, which can run in parallel.

## Owner answers (2026-10-27): each recommendation below is accepted

1. **Delete `IndexedRoot` for `RootKey` plus `FileIndexOptions.Producer`?** Recommended: yes. Its two
   remaining jobs (producer choice, derived serial) are now the library's, and keeping it would leave a
   record whose only field a consumer sets is the key.
2. **Removable NTFS volumes: MFT-eligible?** Recommended: yes, and `WindowsVolume.IsIndexable` uses the
   same predicate, so the library and Automatic agree and git-wizard's existing behavior is kept.
3. **Nested roots in one `FileIndexSet`** (`D:\` beside `D:\src`): unscoped set queries would return each
   file once per containing member. Owner ruling (2026-10-28, replacing the recommendation to reject): the
   constructor accepts nested members, and unscoped set queries and `Find` read only the outermost available
   members (those under no other available member), using the matcher `Find` uses, so each file is returned once and
   belongs to the outermost available member containing it; when the outer member is unavailable the inner one
   covers its own subtree. Owner ruling 16 (2026-10-28): a directory root's final path (links followed) is resolved
   once at open and used for the volume facts, the root directory, containment and the set's duplicate check, so two
   aliases of one folder are duplicates and an alias under another member is nested; a failed resolution falls back
   to the path as named with one diagnostic, and a directory missing at open stays Offline.
