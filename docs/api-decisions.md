# Public API decisions

Owner rulings on the shape of the public surface, each with the reason it was made. Read this page before
proposing to remove, move or reshape a public member. A review that finds "MFTLib never calls this itself" or
"this is not an MFT primitive" has found a fact, not a reason: check the ruling here first, and reopen one only
with evidence that its stated reason no longer holds (for example, the consumers' needs have diverged).

Record every new ruling here in the same change that implements or files it: the decision, the reason, and
what would justify revisiting it.

## Release benchmark baselines are committed reports

**Decision (owner ruling 2026-10-26, B13):** Keep one small, hand-captured
release report in `Benchmark/baselines/<date>-<short sha>.txt`, measured on a
quiet machine and committed beside the code as input to `Benchmark.exe compare`.
These reports are the deliberate exception to the no-measurement-dumps rule;
logs and raw captures remain excluded. Keep baselines for scenarios the current
runner emits. The retained references are `2026-10-16-abaf975d.txt` for index
queries and `2026-10-20-36f07c79.txt` for enumeration walks.

**Reason:** Benchmark numbers need a quiet machine, so release baselines are
captured by hand rather than CI. CI artifacts expire, and CI runners share load,
making their timing numbers noisy. A small text file beside the code is the
easiest input for `compare`.

**Revisit only if:** an explicitly approved replacement provides durable,
quiet-machine measurements with equally straightforward comparison inputs.

## Windows native releases target plain x64

**Decision (2026-10-16):** the Windows Release native library targets plain x64
(SSE2 baseline). AVX and AVX2 are not required. The Release compiler uses its
default x64 instruction set. Release validation inspects the DLL extracted from
the package, permits reviewed CPU-dispatched static-runtime routines, and rejects
advanced instructions elsewhere. A separately rebuilt AVX2 package is a required
negative control for that inspection.

**Reason:** six alternating runs per variant on one 8M-record synthetic dump
showed AVX2 1.3 percent faster for bounded parsing and 3.0 percent slower for
broker streaming. Both differences were within the reported run-to-run spread;
the measurements included managed materialization rather than isolating native
parse time. An unconditional AVX2 build can fault on AVX-less x64 CPUs, including
Intel Jasper Lake. That CPU restriction has no demonstrated benefit here.

**Revisit only if:** new representative measurements justify an explicitly
approved baseline change, or separately dispatched optimized paths retain the
plain-x64 baseline and demonstrate a repeatable improvement.

## Metadata reports observations and freshness without hidden reads

**Decision:**
the index reports the filesystem metadata it actually observed and its freshness, without implicit filesystem
reads to refresh it. A journal event's timestamp is event time (`FileChange.Timestamp`), not a filesystem
last-write observation. `RowFlags.LastWriteTimeUnknown = 32` tracks unknown last-write time alongside
`RowFlags.SizeUnknown = 8`. Both `FileEntry` and `IndexRow` expose `IsLastWriteTimeKnown`, and `IndexRow`
exposes `LastWriteTime` and `Attributes` for full row-column parity with `FileEntry`. Rows with
`LastWriteTimeUnknown` read `LastWriteTime == DateTime.MinValue` and are excluded when `SearchQuery` specifies
`LastWriteTimeMinimum` or `LastWriteTimeMaximum`, matching `SizeUnknown`.

The journal applies these freshness rules, all through `BlockWriter` under the block's access scope with the
descriptor word written atomically. A create or hydration of an unseen row, and a content modification
(`DataOverwrite`, `DataExtend`, `DataTruncation`), zero the size and last-write columns and set both unknown
flags. `BasicInfoChange` zeroes the last-write column, sets
`LastWriteTimeUnknown` and keeps the size with its flag. Rename, move, close and every other reason update the
parent, name and attributes only. The enumeration producer stamps root row 0 with the root directory's real
attributes and last-write time, or sets `LastWriteTimeUnknown` when the directory cannot be read; it never
reads the clock. A rescan writes the producer's observations and clears an unknown flag only
for a row and field whose producer obtains a known value. An MFT record with
no usable size remains size-unknown, and an enumeration root whose metadata
cannot be read remains last-write-time-unknown.

**Reason:** the owner wants fast read-only queries and explicitly chose honest freshness over hidden reads, plus
full `IndexRow` column parity so row loops never need `ToEntry()` just to inspect `LastWriteTime`,
`IsLastWriteTimeKnown`, or `Attributes`. Replacing a scanned last-write value with a rename's
later event time would incorrectly match a recent-write query. Journal-created entries also lack
metadata the journal does not carry, and journal modifications (`DataOverwrite`, `DataExtend`, `DataTruncation`,
`BasicInfoChange`) invalidate previously scanned metadata until the next scan.

**Revisit only if:** consumers explicitly request a separately identified metadata-refresh operation or need
freshness information that the observation/invalidation model cannot express.

## A subtree anchor belongs to the queried index's current root block

**Decision:**
all three query forms (`Search`, `Enumerate`, and `EnumerateRows`) reject `Subtree = default(FileEntry)` with
`ArgumentException` on call entry. They then validate a non-null `Subtree` anchor, through one shared helper,
against the snapshot actually borrowed by the query. They throw `ObjectDisposedException` when the anchor's block
has been released, and `ArgumentException` when the anchor belongs to another `FileIndex`, belongs to a replaced
root block, or has been superseded by row reuse. A retained handle remains readable against its original block
until disposed, and rescanning another root does not invalidate an anchor whose own root block remains current;
the root block is compared, not the whole snapshot. Callers reacquire an anchor from the current index for a new
query after the anchor's root is rescanned.

**Reason:** private root ordinals and row numbers are coordinates, not ownership, and `default(FileEntry)` is
not a valid subtree anchor. The audit reproduced a C-root volume selecting D-root rows in another index,
including after the first index was disposed, as well as an old directory handle selecting a different directory
at the same row after rescan and `default(FileEntry)` silently returning an empty result. Comparing whole
snapshot identity would overcorrect when another root rescans while the anchor's root block stays current.

**Revisit only if:** consumers need an explicitly specified rebinding operation that verifies directory identity
across block replacement.

## One producer enum, selection and observation

**Decision:** `IndexedRoot.Producer` selects a `ProducerKind` per root for initial scans and
rescans, defaulting to `ProducerKind.Mft`. The same enum identifies the producer in `IndexRecordKey.ProducerKind`,
`CachedBlockStatus.ProducerKind` and the block header. Its underlying type is `uint`, with `Mft = 1` and
`Enumeration = 2`. Construction, init setters and `FileIndex.OpenAsync` admission reject the default and every
undefined value before filesystem side effects. Warm adoption compares the selected value directly with the
stored value; a mismatch cold-scans, or reports `CacheDeclined` for `CacheMode.ExistingOnly` and leaves the block in place.

**Reason:** selection and observation describe the same producer. One enum gives consumers one vocabulary and
lets a cache inspection value select a root's producer directly, while preserving block bytes and per-root
choice. An MFT failure never selects a directory walk; enumeration ignores `MftSource` and runs only by request.

**Revisit only if:** producer selection needs a choice that cannot identify a block's actual producer.

## OpenAsync validates root keys and producer choices before filesystem side effects

**Decision:**
`IndexedRoot` validates `Root`, `RootDirectory` (`ArgumentException.ThrowIfNullOrWhiteSpace`), and `Producer`
(`Enum.IsDefined`, throwing `ArgumentOutOfRangeException`) on construction and in its `init` setters.
`FileIndex.OpenAsync` validates `FileIndexOptions.Root` up front, before creating
`CacheDirectory` or running any producer: a null `Root` on a non-dump open throws `ArgumentException`, and an undefined `ProducerKind` value throws
`ArgumentOutOfRangeException`.

**Reason:** blank roots, missing roots, and undefined enum values are caller configuration bugs that must fail fast with no filesystem
side effects.

**Revisit only if:** never; configuration validation before side effects is a baseline contract.

## Cold production failures settle per root across both producers

**Decision:**
a cold-production failure on a `ProducerKind.Enumeration` root settles that root as `RootState.Failed` with
`RootFailureKind.ProducerFailed`, matching `ProducerKind.Mft` and
`RescanAsync`. A failed rescan preserves the previous block and reports `RootStatus.Failure` with
`RootFailureKind.ProducerFailed`. Argument validation performed at admission (null options, null `Root` on a non-dump open, an undefined
`CacheMode`, a missing cache directory, dump-source option conflicts) throws before any root is produced. A root
whose production cannot start or fails for an operational reason settles as `RootState.Failed` with
`RootFailureKind.ProducerFailed`. An MFT root that needs a scan when
`FileIndexOptions.MftSource` is null settles as `RootState.Failed` with `RootFailureKind.MftSourceMissing` instead. Caller cancellation (`OperationCanceledException`) aborts the operation, and a
cancelled `OpenAsync` releases its adopted block.

**Reason:** `IndexedRoot.Producer` is configured per root so an application can open indexes over MFT-scanned and directory-walked
roots alike. An operational production failure must have the same status reporting regardless of the selected
producer, both during initial open and during rescan. Missing roots settle Offline, and enumeration skips
missing or inaccessible subtrees; neither is an operational producer failure by itself.

**Revisit only if:** a consumer explicitly asks for a throwing open mode on operational producer failures.

## A retained FileEntry keeps only its own root block mapped

**Decision:** A `FileEntry` holds its `RootBlock` directly. A held handle keeps that one block mapped and no
other root's: rescanning another root releases that root's retired block once nothing else reaches it. The
16-byte layout holds (`RootBlock?`, `uint` row index, `ushort` sequence number); the root ordinal comes from
the block. Queries borrow every current block they read, and index disposal drains those borrows and releases
every current and retired block regardless of held handles.

**Reason:** Owner ruling (MFTLib#584): rooting the whole multi-root snapshot from one handle was a residue of
the retired root-bundle design, and the per-root lifetime deleted the snapshot-wide release state and the
per-block reference count rather than adding a lease layer.

**Revisit only if:** a consumer needs a handle to pin a consistent multi-root view.

## FileEntry handles are bound to the row's sequence number

**Decision:**
`FileEntry` captures the row's `ushort` sequence number at construction, in its 16-byte struct
(`RootBlock?`, `uint` row index, `ushort` sequence number, padded; `BlockFile` layout is
unchanged, and an enumeration block, which the journal never mutates, captures zero). `BlockWriter.TryWriteRow`
stores the supplied NTFS sequence number exactly, publishing it before it overwrites the columns. A handle is
bound to the NTFS file reference (record number plus sequence number) it was created from. Distinct references
never alias; a reference NTFS itself reuses (a freed record's incremented sequence handed to the next file) is the
same reference, and the library does not pretend otherwise. The journal ignores a delete or other mutation whose
record names a different sequence number than the live row holds, because it belongs to an earlier incarnation a
scan already superseded. A create that rewrites a live row with the same number is a refresh. When a journal create reuses a deleted record's slot,
previously retained handles do not silently alias the new file: `IsDeleted` reads `true`, `Open` throws
`FileNotFoundException` without an open-by-id call, `Children` returns empty, `Subtree` queries reject the
superseded anchor with `ArgumentException`, and reading overwritten row columns (`Name`, `Path`, `Parent`,
`IsDirectory`, `Size`, `IsSizeKnown`, `LastWriteTime`, `IsLastWriteTimeKnown`, `Attributes`) throws `InvalidOperationException`.
Equality (`Equals`, `GetHashCode`, `==`, `!=`) compares the underlying root block, row index and sequence
number. A rename or move of the same file keeps its sequence number, so its handles stay live. `FileChange.Entry`
of a `Deleted` notification whose slot was later reused reports `IsDeleted`; callers read `FileChange.Path`,
`PreviousPath` and `Timestamp` for the event facts.

**Reason:** NTFS reuses MFT segment numbers when creating new files after deletions. Because `FileEntry` already
has 2 bytes of tail padding in its 16-byte layout and `BlockFile.SequenceNumbers` already stores a `ushort` per
row, binding `FileEntry` to the row's sequence number prevents silent data corruption (opening or querying the
wrong file through a retained handle) at zero struct-size cost.

**Revisit only if:** consumers require cached tombstone snapshots of overwritten columns on superseded handles
beyond `IsDeleted`.

## A sequence number is stored as NTFS supplied it

**Decision:**
MFTLib never invents a sequence number. `BlockWriter` stores the NTFS sequence number exactly as supplied. A
`FileEntry` is bound to the NTFS file reference (record number plus sequence number). Distinct references never
alias, and a reference NTFS itself reuses is the same reference. The journal ignores a delete or mutation whose
sequence number differs from the stored live row. Synthetic test data that reuses a slot supplies an incremented
sequence number the way NTFS does.

**Reason:** NTFS increments the sequence number when it frees a record, so any library-side advancement stores a
number the volume disagrees with, and open-by-file-reference then misses. Three review rounds showed every
advancement heuristic fabricating references under freed-record scans and catch-up replay.

**Revisit only if:** a producer exists that cannot supply the volume's sequence number and a consumer needs
identity from it.

## FileEntry identity is validated per access without a lock

**Decision:**
Identity is validated per access, with no lock and no fence on the read path. Column reads are not serialized
against the journal writer. The writer publishes the new sequence number before it overwrites the columns, so a
read that straddles a slot reuse can return the replacement's column once, and the next access throws. A caller
that needs identity and columns together re-checks `IsDeleted` after the read, or uses the `FileChange` event
fields.

**Reason:** The window is a handful of instructions per read and needs a retained handle to a deleted file whose
record NTFS reused at that instant. The same tear already exists for same-file updates, where size is written
before name, and a sequence re-read would not close it. Closing both would need a per-row generation counter with
fences on the hottest read path, which the owner rejects.

**Revisit only if:** a consumer shows a column read that must be exact without a caller-side `IsDeleted` re-check.

## Elevation helpers live in MFTLib

**Decision (API review finding F9):** `IElevationProvider` and `ElevationUtilities` are public in MFTLib.

**Reason:** both consumers need identical elevation behavior, so the shared implementation belongs here:

- Both decide broker-backed versus in-process scanning with `IsElevated()` and `CanSelfElevate()`, which is a
  decision about MFT scanning.
- Both add Windows Defender process exclusions with the same sequence: already elevated, run directly; able to
  self-elevate, `TryRunElevated`; otherwise an elevated PowerShell.
- The samples self-elevate through the same provider ([elevation](elevation.md)).

The broker launcher does not use `TryRunElevated`, on purpose: that call waits for the elevated child to exit,
and the broker has to keep running on its pipe. That is not evidence the helper is unused.

**Implementation:**
the Defender sequence is `ElevationUtilities.TryAddDefenderProcessExclusions(processNames, relaunchArguments, elevation = null)`,
one method on the existing type; [elevation](elevation.md#windows-defender-process-exclusions) holds its contract.
The public elevation helper and provider remain the shared consumer API.

**Revisit only if:** one consumer needs elevation behavior the other cannot share.

## Sample-caller audit: helper policy and harness-owned observations

**Decision:**
`InProcessBrokerScan` is a receive-only
record with an explicit internal constructor, a public init-only `Volume` property and no
positional deconstruction. Root validation applies on construction and init.
The Direct `open` sample branches on `FileEntry.IsLastWriteTimeKnown` and prints `<unknown>` for
unknown metadata, preserving the metadata-freshness ruling.

**Reason:** both production consumers call `TryAddDefenderProcessExclusions`, which owns its
60-second elevated-child policy. Their remaining timeout references are test expectations.
The broker harness produces scan observations and consumers inspect them; construction belongs
to the harness and MFTLib's friend tests. Printing an unknown timestamp as a date obscures the
freshness decision the public property exists to support.

**Revisit only if:** a concrete consumer production requirement needs the timeout as a shared
public value, or a consumer test needs to supply a scan observation as input to a supported API.

## Journal retention sizing is reported by the library; growth is a consumer decision

**Decision (API review finding F12):**
`BrokerSession.GrowUsnJournalAsync(volume, target, cancellationToken)` takes a `UsnJournalSettings` target.
The library reports the exact journal size that would have retained a lost checkpoint; the consumer owns the
decision to grow. `JournalCheckpointLoss.TryGetGrowthTarget` projects that recorded size and allocation delta,
and `GrowthTargetRefusalReason` is a nullable `JournalGrowthTargetRefusalReason`, non-null only when no target is
available. The reason enum has no success member, following the state-plus-nullable-reason rule below.
`ScriptedBrokerVolumes.GrowUsnJournal` also receives the target settings as one value.

**Reason:** settings already group both sizes, so unpacking them to request growth adds no information.
Consumers must explain refusals without repeating eligibility checks. The reason is derived from the report's
current values, so synthetic reports and `with` copies stay consistent with the projection. Non-trimming losses,
unavailable retention sizes (including overflow), and sizes at or below the recorded maximum can occur in
production. Nonpositive retention sizes and allocation deltas are guards reachable through synthetic reports;
the live checkpoint check rejects nonpositive allocation deltas before producing a report. Refusals prioritize
the cause, absent size, nonpositive size, nonpositive allocation delta, then the recorded maximum. Arithmetic is
unchanged: checkpoint-to-tip span rounded up to the allocation delta, plus one delta. Detection-time sizing
does not decide whether growth is worth it, and a consumer reads fresh settings immediately before requesting it.

**Revisit only if:** the consumers need a different retention calculation or a journal setting not carried by
`UsnJournalSettings`. The proposed reason names are listed in the implementing pull request for owner review.

## Enumeration fans out across directories with one block writer

**Decision (2026-10-20):** Enumeration scans use concurrent directory readers and
one block writer. Every walk holds a base permit and borrows only immediately
available extra permits from the process-wide processor-sized budget. Readers
copy every entry's name and metadata into owned directory buffers, handed off through
a bounded waiting queue. The writer commits parent rows before scheduling their
children. Row order between directories is unspecified. With one available
permit the same pipeline runs sequentially. Parallel traversal ships
unconditionally in 0.3.0.

**Reason:** The [single-core cold walk, issue 676](/schoen/MFTLib/issues/676)
records 104.1 seconds for uncached enumeration-backed discovery versus 13.5 seconds
for git-wizard's parallel walk, a 7.7-times difference exceeding the consumer's
1.5-times bound. Traversal belongs in the producer; git-specific discovery and
pruning policy belong in git-wizard. Walked roots have no journal or live watch,
and rescans replace their blocks, so sequential row order serves no contract.

**Revisit only if:** Representative measurements identify the shared budget or
buffer handoff as the bottleneck, or a concrete consumer requires separately
specified stable ordering. Revisit with evidence and an explicit owner ruling
while preserving bounded work, parent links and worker-drain ownership.

## The enumeration producer is a product feature, chosen per root

**Decision (API review finding F6):** the directory-walk producer stays public. It is how an index
is built where the MFT is not available: Linux, non-NTFS volumes, and a run without elevation. The producer
choice belongs to each volume, not to the whole open, so one index can hold an MFT-scanned volume beside a
directory-walked one.

**Reason:** the review found no shipping caller and recommended moving the producer into the test package. The
absence of a caller was a gap in the consumers, not the intent: file-wizard documents the producer but exposes
no way to select it, and git-wizard
carries a separate hand-rolled walk.

**Rule:** a volume whose MFT scan fails is `Failed`; it is never rebuilt by a directory walk on the
caller's behalf. Enumeration runs only for a root that asks for it.

**Revisit only if:** both consumers decide against indexing anything but elevated NTFS volumes.

## One cache mode, not two booleans

**Decision (API review finding F15, cache half):** `FileIndexOptions.CacheMode` is a nullable enum with
`Persistent`, `ExistingOnly` and `Disabled`. `Persistent` warm-starts
from the cache directory, scans what is missing and writes blocks back. `ExistingOnly` opens only from existing
blocks and never scans. `Disabled` reads and writes no cache directory; its blocks are private and deleted on
close. `CacheDirectory` is required and nonblank for `Persistent` and `ExistingOnly` and ignored for `Disabled`.
Unset resolves to `Disabled` for a dump source and `Persistent` otherwise.

**Reason:** cache policy is one choice. A single value cannot express the contradiction
of disabling the cache while requiring an open to use only cached blocks.

**Revisit only if:** a fourth way to use the cache appears that is not one of the three.

## A dump source describes its own volume and cache

**Decision (API review finding F10):** a dump source is fully self-describing.
The caller supplies `FileIndexOptions.MftSource`; the source supplies its one
virtual volume (letter, root, serial zero, `ProducerKind.Mft`), which
`FileIndex.OpenAsync` configures on the index. `Root`, `CacheMode` and `CacheDirectory` must be null, and `CacheTag` must
be default. Other values throw `ArgumentException` naming the option; every
non-null cache mode, including `Disabled`, is rejected.
`MftIndexSources.FromLocalVolumes`, `MftIndexSources.FromMftDumpFile` and `MftScanOptions.IncludeFreed`
are public, sample-reached features.

**Reason:** the caller had to restate facts the source already knows (the letter, the dump root, serial zero, an
uncached open), and the library threw when any part of the restatement was off. A setting that can only be right in
one way is not a setting.

**Revisit only if:** a dump gains a legitimate cached form, for example a persisted scan of an image.

## Two key types: VolumeKey and RootKey, one method per lifecycle verb

**Decision (owner ruling 2026-10-27, issue 806; API review findings F7 and F8; updated for issue 829):** two structs name what an index
holds. `VolumeKey` identifies an NTFS volume by one ASCII letter: it normalizes case once, converts implicitly from
`char` and never from `string`, and its default value is invalid. `RootKey` identifies any indexed root, either a
`VolumeKey` or a normalized directory path: it converts implicitly from `VolumeKey`, exposes `IsVolume`,
`TryGetVolume`, `FromDirectory`, `TryParse` and `CompareTo` (volumes by letter before directories by ordinal text), and
has no `Letter`. Directory keys compare under the platform case policy and the default `RootKey` is invalid.
Volume-only APIs take `VolumeKey`: `MFTUtilities.GetVolumePath`, `MftVolume` opens, `FromWindowsDrive`,
`FileIndex.QueryUsnJournalSettings`, `BrokerSession.GrowUsnJournalAsync`, `MftIndexSources.FromMftDumpFile`, every
broker frame, and the watch notifications `WatchFault.Volume`, `WatchStateChange.Volume` and `JournalCheckpointLoss.Volume`.
Any-root APIs take or carry `RootKey`: `FileEntry`, `IndexRow`, `IndexRecordKey`, `RootStatus`,
`RootOperationResult`, `IndexScanProgress`, `CachedBlockFile`, `CacheDirectory.DeleteCached`
and `InspectCached`, and `IndexedRoot.Root`. Each lifecycle verb of `FileIndex`
(start watching, stop watching, wait for catch-up, rescan) is one method taking a required `CancellationToken` and returning one
`RootOperationResult`, with no overloads; a root that cannot be watched answers `NotApplicable`.
The library derives the key and serial of a walked folder: `IndexedRoot.FromDirectory(rootDirectory)` returns a root
whose `Root` is `RootKey.FromDirectory(rootDirectory)` and whose `VolumeSerial` is the serial of the volume that
contains the path, or zero when the platform does not report one. The public record constructor for an arbitrary key
and serial does not exist; test code gets one from `MFTLibTestExtensions.SyntheticIndexedRoot`. A directory key can
never reach raw volume access: internal volume boundaries take `VolumeKey` through `TryGetVolume`, and a directory key
with `ProducerKind.Mft` is rejected at configuration with `ArgumentException`.

**Reason:** the letter was the key of the whole API with no owner: case rules, parameter types and collection
types differed from member to member, both consumers up-cased what the library returned, and the four verbs had
an uneven set of single, list and all-roots overloads. A bare `IEnumerable<char>` would accept a `string`
("CD" as two roots), which is why the key has its own type. Consumers read `.Letter` from any-root keys at about 55
production sites; with one type each of those is a latent runtime throw on a walked root, with two the compiler finds
every site. Consumers also invented letters and serials for walked folders; the serial of a walked root is the
containing volume's serial or zero, and the library owns that derivation.

**Deferred on purpose:** whether a key may be a label rather than a letter.

**Implementation note:** the default value of either struct is invalid; `Letter`, `ToString` and public key-accepting
operations reject it with `ArgumentException`. Key-bearing record constructors and `Root` and `Volume` init accessors validate it
too. Members carrying a `RootKey` are named `Root` and members carrying a `VolumeKey` are named `Volume`. A directory key whose normalized text exceeds 1,983 UTF-16 code units is rejected by
`RootKey.FromDirectory`, the limit of the key text stored in each cache block (see [index format](index-format.md)).
These choices keep identity and admission in one owner without adding a string conversion.

**Implementation completion:** keys continue through internal volume operations, broker channels and source delegates,
and test adapters. Variables carrying identity are named `root` or `volume`. The native boundary formats the device path from the
volume key. The compiler, public-surface pins and protocol tests enforce the change. **Revisit only if:** a concrete
producer needs a volume key that the ASCII letter cannot represent.

**Vocabulary sweep, issue 806 PR 2 (completed 2026-10-27, owner rulings mftlib-030-closeout links 6 and 9):** Root names anything the index
holds, Volume names NTFS and Windows volume facts, and Drive survives only where a drive letter is meant. Renamed: `DriveBlockStatus`,
`DriveFailure`, `DriveFailureKind`, `DriveOperationOutcome`, `DriveOperationResult`, `DriveState` and `DriveWatchStatus` became the `Root...`
types; every `RootKey`-typed `Drive` member became `Root` and every `VolumeKey`-typed one `Volume` (`JournalCheckpointLoss`, `WatchFault`,
`WatchStateChange`, `WindowsVolume`, the scripted-watch types); `ScriptedDriveWatch` became `ScriptedVolumeWatch`, `ScriptedBrokerVolumes.ScanDrive`
and `WatchDrive` became `ScanVolume` and `WatchVolume`, `SyntheticIndexedDrive` became `SyntheticIndexedRoot`, and the `SyntheticNotifications`
`CreateDrive...` builders became `CreateRoot...`. Internals follow: `DriveBlock` is `RootBlock`, `DriveRuntime` is `RootRuntime`,
`IIndexDriveWatch` is `IIndexVolumeWatch`, broker channel and scan types are `BrokerVolume...`. `FileIndex.QueryUsnJournalSettings()` takes no
argument and reads the journal of the index's own root volume; a source without live journal settings or a walked folder root refuses with
`InvalidOperationException`. Kept as Drive: `IndexedRoot.FromWindowsDrive`, `WindowsVolume.DriveType`, the `char` and `string` drive-letter
variables and the samples' command-line drive letters. Routine calls made 2026-10-27 (link 9): `IndexedRoot.Root` and `WindowsVolume.Volume` keep
the rule's name despite the repetition; `JournalCheckpointLossDetection.DriveOpening` is `RootOpening`; `WatchFaultKind.Drive` is `Volume`;
`DriveCaughtUp` is `VolumeCaughtUp`; `TestDriveRoot` is `TestRootDirectory`; `SyntheticDriveHeader` is `SyntheticRootHeader`; `DriveKind` is
`RootKind`; `System.IO.DriveType` stays the BCL type. Consumer migration is plain renaming by file in the pin-bump pull requests.

## VolumeKey parses volume specs

**Decision (2026-10-19):** `VolumeKey.TryParse(string? text, out VolumeKey result)`
accepts one ASCII letter with an optional colon and an optional final backslash
or slash, ignoring surrounding whitespace and case. Success returns the normalized
key. Null, empty, whitespace, UNC roots, multi-letter tokens, and paths beyond the
root return false and set the out parameter to the invalid default key. Callers
holding a path pass its root; path resolution belongs to the caller.

**Reason:** file-wizard and git-wizard each hand-rolled the same root-spec parser
and compared strings instead of VolumeKey values. `RootKey.TryParse` builds on it: a volume spec parses to the volume case and
any other valid full path to a normalized directory.

**Revisit only if:** a consumer needs path-to-root resolution MFTLib cannot
define portably.

## A boolean that reports state takes the Is prefix

**Decision (API review finding F15, naming half):** a public boolean property that reports state on a
status or result type takes the `Is`, `Has` or `Can` prefix: `RootWatchStatus.IsSupported`, `IsRequested` and
`IsRecoveryStopped`, `JournalCatchUpLostException.IsRecoveryStopped`, `RootBlockStatus.IsCapacityExhausted` and
the test package's `PowerShellLaunch.IsElevated`. A boolean option that reads as an instruction takes no prefix
(`CaseSensitive`, `IncludeDeleted`, `IncludeFreed`, `CatchUpOnStart`). `BooleanNamingTests` enforces the rule
by reflection over the compiled MFTLib and MFTLibTestExtensions assemblies, with an explicit allowlist of the option types (`MftScanOptions`,
`SearchQuery` and `ScriptedWatchSource`).

**Reason:** thirteen state flags already took `Is` and six did not, so a reader could not tell a state flag from
an option by its name, and one status record mixed both styles. The framework guideline leaves the prefix
optional, so this is a house rule for consistency, not a correction of an error. The allowlist names types
rather than matching a suffix because `SearchQuery` and `ScriptedWatchSource` do not end in `Options`.

**Revisit only if:** a public type needs a boolean that is neither state nor an instruction.

## One name per concept across the public surface

**Decision (API review finding F16):** a file's last write is `LastWriteTime`, a scan's completion
is `ScanTimestamp`, a journal event's time is `Timestamp`, a byte size is `Size` with no unit suffix, a count
ends in `Count`, and a bound is the property name followed by `Minimum` or `Maximum`. Applied in MFTLib and
MFTLibTestExtensions: `SyntheticRow.LastWriteTime`, `ScanTimestamp` on `SyntheticBlockOptions`,
`SyntheticMftProducer`, `CachedBlockFile.Size`, `IndexScanProgress.WrittenRowCount`
and `ExpectedRowCount`, and `SearchQuery.SizeMinimum` and `SizeMaximum`. A parameter that takes a `RootKey` is
named `root` (or `roots` for a sequence) and one that takes a `VolumeKey` is named `volume`, never `driveLetter`.

**Reason:** the surface named the same concept several ways, so a reader had to learn each type's word for a
last write time, a scan time, a size, a count and a query bound. The test package had dropped the `Utc` suffix
without aligning to the production names, and no record shows the difference was chosen. Names that mirror an
NTFS structure (`UsnJournalSettings` and the test package's `SyntheticJournalWindow.MaximumSize`) are outside
the rule.

**Revisit only if:** a new concept has no existing word, or an NTFS structure's own name conflicts with the rule.

## Raw block-header inspection is internal test support

**Decision:** `SyntheticIndexInspection` and `SyntheticRootHeader` are internal to
`MFTLibTestExtensions`, available to `MFTLib.Tests` through its existing friend-assembly access.
Consumer tests read `index.Root().RecordKey.ProducerKind` for the producer and
`status.Block!.LiveRowCount` for live inventory. `RootBlockStatus.ScanTimestamp` supplies the stored scan time.

**Reason:** the only consumer uses are two git-wizard assertions already answered by the index.
Raw header row counts include unused slots, whereas live row counts express the inventory assertion.
file-wizard has no consumer uses outside its MFTLib submodule.

**Revisit only if:** a consumer test needs a block-header observation the public index cannot answer.

## Source absence is typed; applications own their wording

**Decision (owner ruling 2026-10-19, API review finding F19):**
`FileIndexOptions.MftSource` is null when the application has no MFT source.
An MFT volume that needs a scan reports `RootFailureKind.MftSourceMissing`
with a library-owned, user-readable sentence naming the root. A rescan cannot
recover it while the source stays null. Warm cache adoption needs no source;
watch start reports `NotApplicable` without recording a request.

**Reason:** the application already knows why it has no source and chooses its
own wording where it displays the failure kind. Real sources come from
`BrokerSession.CreateIndexSource`, `MftIndexSources.FromLocalVolumes`, and
`MftIndexSources.FromMftDumpFile`.

**Revisit only if:** an application needs a structured distinction that the
missing-source kind and its own configuration cannot express.

## A method named Try returns quickly

**Decision (API review finding F23):** `ElevatedEntryPoint.RunBrokerIfRequested(arguments)`
is the broker-mode dispatch method.

**Reason:** the method runs the broker for its whole life when the process was started with the broker flag,
exits the process with code 1 on rejected arguments, and otherwise returns false at once. A `Try` name in .NET
promises a quick attempt that reports failure by returning false, which does not describe a broker run.

**Revisit only if:** the method stops blocking for the broker's lifetime.

## Name of the index scan-progress option, and what stays as it is

**Decision (API review finding F21; updated for issue 829):** `FileIndexOptions.ScanProgress` reports scan
progress samples while a scan runs. `BrokerSession.Connecting`
stays an event. The mix of events, `IProgress` options and the `Diagnostics` callback stays, because each style
follows what it reports.

**Reason:** the option's name identifies the operation it reports.
`BrokerSession.Connecting` stays because git-wizard production
(`GitWizard/Discovery/RepositoryDiscoveryCoordinator.Watch.cs`) and the Watch sample use it.

**Revisit only if:** a consumer stops using `BrokerSession.Connecting`.

## The elevation provider types and the three query methods stay

**Decision (API review findings F11 and F26):** `IElevationProvider`,
the production provider exposed through `ElevationUtilities.DefaultProvider`, and both
`BrokerSession` constructor parameters stay as they are (F11).
`ProducerKind.Enumeration` and all three query methods `Search`, `Enumerate` and `EnumerateRows` stay (F26).

**Reason:** F11 found no defect to correct in the elevation provider shape or the session constructors. For F26,
`EnumerateRows` is a `ref struct` and cannot be used with LINQ or `await`, and `Search` is the only form that
cannot hold a snapshot borrow open, so each of the three serves a caller the others cannot.

**Revisit only if:** `EnumerateRows` stops being a `ref struct`, or a consumer needs elevation behavior the
current provider types cannot express.

## A state enum plus a nullable reason

**Decision (API review finding F14):** a state enum is the field callers branch
on; the reason or error that accompanies it is a nullable property, non-null only when there is something to
report; a reason enum has no "nothing wrong" member. `CachedBlockStatus.Validation` is null unless
`Availability` is `Invalid`, and `BlockValidationResult` has no `Valid`. `RootBlockStatus.CacheLocation` is a
nullable `CacheLocation`, null when the block is not backed by a cache location, and `CacheLocation` has no
`NotApplicable`.

**Reason:** two fields stating one fact can contradict each other, and both consumers branch on the state enum
and only print the reason. Six of the eight state-plus-reason pairs on the public surface already had this
shape: `RootStatus.Failure`, `WatchStateChange.Fault`, `RootOperationResult.Failure`,
`CachedBlockDeletionResult.FailureMessage`, `IndexScanProgress.Outcome` and `RootWatchStatus.FailureMessage`.

**Revisit only if:** a consumer needs to distinguish "checked and valid" from "not checked" on a row it cannot
already distinguish by the state enum.

## One cache inspection method, documented directory preparation

**Decision (API review finding F20):** `CacheDirectory.InspectCached`
is one public method with optional `IEnumerable<RootKey>? roots = null`
and `Action<CachedBlockRejection>? rejectedFile = null` parameters.
`CacheDirectory.EnsureCreated` stays public and unchanged, with its application
usage documented. `DeleteCached` keeps its rejected-filename callback overload internal.

**Reason:** inspection's two overloads differed only by optional rejection
reporting; one method follows the existing one-method-per-verb rule and supports
both callers directly. file-wizard's `FileWizard/ContentHashSidecar.cs` calls
`EnsureCreated` before writing inside its cache directory, so directory preparation
is a production API readers need to discover. Cached index opens already call it
automatically. No consumer needs rejected filenames from public cache deletion.

**Revisit only if:** a concrete consumer requirement changes these responsibilities,
such as a production need for rejected-filename reporting during deletion.

## No privacy promise: full MFT metadata retention is the default

**Decision (owner ruling 2026-10-26):** MFTLib makes no promise to hide any file's metadata from a
process running as the indexing user. MFT scans offer no subtree-exclusion option, and full metadata
retention is their default. `DirectoryWalkOptions.PrunedDirectoryPaths` and the other directory-walk
pruning rules can skip enumeration subtrees. These rules are not privacy boundaries. What MFTLib
does do: `CacheDirectory.EnsureCreated`, which every cached open calls, gives the cache directory an
access list of the current user plus SYSTEM with inheritance removed (owner-only mode on Unix), and
file contents are never read.

**Reason:** a scan reads the raw MFT, through the elevated broker or an elevated process, and that
read applies no per-file access checks. With default retention, the index and its cache file hold
the name, parent folder, attributes, size and timestamps of every file on the volume, including
files the user's account cannot open. At some point the full table is in the consuming process's
memory, where anything able to read that process can read it. Enumeration pruning is not a privacy
guarantee. An MFT subtree-exclusion option presented as a privacy boundary would be a promise MFTLib
cannot keep.

**Revisit only if:** a consumer asks for MFT subtree exclusion for a reason other than privacy, for
example size.

## Cache deletion reports per-file outcomes through results

**Decision:**
`CacheDirectory.DeleteCached(string cacheDirectoryPath, IEnumerable<RootKey>? roots = null)`
returns each attempted file's outcome and optional failure reason in
`CachedBlockDeletionResult`. The Watch sample prints those results once.
The internal overload additionally accepts `Action<CachedBlockRejection>? rejectedFile`
for non-canonical filenames excluded from the result list.

**Reason:** the success logger duplicates information already in the results.
The Watch sample passed its output writer and then printed the same deletions
from the result list. git-wizard's `GitWizard/MftIndexCacheMaintenance.cs`
and file-wizard's `file-wizard/CliServices.cs` already call deletion without
a logger and consume its returned results. Their call expressions stay as
written when their MFTLib pins are updated and rebuilt.

**Revisit only if:** a concrete consumer needs information that per-file
deletion results cannot express.

## File entries expose their root and keep distinct state questions

**Decision (API review finding F24; updated for issue 829):** `FileEntry.Root` is a
`RootKey` equal to `RecordKey.Root`, with the same default-value and disposal
behavior as the entry's other data properties. `FileIndex.Status` reports the single root's `RootStatus`.
`FileEntry.IsValid` and `IsDisposed` stay separate.

**Reason:** `IndexRow` already exposes `Root`, while both consumers repeatedly
read `FileEntry.RecordKey.Root`. Direct access removes that repeated two-hop
read. The entry's state properties answer different questions: `IsValid` says whether
the handle references a root block, `IsDisposed` says whether that block has
been released, and `IsDeleted` identifies a retained tombstone. A disposed handle
can still be valid; a default handle is neither valid nor disposed.

The Watch sample reads `change.Entry.Root` when printing each change. The getter
throws `InvalidOperationException` for a default entry and
`ObjectDisposedException` after its root block is released. Consumer expression
replacements belong to their pin-bump pull requests.

**Revisit only if:** concrete consumer requirements change root identity or the
distinct meanings of these state questions.

## Hand-out watch events and operation results are library-constructed

**Decision (API review finding F22):** `WatchStateChange` and
`RootOperationResult` follow the receive-only record rule: explicit internal
constructors, public init-only properties, and no positional `Deconstruct`.
Tests construct them through `SyntheticNotifications.CreateWatchStateChange`
and `SyntheticNotifications.CreateRootOperationResult` in `MFTLib.TestExtensions`.

**Reason:** the library produces these events and operation verdicts. The
consumer review found no production need to construct them. file-wizard's
snapshot seeding passes the root, `RootWatchStatus`, and nullable fault to
its own merge/presentation method instead of wrapping a snapshot in an event.
Its cache-only host reports its own refusal instead of fabricating a library
result. Forwarding that refusal to `RescanAsync` was rejected because it would
touch the cache file and rewrite the root's failure kind.

Consumer pin-bump pull requests migrate those production callers and replace
synthetic events/results in consumer tests with the factories. The factories
forward to the internal constructors, retaining the same root-key validation.
The public init-only properties and record copy/equality behavior remain.

**Revisit only if:** a concrete consumer production requirement needs the
library to accept an application-constructed event or operation result;
snapshot presentation and application-owned refusals do not establish that need.

## The test package keeps the shapes of the consumer doubles it was hoisted from

**Decision (API review finding F25):** the doubles in `MFTLib.TestExtensions`
follow three shape rules.
Configuration properties are init-only, except a property a test changes while the
double is running, which keeps `set` and says so in its XML summary
(`ScriptedWatchSource.StartFailure` is the only one). Batches of
`SyntheticJournalRecord` are `IReadOnlyList` in every public signature, including
the `ScriptedBrokerVolumes.ReadJournal` and `WatchVolume` delegates. No public type
lists a base type or interface that is not public: `ScriptedWatchSource` and
`ScriptedVolumeWatch` reach the index through an internal adapter instead of
implementing `IIndexWatchSource` and `IIndexVolumeWatch`.

**Reason:** the package was hoisted from consumer test code and kept the accidents of
those originals: some properties settable and others init-only for no reason, array
parameters beside list parameters, and public classes advertising interfaces a
consumer cannot name or implement. A consumer cannot tell a property that is safe to
change mid-run from one that is not unless the shape says so. `TestPackageSurfaceTests`
pins the third rule, with a negative control over an internal interface.

**Revisit only if:** a consumer test needs to change a currently init-only double
property while the double runs; that property then keeps `set` with a summary saying why.

## The broker session ends only when the broker ends

**Decision (API review finding F1):**
`BrokerSession.Ended` is the one member that reports a broker end. It completes once, shortly after the launched
process ends, with the end reason, and never at disposal. A session that never launched, or that is disposed while
its process is alive, never completes it. A caller that wants a flag reads
`Ended.IsCompleted`. A process end that completed before disposal began is still published. After `DisposeAsync`
every use throws `ObjectDisposedException`; a use after the process ended throws `InvalidOperationException`
naming the reason.

**Reason:** consumers must distinguish broker death from their own disposal. One task
with one meaning supplies both notification and a completion flag without observer lag.

**Revisit only if:** a consumer needs to await its own session's disposal through the same task.

## One failure value and a nullable block on RootStatus

**Decision (API review finding F2; updated for issue 829):**
`RootStatus.Failure` is a `RootFailure` (`Kind`, non-null `Message`), non-null exactly when the root is
`Failed`, or `Ready` retaining a block after a failed `RescanAsync` or a failed catch-up retry scan during
`OpenAsync` (kind `ProducerFailed`, the producer's message). A successful scan publication clears it and an
`Offline` root has none. `RootStatus.Block` is null unless `State` is `Ready`.
`RootFailureKind` contains failure reasons and `BlockOrigin` identifies an actual origin;
absence is represented by the nullable containing value. A failure message is required
where it is produced; there is no empty-string fallback.

**Reason:** "did this root fail, and why" took three fields whose combinations were not independent: a `Ready`
root could carry a rescan failure message with kind `None`. A blockless root exposed a block of zeros that
git-wizard detected by testing `ScanTimestamp == default`, and both consumers recombined the fields with their own
fallback text. This is the first instance of the state-plus-nullable-reason rule above.

**Revisit only if:** a consumer needs a failure that is not tied to one root.

## One watch snapshot type, named for what it holds

**Decision (API review finding F3; updated for issue 829):**
the enum is `WatchState`; its members `NotStarted`, `Recovering` and `Faulted`
describe the watch lifecycle, and `RootWatchStatus.State` is its property. `FileIndex.WatchStateChanged` is an
`Action<WatchStateChange>`; `WatchStateChange` carries `VolumeKey Volume`, the same
`RootWatchStatus` snapshot that `RootStatus.Watch` reports (`Watch`), and the optional `Fault`. One builder produces
the snapshot for both `Status` and the event, so they cannot diverge.

**Reason:** snapshots and events report the same state and version, so consumers can seed
their history without converting between shapes. Names distinguish the snapshot, event and enum.

**Revisit only if:** an event needs data that the snapshot does not carry.

## MftScanOptions configures every MFT scan

**Decision (API review finding F4):**
`MftScanOptions` configures every MFT scan: broker-backed through
`BrokerSession.CreateIndexSource(scanOptions)` and in-process through `MftIndexSources.FromLocalVolumes(scanOptions)`.
It holds `IncludeFreed`, the one setting that exists only for an MFT scan.

**Reason:** scan options apply independently of the transport, and freed records exist only in the MFT.

**Revisit only if:** a scan setting applies to one transport only.

## Every MFT index stores every name

**Decision (2026-10-27, owner rulings 3 and 4 of MFTLib issue 844):** every MFT index stores the whole volume
with every name. `FileIndexOptions.RetainedFileNames` and the test package's
`InProcessBrokerScan.RetainedFileNames` are deleted, along with the broker's retained-name payload, the
producer filters and the Watch sample's retention flags. Every producer writes every eligible record.
`MftScanOptions.IncludeFreed` remains an independent scan choice, and directory-walk pruning is unchanged.
This supersedes "Retention is an index option applied by every producer".

**Reason:** retention existed only to shrink a block, and a name filter prevents all indexes on one volume
from sharing one scan. Format 8's deduplicated, dual-width name encoding makes the all-names design smaller:
the measured volumes total 2,527 MB against 3,310 MB for today's filtered layout. There is no longer a
storage reason to discard names.

**Revisit only if:** a future format cannot provide whole-volume sharing without discarding names.

## The directory walk prunes declaratively and records pruned directories

**Decision (amended 2026-10-27, owner ruling on [issue 835](/schoen/MFTLib/issues/835), mftlib-030-closeout link 8):**
`FileIndexOptions.DirectoryWalk` is a `DirectoryWalkOptions` record, defaulting to `new()`, with
five rules: `PrunedDirectoryNames`, `PrunedDirectoryNamePatterns`, `PrunedDirectoryPaths`, `PrunedDirectoryAttributes` and
`PruneDotDirectories`. A pruned directory is written as a row, like `find -prune`, but its children are not
enumerated. The walk's root directory is never pruned. On directory walks, the exact-name and path pruning rules use
one root comparison: ignoring case on Windows, ordinal elsewhere. `PrunedDirectoryNamePatterns` is an
`IReadOnlyCollection<Regex>` defaulting to `[]`: each `Regex` carries its own `RegexOptions` and match timeout (for example
`new Regex(@"^\.git$", RegexOptions.IgnoreCase)`) rather than using the root comparison, and a directory whose name matches
any pattern is pruned the same way. `PrunedDirectoryNames` stays as the exact-name hash lookup. Each pattern's `ToString()`
and `RegexOptions` (without `MatchTimeout`) are written into the enumeration `ScanShape` in a canonical order, so changing a
pattern or its options discards the block and cold-scans. Paths match a directory whose full path
equals them after `Path.GetFullPath` and
`Path.TrimEndingDirectorySeparator`, so a path outside a root's directory has no effect on it. Hidden and system
entries stay included unless a rule prunes them, and reparse points stay recorded and never entered. MFT volumes
ignore the record, including `PrunedDirectoryNamePatterns`. `FileIndex.OpenAsync` rejects a null record or collection, a null pattern element, a blank or separator-containing
name, a path that is not fully qualified, and `Directory` or `ReparsePoint` in the attribute mask with
`ArgumentException` before any side effect. `PruneDotDirectories` is an instruction, so `BooleanNamingTests`
allowlists the type.

**Reason:** a delegate predicate over name, attributes, depth and path was rejected for two reasons. The
library cannot fingerprint a delegate, so every consumer would hand-version its cache tag again. A predicate also invites consumer logic into
the walk, such as git's bare-repository test, which stays in git-wizard. The initial four rules could not prune a name
case-insensitively on a Unix root because `PrunedDirectoryNames` uses the ordinal root comparison off Windows, forcing
git-wizard ([git-wizard issue 441](/schoen/git-wizard/issues/441)) to list all eight case spellings of `.git` to prune `.GIT` on
a case-insensitive Unix mount. Wildcards (`FileSystemName.MatchesSimpleExpression`) were rejected because they have no character
classes and their case option is per call, not per pattern. Adding `PrunedDirectoryNamePatterns` gives each rule its own
`RegexOptions` while keeping the rule set fingerprintable in `ScanShape`, and recording the pruned directory keeps a marker
row such as `.git` while skipping its contents.

**Revisit only if:** a consumer needs a pruning rule that none of the five can express.

## The library owns scan-shape identity

**Decision:** block format 6 stores a 64-bit scan shape at header offset 112: the first eight bytes of SHA-256
over the canonical encoding of the options that shaped the rows, per producer (`docs/index-format.md`). Warm
start compares it after the cache tag; a mismatch records `BlockValidationResult.WrongScanShape`, deletes the
block under its owner lock and cold-scans, and a cache-only open leaves the block in place and fails with
`RootFailureKind.CacheIdentityMismatch`, the kind both identity mismatches report. `CacheTag` carries only
meaning the consumer owns. The shape has no public accessor, and `CacheDirectory.InspectCached` does not report
it. `MftIndexSource` carries its producer's `IncludeFreed` flag internally so the index can compute the MFT shape.

**Reason:** a consumer cannot hand-version a user-editable exclusion list. Every consumer reacts to a tag
mismatch and a shape mismatch the same way, by rebuilding, so one failure kind covers both.

**Revisit only if:** a consumer must react differently to a shape mismatch than to a tag mismatch.

## A cache-only open never deletes a block

**Decision:** `CacheMode.ExistingOnly` treats every warm-start rejection (format, volume serial, producer
kind, root directory, cache tag, scan shape) as a refusal: the root fails with `CacheDeclined` or
`CacheIdentityMismatch` and the file stays in place, unchanged. Only a writing open (`Persistent`, or a
`RescanAsync`) deletes or replaces a rejected canonical block, because only it has a scan to put in its place.

**Reason:** a cache-only open has nothing to replace a rejected block with, so deleting it only destroys a
block another index or a later open under the matching options could still adopt. A reader such as the
Benchmark, which inspects a slot and opens it later, could otherwise delete a block another process published in
between. The rule makes the cache-only open itself non-destructive instead of asking each reader to hold the slot
lock across inspection and adoption.

**Revisit only if:** a consumer needs a cache-only open to clear rejected blocks; `CacheDirectory.DeleteCached`
already does that explicitly.

## Search bounds are named for what they compare, and say what they exclude

**Decision (API review finding F5):**
`SearchQuery.LastWriteTimeMinimum` and `SearchQuery.LastWriteTimeMaximum` are
inclusive bounds on `FileEntry.LastWriteTime`, which is UTC. The raw ticks of the
supplied `DateTime` are compared with no `DateTime.Kind` conversion, so the caller
supplies a UTC value. A time bound excludes rows whose last-write time is unknown.
`SearchQuery.SizeMinimum` and `SearchQuery.SizeMaximum` are inclusive size bounds;
either excludes a row whose size is unknown even when its stored size satisfies
the comparison. `SearchQuery.Subtree` restricts the result to the subtree below
an entry, the entry included. `SearchQuery.IsDirectory` is true for directories
only, false for files only, and null for no filter.

**Reason:** bound names identify the compared property and make their inclusive
meaning explicit. Size filtering also excludes unknown observations: a
`SearchQuery.SizeMinimum` value of `long.MinValue` drops unknown sizes. The
directory and subtree options identify their selection directly. The owner
retained `SearchQuery.CaseSensitive`, the time bounds, `SearchQuery.SizeMaximum`,
and `SearchQuery.IncludeDeleted`; the Direct sample exercises them.

**Revisit only if:** a consumer needs an exclusive bound or a size filter that keeps unknown sizes.

## Public XML comments name public members, and Root says why it refused

**Decision (API review findings F17 and F18; updated for issue 829):**
a public XML comment refers only to public members and states the contract the code has. `MftIndexSource` names
`BrokerSession.CreateIndexSource` and `MftIndexSources`, `JournalCheckpointLoss.SizeThatWouldHaveRetained` states that
the library reports the size and the consumer owns the growth decision, and
`BrokerDiagnostics` documents the public diagnostics configuration. A broker failure
does not fall back to a direct scan. `FileIndex.Root(CancellationToken = default)` documents its
`InvalidOperationException`, `Find` documents that null means no entry, and `Search` and `Enumerate` document
`ArgumentNullException` for a null query. `FileIndex.Root` throws `InvalidOperationException` with `"Root X has no block (state: Offline)."` (the root's current `RootState`) when the root has no block.

**Reason:** a reader following public documentation landed on types they cannot see, and a root configured in `FileIndexOptions.Root` that settled `Offline` or `Failed` needs a state-bearing refusal message. Both git-wizard `Root` call sites can meet such a root.

**Revisit only if:** `Root` gains a non-throwing form for a root without a block.

## A root watch's pump owns fault attribution

**Decision:** `IIndexVolumeWatch` supplies a read stream and asynchronous disposal; it carries no
`Volume`. `FileIndex` attributes batches and faults to the `RootRuntime` that started the pump, and
`VolumeWatchFaultException` carries only a message and optional inner exception to classify a
`WatchFaultKind.Volume` fault. Consumers read `WatchFault.Volume`.

**Reason:** the pump already owns the authoritative root; repeating it on the handle and the marker
exception informed nobody.

**Revisit only if:** one watch stream can represent several independently owned root runtimes.

## Windows volume discovery reports every volume with an MFT verdict

**Decision (owner, 2026-10-16; record 2026-10-18; updated for issue 829):** `IndexedRoot.EnumerateWindowsVolumes()` returns every
root from `DriveInfo.GetDrives()` as `IReadOnlyList<WindowsVolume>`. `WindowsVolume` is a public sealed record
of `VolumeKey Volume`, `string RootDirectory`, `DriveType DriveType`, `bool IsReady`, `string? Format`,
`bool IsIndexable` and `string? Reason`. Only ready fixed NTFS volumes are indexable; NTFS compares with
`StringComparison.OrdinalIgnoreCase`. `Reason` stays for display: `not ready`, `network`, `removable`,
`optical`, `other drive type`, `not NTFS (<format>)`. Non-fixed volumes are classified by type alone, so
`IsReady` is true only for a fixed root read as ready, and `Format` is non-null only for a ready fixed root.
A per-root `IOException`, `UnauthorizedAccessException` or `SecurityException` means `not ready`.
Free-space and size stay consumer-side.

**Reason:** Watch and file-wizard each carried a diverging copy of "ready, fixed, NTFS"; the library
owns the policy once. A consumer that wants the ready fixed non-NTFS volumes (the directory-walk candidates)
branches on `DriveType`, `IsReady` and `Format` instead of parsing the reason text. file-wizard migrates in
its pin-bump pull request.

**Revisit only if:** a consumer needs a different selectable discovery policy.

## Capacity exhaustion names the state; rescan is the remedy

**Decision (2026-10-16):** use `CapacityExhausted` for the internal state and `IsCapacityExhausted` for the public boolean; bit 2 and the block format stay unchanged. A write did not fit, and a rescan rebuilds the inventory. **Reason:** state names describe the observed condition and the API names the actual remedy. **Revisit only if:** a consumer needs a distinct capacity-recovery state.

## The public-surface pin stays bespoke

**Decision (2026-10-16):** retain the bespoke public-surface pin; do not add PublicApiGenerator or PublicApiAnalyzers. **Reason:** the caller gate needs the bespoke metadata reader anyway; another library adds a second IL reader and a third-party dependency. **Revisit only if:** the caller gate no longer needs that reader and one replacement can cover both contracts.

## Block completion uses an internal TimeProvider

**Decision (2026-10-16):** producers and section writers take an internal `TimeProvider`; enumeration uses internal `FileIndexOptions.TimeProvider`, defaulting to `TimeProvider.System`. Tests reach the option through internal `MFTLibTestExtensions` forwarding. Completion stores `GetUtcNow().UtcDateTime` after production. **Reason:** one clock abstraction makes completion deterministic without public configuration. **Revisit only if:** a consumer production requirement needs clock configuration.

## Origin, cache location and journal position have distinct names

**Decision:** `RootBlockStatus.Origin` uses `BlockOrigin`; `CacheLocation` uses nullable `CacheLocation`. Synthetic journal records use `Usn`, with `FirstUsn` and `NextUsn` for bounds.
**Reason:** provenance, cache backing and journal position each need one vocabulary; data-supplier types keep Source/Sources and row positions keep slot terminology.
**Revisit only if:** a concrete consumer needs a distinct concept these names cannot express.

## Live watch is MFT-only in 0.3.0; walked roots refresh by age

**Decision (2026-10-20):** in MFTLib 0.3.0, live watch is an MFT-only feature. Enumeration (directory-walk)
roots report `RootWatchStatus.IsSupported` false. Their refresh contract is rescan-by-age: a consumer
compares `RootBlockStatus.ScanTimestamp` with its own maximum age and calls `FileIndex.RescanAsync`
(file-wizard's `CacheFreshness` does this). Live watch for enumeration roots is deferred to 0.4.0. Issue 675
stays open for that work.

**Reason:** neither consumer needs it for 0.3.0: file-wizard refreshes by age, and git-wizard keeps its own
Linux fanotify watch. The draft design (issue comment 129571) also still has an unsolved Windows problem:
`FileSystemWatcher` cannot prune, so heavy `.git` and `node_modules` activity overflows its buffer and turns
overflow-then-rescan into a rescan loop.

**Revisit only if:** a consumer needs live updates on a walked root, or during 0.4.0 planning.

## Windows-only annotations sit on the member that reaches Windows

**Decision (2026-10-26; updated for issue 829):** `[SupportedOSPlatform("windows")]` sits on the narrowest member that reaches a
Windows-only API (a Windows P/Invoke, WMI, a named memory-mapped section, a Windows access-control list, UAC
elevation), never on a whole type or assembly. Platform-neutral logic and test seams carry no annotation.
A public Windows-only member whose behavior wraps logic is a one-line entry point over an internal
platform-neutral member that tests call on every platform: `BrokerLauncher.Launch` over `StartElevated`,
`IndexedRoot.FromWindowsDrive` over `ReadWindowsVolume`, `FileIndex.QueryUsnJournalSettings` over
`ReadUsnJournalSettings`, and both launcher `BrokerSession` constructors over an internal constructor that takes
the block section factory (production passes the Windows-only named-section factory). Internal members that
guard with `OperatingSystem.IsWindows()` and throw `PlatformNotSupportedException` off Windows, such as
`UsnJournalSettingsQuery.Query`, stay unannotated so their refusal is testable on Linux.

**Reason:** a type- or assembly-wide annotation forced cross-platform tests (broker, index and Benchmark logic
run on Linux through faked OS calls) to claim Windows-only to compile, which MSTest's MSTEST0084 flags. Suppressing
MSTEST0084 or CA1416 hides the mismatch instead of fixing the description of the API (issue 743).

**Revisit only if:** a public member's platform-neutral logic is needed by a consumer on Linux, which would
make the internal member a public one.

## Windows volume construction is named for its volume key

**Decision (owner ruling B6, 2026-10-26; updated for issue 829):** `IndexedRoot.FromWindowsDrive(VolumeKey volume)` is the sole public
name for the Windows-only factory that reads the volume's serial and returns an MFT `IndexedRoot`. Because the
parameter is a `VolumeKey`, the volume is a letter by type and the factory has no letter validation of its own.

**Reason:** the factory takes a `VolumeKey`, not a `WindowsVolume`. Naming it for the Windows volume avoids
suggesting that callers must first construct or supply a `WindowsVolume`.

**Revisit only if:** the factory changes the identity it accepts or no longer constructs a root from a Windows
volume key.

## Journal application is asynchronous end to end

**Decision (2026-10-26, issue 700):** `MFTLibTestExtensions.FileIndexTestAccess.ApplyJournalEntriesAsync` replaces
the synchronous `ApplyJournalEntries` and returns `Task<IReadOnlyList<FileChange>>`. It takes no cancellation
token: a direct apply has no stop of its own. Internally every journal apply awaits the volume's write gate; the
watch pump passes its stop token, so a stop that arrives while a batch waits for the gate ends the pump as a stop
and leaves the batch unapplied.

**Reason:** the only production caller of journal application is the async watch pump. The synchronous signature
made a thread-pool thread block on the same `SemaphoreSlim` the commit path awaits, and a stop could not end that
wait (JetBrains MethodSupportsCancellation, issue 700). The test-package forwarder was the one public face of the
synchronous shape.

**Revisit only if:** a consumer needs to apply journal entries from a context that cannot await.

## The Watch sample keeps every cached block in one cache folder

**Decision (2026-10-26; updated for issue 829):**
(B3) The Watch sample keeps every root's cached block in the one cache directory. Its per-policy
subfolders, the retention fingerprint behind them and the `cache` verb's per-folder loop are deleted;
the library's scan-shape comparison rebuilds a block when row-shaping flags change.
(B7) `BrokerSession(launchBroker, connectTimeout)` stays public because a future consumer may set its own
wait. The Watch sample passes `--connect-timeout <seconds>` to it, which replaces the constructor's
exemption in `PublicMemberCallerTests`.

**Reason:** the library already compares the scan shape, so a consumer-side folder per policy duplicated a
library responsibility and left stale folders behind. The connect timeout is a real operator choice
that a sample can show.

**Revisit only if:** a consumer needs the cache split by policy outside the library's scan-shape
comparison, or the connect timeout stops being a consumer setting.

## Block Generation is a per-root change version

**Decision (2026-10-26, owner ruling B8):** the block header's `Generation` stays and is exposed as
`RootBlockStatus.Generation` (`ulong`). Within one `FileIndex` it strictly increases on every applied journal
mutation batch and on every rescan: a replacement block starts at the superseded block's value plus one, a warm
start adopts the cached block's value, and a cold scan with no previous block starts at 1. It is comparable only
within one `FileIndex` instance; a deleted cache or a new process can restart it, so a consumer remembers the last
value it saw for a root and compares for inequality.

**Reason:** the field was written but never read, and a rescan restarted it at 1, so no single value could say
whether a root changed. file-wizard `FileWizardMaui.Logic/DuplicateNameCounter.cs` uses it to skip a
duplicate-name recount when no root's Generation changed.

**Revisit only if:** a consumer needs change versions that survive a deleted cache or span processes.

## FileEntry.Children stays, with the Direct sample as its caller

**Decision (2026-10-26, issue 759):** `FileEntry.Children` stays as the supported direct-child navigation primitive.
`SampleProgram.Direct` calls it from its `children` verb, which lists one directory's direct children (the root directory
without `--path`), and `PublicMemberCallerTests` sees that sample caller instead of a reasoned exemption.

**Reason:** the member is the natural answer to "what is directly inside this directory"; the `tree` verb groups one
streaming pass by parent because it walks many directories, which `Children` (a whole-root scan per call) does not
suit. The earlier exemption named a consumer file that no longer calls it, so the caller gate was satisfied by a
stale reason.

**Revisit only if:** the children table becomes a precomputed structure and the sample's `children` verb no longer
exercises the member.

## WindowsVolume.Reason stays as display-only text

**Decision (2026-10-26, issue 759):** `WindowsVolume.Reason` stays. It is display text that consumers show and never
parse; its XML documentation says it is not a stable format. Consumers branch on `DriveType`, `IsReady`, `Format` and
`IsIndexable`. The caller of record is the Watch sample, `SampleProgram.Shared/SampleHost.Drives.cs`, which prints it.

**Reason:** the strings explain a skipped root to a person; the typed members already carry the facts a program
needs, so a second machine-readable channel would duplicate them.

**Revisit only if:** a consumer needs a skip classification the typed members cannot express.

## The Direct sample's largest and duplicate-names verbs stay as heavy-scan demos

**Decision (2026-10-26, issue 759):** `SampleProgram.Direct`'s `largest` and `duplicate-names` verbs stay. They are
deliberate heavy-scan demonstrations of row-level algorithms built over the public row enumeration.

**Reason:** they show a consumer how to run a bounded-memory whole-root pass (a size heap, a hash sieve) without
materializing entries, which no lighter verb demonstrates.

**Revisit only if:** the row enumeration they exercise is covered by another sample verb.

## The Defender exclusion script is passed with -Command, not through a pinned temporary file

**Decision (2026-10-26, owner ruling B9):** the Defender exclusion launch passes its script to Windows PowerShell as
`-NoProfile -NonInteractive -Command <script>`. The temporary `.ps1` file and its pinning (held handle, file-id check,
wildcard-path refusal, no delete by path) are removed. A plain `-Command` with the same script, run unelevated on the
owner's machine on 2026-10-26, raised no Defender detection (the earlier `-EncodedCommand` launch had raised
`Trojan:Win32/Commando.A!ml`); the owner ruled that a file would need only a diff check and that none is needed.

**Reason:** the pinning defended against a same-user file swap that was never observed, with far more code than the
two-line script it protected. A process already running as the user can interfere with this step (it can edit the
user's own Defender-adjacent settings, PATH or the launching process itself), and MFTLib does not try to prevent it.

**Revisit only if:** Defender flags the `-Command` launch, in which case the script returns to a temporary file checked
by comparing its bytes with the bytes written immediately before launch.

## The native synthetic MFT generators stay in the shipped DLL

**Decision (2026-10-26, owner ruling B10):** the native generators in `MFTLibNative/mft/mft_synthetic.cpp` (bulk,
`GenerateSyntheticMFTSizedUtf8`) and `mft_synthetic.fixture.cpp` (24-record fixture, `GenerateFixtureMFTUtf8`) stay in
the shipped DLL. `Benchmark native-timings --synthetic` calls the bulk generator through `MftVolume.GenerateSyntheticMFT`;
Benchmark's managed copy of it is deleted. `MFTLib.Tests/TestSupport/MftDumpFixture.cs` stays.

**Reason:** the bulk generator produces realistic input (4096-byte records, extension records, non-resident data,
millions of records, written by several threads) that a managed copy would reproduce less well. The 24-record fixture
drives the parser edge-case tests on Windows and Linux. The shipped DLL is the tested DLL, as with the native test hooks.
`MftDumpFixture` stays because it builds exact records for single-field corruption tests, which a bulk generator cannot do.

**Revisit only if:** a generator stops being used by any test or by Benchmark. A proposal to remove the generators on the
grounds that test code ships in the release DLL does not reopen this ruling.

## Pruning uses the root comparison

**Decision (2026-10-26, owner ruling B11):** directory-walk pruning uses the root comparison. The directory
walk ignores case on Windows and is ordinal elsewhere for exact pruned names and pruned paths
(`PrunedDirectoryNamePatterns` uses each `Regex` instance's own `RegexOptions`). The format version moved to
6, so blocks shaped under the previous comparison are discarded.

**Reason:** one rule matches what the file system itself treats as the same name.

**Revisit only if:** a source needs a comparison other than its file system's own name equality.

## Failure text is Message or FailureMessage, and TryParse results are named result

**Decision (owner ruling B12, 2026-10-26):** an object that is a failure names its human-readable text `Message`, as
`Exception.Message` and `RootFailure.Message` do. An object that may carry a failure names it `FailureMessage`, as
`RootWatchStatus.FailureMessage` does. `CachedBlockRejection.Reason` is therefore `Message` and
`CachedBlockDeletionResult.FailureReason` is `FailureMessage`. `WindowsVolume.Reason` stays: it explains why a volume
is skipped and is not a failure (B2). `VolumeKey.TryParse(string? text, out VolumeKey result)` names its out parameter
`result`, as `int.TryParse`, `Guid.TryParse` and `IParsable<T>.TryParse` do.

**Reason:** matching the platform's conventions means consumers need not learn per-type names for the same idea.

**Revisit only if:** the platform's own convention for failure text or `TryParse` parameters changes.

## The broker body is an internal test seam

**Decision (owner ruling, 2026-10-27, issue 802):** the broker body is an internal test seam, not a consumer extension point. `IElevatedEntryRunner` is deleted; the internal `ElevatedEntryPoint.RunBrokerIfRequested` overload accepts an `Action<string?>` broker-body delegate, while the public one-argument `ElevatedEntryPoint.RunBrokerIfRequested(string[] arguments)` overload stays unchanged and dispatches to `new DefaultElevatedEntryRunner().RunBroker`.

**Reason:** the single-method interface had one production implementation and existed only so unit tests could substitute the broker body without spawning an elevated process. A consumer that wants its elevated child process to perform application-owned work uses `IElevationProvider` with its own relaunch flags rather than replacing the broker body, and any future tunable of the broker run (such as connect timeout or clock) belongs on a public options parameter on `RunBrokerIfRequested`, not a replaceable body.

**Revisit only if:** a consumer needs a public options parameter on `RunBrokerIfRequested` to tune broker-session execution.

## In-place cache-tag mutation lives in the synthetic block editor

**Decision (owner ruling, 2026-10-27, [issue 801](/schoen/MFTLib/issues/801), [recorded resolution](/schoen/MFTLib/issues/801#issuecomment-144621)):** `BlockWriter.SetCacheTag` is deleted. Production consumers configure cache identity through `FileIndexOptions.CacheTag`, which is written at block creation and on every replacement rescan. No public retag-in-place operation is added to `CacheDirectory` or the samples. `SyntheticBlockEditor.SetCacheTag` keeps its public signature and behavior, writing `CacheTagFourCc` and `CacheTagVersion` directly to the header under a `_block.TakeAccess()` scope.

**Reason:** normal production sets the cache tag fields at block creation (`BlockFile.Create`); the internal writer method had no production caller and existed only so the test package's synthetic editor could rewrite a tag on an existing block.

**Revisit only if:** a concrete production consumer requirement needs in-place cache-tag mutation outside block creation and replacement rescans.

## GrowUsnJournal caps the requested maximum at 8 GiB and 5% of volume total size

**Decision (owner ruling, 2026-10-27, [issue 778](/schoen/MFTLib/issues/778)):** `UsnJournalVolume.GrowUsnJournal`
(and therefore `BrokerSession.GrowUsnJournalAsync`) caps the requested `MaximumSize` at the smaller of 8 GiB
(`8,589,934,592` bytes) and 5% of the volume's total size (`totalBytes / 20`), queried at request time through
`GetDiskSpaceInformationW` (`ActualTotalAllocationUnits * SectorsPerAllocationUnit * BytesPerSector`) with no caching
and ignoring caller quota or free-space fields. A request above that cap is refused before querying or modifying the
journal (`ArgumentOutOfRangeException` on `UsnJournalVolume.GrowUsnJournal`, surfaced as a request-id-correlated
`InvalidOperationException` on `BrokerSession.GrowUsnJournalAsync` without ending the broker session). Broker channel
pipe names must match `mftlib-broker-` plus 32 ASCII hex digits, and scan section names must match
`mftlib-block-<volume>-` plus 32 ASCII hex digits for the channel's volume.

**Reason:** an elevated broker serves any same-user client process; without a volume-relative and absolute cap, a
client request could ask `FSCTL_CREATE_USN_JOURNAL` to reserve an arbitrary journal maximum on the volume, and without
strict prefix-and-hex validation the broker would connect to or map caller-supplied kernel object names outside MFTLib's
own naming scheme.

**Revisit only if:** a consumer workload on large volumes requires a journal maximum above 8 GiB or 5% of the volume.

## One root per FileIndex, and FileIndexSet for multi-index queries

**Decision (owner rulings 2026-10-27, [issue 775](/schoen/MFTLib/issues/775) and [issue 829](/schoen/MFTLib/issues/829)):**
Each `FileIndex` coordinates one root: `FileIndexOptions.Root` (`IndexedRoot?`) replaces `FileIndexOptions.Roots`,
`FileIndex.Status` (`RootStatus`) replaces `FileIndex.Roots`, and `FileIndex.StartWatchingAsync`,
`StopWatchingAsync`, `WaitForCatchUpAsync`, and `RescanAsync` take only a required `CancellationToken` and return
`Task<RootOperationResult>`. `FileIndex.Root(CancellationToken = default)` returns the root `FileEntry` of that index
and throws `InvalidOperationException` (`"Root {root} has no block (state: {state})."`) when the index has no block.
`FileIndexOptions.DriveOpenProgress` and `IndexDriveOpened` are deleted. `IndexedDrive` is renamed to `IndexedRoot`,
`DriveStatus` to `RootStatus`, while `SyntheticIndexedRoot` and `SyntheticNotifications.CreateRootStatus` return `IndexedRoot` and `RootStatus`.

`FileIndexSet` is a public sealed read-only query coordinator over a fixed ordered list of distinct-root `FileIndex`
instances (`new FileIndexSet(IReadOnlyList<FileIndex> indices)`, which rejects null entries and repeated `RootKey`s). It exposes `Indices`, `Find`, `Search`, `Enumerate`,
and `EnumerateRows`; it has no `Root` method (`Root` stays on each `FileIndex`), no lifecycle methods, no events, and
does not implement `IAsyncDisposable` or `IDisposable`. `FileIndexSet` borrows one member's block at a time in `Indices`
order (or the single longest-prefix member for `Find`, or the single matching member for a `Subtree` anchor) and never
holds borrows across two members at once.

Callback reentrancy is one process-wide rule, not a per-index one: a lifecycle call (`StartWatchingAsync`, `StopWatchingAsync`,
`RescanAsync`, `DisposeAsync`, an unsettled `WaitForCatchUpAsync`) from inside a `Changed`, `WatchFaulted` or
`WatchStateChanged` handler of any `FileIndex` throws `InvalidOperationException`, whichever index the call names, because separated indices
whose handlers stop each other would otherwise deadlock both pumps ([contract](watch-lifetime.md)).

**Reason:** every per-root runtime (`RootRuntime`), block (`RootBlock`), cache lock, broker channel, and watch pump
was already independent, while `FileIndex` carried an internal multi-root dictionary, snapshot array, ordinal
assignment, batched verb fan-out, and settle-all open gate. Meanwhile, git-wizard already opened one `FileIndex` per
repository root so warm roots could be queried before cold roots finished scanning, and file-wizard wanted each volume
to become queryable as soon as its own open settled. Making `FileIndex` single-root lets each root open, watch, rescan,
and dispose independently while `FileIndexSet` provides the cross-index read queries (`Find`, `Search`, `Enumerate`,
`EnumerateRows`) that file-wizard and the Watch sample need.

**Revisit only if:** a consumer requires a cross-index atomic snapshot borrow across multiple roots at once.
