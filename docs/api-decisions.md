# Public API decisions

Owner rulings on the shape of the public surface, each with the reason it was made. Read this page before
proposing to remove, move or reshape a public member. A review that finds "MFTLib never calls this itself" or
"this is not an MFT primitive" has found a fact, not a reason: check the ruling here first, and reopen one only
with evidence that its stated reason no longer holds (for example, the consumers' needs have diverged).

Record every new ruling here in the same change that implements or files it: the decision, the reason, and
what would justify revisiting it.

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
reads the clock. A rescan writes fresh observations and clears the flags.

**Reason:** the owner wants fast read-only queries and explicitly chose honest freshness over hidden reads, plus
full `IndexRow` column parity so row loops never need `ToEntry()` just to inspect `LastWriteTime`,
`IsLastWriteTimeKnown`, or `Attributes`. Replacing a scanned last-write value with a rename's
later event time would incorrectly match a recent-write query. Journal-created entries also lack
metadata the journal does not carry, and journal modifications (`DataOverwrite`, `DataExtend`, `DataTruncation`,
`BasicInfoChange`) invalidate previously scanned metadata until the next scan.

**Revisit only if:** consumers explicitly request a separately identified metadata-refresh operation or need
freshness information that the observation/invalidation model cannot express.

## A subtree anchor belongs to the queried index's current drive block

**Decision:**
all three query forms (`Search`, `Enumerate`, and `EnumerateRows`) reject `Subtree = default(FileEntry)` with
`ArgumentException` on call entry. They then validate a non-null `Subtree` anchor, through one shared helper,
against the snapshot actually borrowed by the query. They throw `ObjectDisposedException` when the anchor's block
has been released, and `ArgumentException` when the anchor belongs to another `FileIndex`, belongs to a replaced
drive block, or has been superseded by row reuse. A retained handle remains readable against its original block
until disposed, and rescanning another drive does not invalidate an anchor whose own drive block remains current;
the drive block is compared, not the whole snapshot. Callers reacquire an anchor from the current index for a new
query after the anchor's drive is rescanned.

**Reason:** private drive ordinals and row numbers are coordinates, not ownership, and `default(FileEntry)` is
not a valid subtree anchor. The audit reproduced a C-drive root selecting D-drive rows in another index,
including after the first index was disposed, as well as an old directory handle selecting a different directory
at the same row after rescan and `default(FileEntry)` silently returning an empty result. Comparing whole
snapshot identity would overcorrect when another drive rescans while the anchor's drive block stays current.

**Revisit only if:** consumers need an explicitly specified rebinding operation that verifies directory identity
across block replacement.

## One producer enum, selection and observation

**Decision:** `IndexedDrive.Producer` selects a `ProducerKind` per drive for initial scans and
rescans, defaulting to `ProducerKind.Mft`. The same enum identifies the producer in `IndexRecordKey.ProducerKind`,
`CachedBlockStatus.ProducerKind` and the block header. Its underlying type is `uint`, with `Mft = 1` and
`Enumeration = 2`. Construction, init setters and `FileIndex.OpenAsync` admission reject the default and every
undefined value before filesystem side effects. Warm adoption compares the selected value directly with the
stored value; a mismatch cold-scans, or reports `CacheDeclined` for `CacheMode.ExistingOnly`.

**Reason:** selection and observation describe the same producer. One enum gives consumers one vocabulary and
lets a cache inspection value select a drive's producer directly, while preserving block bytes and per-drive
choice. An MFT failure never selects a directory walk; enumeration ignores `MftSource` and runs only by request.

**Revisit only if:** producer selection needs a choice that cannot identify a block's actual producer.

## OpenAsync validates drive keys and producer choices before filesystem side effects

**Decision:**
`IndexedDrive` validates `Drive`, `RootDirectory` (`ArgumentException.ThrowIfNullOrWhiteSpace`), and `Producer`
(`Enum.IsDefined`, throwing `ArgumentOutOfRangeException`) on construction and in its `init` setters.
`FileIndex.OpenAsync` materializes `FileIndexOptions.Drives` once and validates it up front, before creating
`CacheDirectory` or running any producer: a null `Drives` collection, a null or invalid element, or duplicate
normalized `DriveKey` entries throw `ArgumentException`, and an undefined `ProducerKind` value throws
`ArgumentOutOfRangeException`.

**Reason:** `OpenAsync` is a dictionary-keyed, per-drive API (`Drives`, `Root(DriveKey)`), so duplicate drive
keys, blank roots, and undefined enum values are caller configuration bugs that must fail fast with no filesystem
side effects.

**Revisit only if:** never; configuration validation before side effects is a baseline contract.

## Cold production failures settle per drive across both producers

**Decision:**
a cold-production failure on a `ProducerKind.Enumeration` drive settles that drive as `DriveState.Failed` with
`DriveFailureKind.ProducerFailed` while healthy sibling drives remain usable, matching `ProducerKind.Mft` and
`RescanAsync`. A failed rescan preserves the previous block and reports `DriveStatus.Failure` with
`DriveFailureKind.ProducerFailed`. Argument validation performed at admission (null options, an undefined
`CacheMode`, a missing cache directory, dump-source option conflicts) throws before any drive is produced. A drive
whose production cannot start or fails for an operational reason, including an MFT drive that needs a scan when
`FileIndexOptions.MftSource` is null, settles as `DriveState.Failed` with `DriveFailureKind.ProducerFailed` while
sibling drives remain usable. Caller cancellation (`OperationCanceledException`) aborts the whole operation, and a
cancelled `OpenAsync` releases every adopted block.

**Reason:** `IndexedDrive.Producer` is configured per drive so one index can mix MFT-scanned and directory-walked
drives. An operational production failure must have the same per-drive isolation regardless of the selected
producer, both during initial open and during rescan. Missing drives settle Offline, and enumeration skips
missing or inaccessible subtrees; neither is an operational producer failure by itself.

**Revisit only if:** a consumer explicitly asks for an all-or-nothing open mode across all drives.

## FileEntry handles are bound to the row's sequence number

**Decision:**
`FileEntry` captures the row's `ushort` sequence number at construction, in the tail padding of its 16-byte
struct (`Snapshot?`, `uint` row index, `ushort` drive ordinal, `ushort` sequence number; `BlockFile` layout is
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
Equality (`Equals`, `GetHashCode`, `==`, `!=`) compares the underlying drive block, row index and sequence
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
The public elevation helper and provider remain the shared consumer API. The timeout field's
visibility is governed by the sample-caller audit decision below.

**Revisit only if:** one consumer needs elevation behavior the other cannot share.

## Sample-caller audit: helper policy and harness-owned observations

**Decision:**
`ElevationUtilities.DefaultElevatedTimeout` is internal. `InProcessBrokerScan` is a receive-only
record with an explicit internal constructor, public init-only `Drive` and `RetainedFileNames`
properties, and no positional deconstruction. Drive validation applies on construction and init.
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
`BrokerSession.GrowUsnJournalAsync(drive, target, cancellationToken)` takes a `UsnJournalSettings` target.
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

## The enumeration producer is a product feature, chosen per drive

**Decision (API review finding F6):** the directory-walk producer stays public. It is how an index
is built where the MFT is not available: Linux, non-NTFS volumes, and a run without elevation. The producer
choice belongs to each drive, not to the whole open, so one index can hold an MFT-scanned drive beside a
directory-walked one.

**Reason:** the review found no shipping caller and recommended moving the producer into the test package. The
absence of a caller was a gap in the consumers, not the intent: file-wizard documents the producer but exposes
no way to select it, and git-wizard
carries a separate hand-rolled walk.

**Rule:** a drive whose MFT scan fails is `Failed`; it is never rebuilt by a directory walk on the
caller's behalf. Enumeration runs only for a drive that asks for it.

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

## A dump source describes its own drive and cache

**Decision (API review finding F10):** a dump source is fully self-describing, and there is exactly one
way to write a dump open: set `FileIndexOptions.MftSource` and nothing else about drives or cache. The source
supplies its one virtual drive (letter, root, serial zero, `ProducerKind.Mft`), which `FileIndex.OpenAsync` adds
to the index. `Drives` must be empty, and `CacheMode` (every value, `Disabled` included), `CacheDirectory` and
`CacheTag` must be unset; setting any of them throws `ArgumentException` naming it.
`MftIndexSources.FromLocalVolumes`, `MftIndexSources.FromMftDumpFile` and `MftScanOptions.IncludeFreed`
are public, sample-reached features.

**Reason:** the caller had to restate facts the source already knows (the letter, the dump root, serial zero, an
uncached open), and the library threw when any part of the restatement was off. A setting that can only be right in
one way is not a setting.

**Revisit only if:** a dump gains a legitimate cached form, for example a persisted scan of an image.

## One key type for drives, one method per lifecycle verb

**Decision (API review findings F7 and F8):** the `DriveKey` struct identifies drives
across the public surface. It normalizes case
once, converts implicitly from `char` and never from `string`. Each lifecycle verb of `FileIndex` (start
watching, stop watching, wait for catch-up, rescan) is one method taking `IEnumerable<DriveKey>` and returning
one `DriveOperationResult` per requested drive, with no overloads.

**Reason:** the letter was the key of the whole API with no owner: case rules, parameter types and collection
types differed from member to member, both consumers up-cased what the library returned, and the four verbs had
an uneven set of single, list and all-drives overloads. A bare `IEnumerable<char>` would accept a `string`
("CD" as two drives), which is why the key has its own type. The struct is also the one place a key could later
become something other than an ASCII letter for Linux and enumeration roots.

**Deferred on purpose:** whether a key may be a label rather than a letter.

**Implementation note:** the default struct value is invalid; its `Letter` and `ToString`
and public key-accepting operations reject it with `ArgumentException`. Key-bearing record constructors
and `Drive` init accessors validate it too. Members carrying keys are named `Drive`; the letter accessor
on the key itself is `Letter`. Lifecycle requests are materialized once, reject duplicates before work
starts and preserve request order, matching the list contract. Empty sequences succeed with no results
unless cancelled. These choices keep identity and admission in one owner without adding public validation
members or a string conversion.

## A boolean that reports state takes the Is prefix

**Decision (API review finding F15, naming half):** a public boolean property that reports state on a
status or result type takes the `Is`, `Has` or `Can` prefix: `DriveWatchStatus.IsSupported`, `IsRequested` and
`IsRecoveryStopped`, `JournalCatchUpLostException.IsRecoveryStopped`, `DriveBlockStatus.IsCompactionNeeded` and
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
and `ExpectedRowCount`, and `SearchQuery.SizeMinimum` and `SizeMaximum`. A parameter that takes a `DriveKey` is
named `drive` (or `drives` for a sequence), never `driveLetter`.

**Reason:** the surface named the same concept several ways, so a reader had to learn each type's word for a
last write time, a scan time, a size, a count and a query bound. The test package had dropped the `Utc` suffix
without aligning to the production names, and no record shows the difference was chosen. Names that mirror an
NTFS structure (`UsnJournalSettings` and the test package's `SyntheticJournalWindow.MaximumSize`) are outside
the rule.

**Revisit only if:** a new concept has no existing word, or an NTFS structure's own name conflicts with the rule.

## Raw block-header inspection is internal test support

**Decision:** `SyntheticIndexInspection` and `SyntheticDriveHeader` are internal to
`MFTLibTestExtensions`, available to `MFTLib.Tests` through its existing friend-assembly access.
Consumer tests read `index.Root(drive).RecordKey.ProducerKind` for the producer and
`status.Block!.LiveRowCount` for live inventory. `DriveBlockStatus.ScanTimestamp` supplies the stored scan time.

**Reason:** the only consumer uses are two git-wizard assertions already answered by the index.
Raw header row counts include unused slots, whereas live row counts express the inventory assertion.
file-wizard has no consumer uses outside its MFTLib submodule.

**Revisit only if:** a consumer test needs a block-header observation the public index cannot answer.

## Every way to obtain an index source is on MftIndexSources

**Decision (API review finding F19):** `MftIndexSources.Unavailable(reason)` supplies an unavailable
source. `MftIndexSource` keeps no public factory, so the
flat `MFTLib` namespace holds every way to obtain a source: `BrokerSession.CreateIndexSource`,
`MftIndexSources.FromLocalVolumes`, `MftIndexSources.FromMftDumpFile` and `MftIndexSources.Unavailable`.

**Reason:** source factories belong together. The unavailable factory carries the
application's user-facing reason into `DriveFailure.Message`: on every path a
null source and an unavailable source have the same outcome (the drive fails with `ProducerFailed`, a batched
watch start reports `NotApplicable`) and only the message differs, so one slot holds either a real source or
the reason there is none.

**Revisit only if:** the index reports a producer-less drive's reason some other way.

## A method named Try returns quickly

**Decision (API review finding F23):** `ElevatedEntryPoint.RunBrokerIfRequested(arguments)`
is the broker-mode dispatch method.

**Reason:** the method runs the broker for its whole life when the process was started with the broker flag,
exits the process with code 1 on rejected arguments, and otherwise returns false at once. A `Try` name in .NET
promises a quick attempt that reports failure by returning false, which does not describe a broker run.

**Revisit only if:** the method stops blocking for the broker's lifetime.

## Names of the two index progress options, and what stays as it is

**Decision (API review finding F21):** `FileIndexOptions.ScanProgress` reports scan
progress samples only while a scan runs; `FileIndexOptions.DriveOpenProgress` reports one
`IndexDriveOpened` per drive that settles during open, warm starts included. `BrokerSession.Connecting`
stays an event. The mix of events, `IProgress` options and the `Diagnostics` callback stays, because each style
follows what it reports.

**Reason:** each option's name identifies the operation it reports.
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

## Open progress counts settle order and total

**Decision (API review finding F13):** `IndexDriveOpened` keeps both `SettledDriveCount` and
`TotalDriveCount`. The Watch sample prints one line per settled drive, so the notification has a sample
caller.

**Reason:** the notification was built to order for file-wizard's "drive k of N" display during warm starts. Drives settle
concurrently, so the settle counter is what lets a handler order overlapping reports, and the total keeps each
report self-describing. A single consumer is the design, not drift. The gap the review found was that no sample
showed it.

**Revisit only if:** file-wizard stops rendering open progress.

## A state enum plus a nullable reason

**Decision (API review finding F14):** a state enum is the field callers branch
on; the reason or error that accompanies it is a nullable property, non-null only when there is something to
report; a reason enum has no "nothing wrong" member. `CachedBlockStatus.Validation` is null unless
`Availability` is `Invalid`, and `BlockValidationResult` has no `Valid`. `DriveBlockStatus.CacheSlot` is a
nullable `CacheSlotState`, null when the block is not backed by a cache slot, and `CacheSlotState` has no
`NotApplicable`.

**Reason:** two fields stating one fact can contradict each other, and both consumers branch on the state enum
and only print the reason. Six of the eight state-plus-reason pairs on the public surface already had this
shape: `DriveStatus.Failure`, `WatchStateChange.Fault`, `DriveOperationResult.Failure`,
`CachedBlockDeletionResult.FailureReason`, `IndexScanProgress.Outcome` and `DriveWatchStatus.FailureMessage`.

**Revisit only if:** a consumer needs to distinguish "checked and valid" from "not checked" on a row it cannot
already distinguish by the state enum.

## One cache inspection method, documented directory preparation

**Decision (API review finding F20):** `CacheDirectory.InspectCached`
is one public method with optional `IEnumerable<DriveKey>? drives = null`
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

## Cache deletion reports per-file outcomes through results

**Decision:**
`CacheDirectory.DeleteCached(string cacheDirectoryPath, IEnumerable<DriveKey>? drives = null)`
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

## File entries expose their drive and keep distinct state questions

**Decision (API review finding F24):** `FileEntry.Drive` is a
`DriveKey` equal to `RecordKey.Drive`, with the same default-value and disposal
behavior as the entry's other data properties. `FileIndex.Drives` keeps its name.
`FileEntry.IsValid` and `IsDisposed` stay separate.

**Reason:** `IndexRow` already exposes `Drive`, while both consumers repeatedly
read `FileEntry.RecordKey.Drive`. Direct access removes that repeated two-hop
read. The collection remains `Drives` under the owner's no-change ruling.
The entry's state properties answer different questions: `IsValid` says whether
the handle references a snapshot, `IsDisposed` says whether that snapshot has
been released, and `IsDeleted` identifies a retained tombstone. A disposed handle
can still be valid; a default handle is neither valid nor disposed.

The Watch sample reads `change.Entry.Drive` when printing each change. The getter
throws `InvalidOperationException` for a default entry and
`ObjectDisposedException` after its snapshot is released. Consumer expression
replacements belong to their pin-bump pull requests.

**Revisit only if:** concrete consumer requirements change drive identity or the
distinct meanings of these state questions.

## Hand-out watch events and operation results are library-constructed

**Decision (API review finding F22):** `WatchStateChange` and
`DriveOperationResult` follow the receive-only record rule: explicit internal
constructors, public init-only properties, and no positional `Deconstruct`.
Tests construct them through `SyntheticNotifications.CreateWatchStateChange`
and `SyntheticNotifications.CreateDriveOperationResult` in `MFTLib.TestExtensions`.

**Reason:** the library produces these events and operation verdicts. The
consumer review found no production need to construct them. file-wizard's
snapshot seeding passes the drive, `DriveWatchStatus`, and nullable fault to
its own merge/presentation method instead of wrapping a snapshot in an event.
Its cache-only host reports its own refusal instead of fabricating a library
result. Forwarding that refusal to `RescanAsync` was rejected because it would
touch the cache file and rewrite the drive's failure kind.

Consumer pin-bump pull requests migrate those production callers and replace
synthetic events/results in consumer tests with the factories. The factories
forward to the internal constructors, retaining the same drive-key validation.
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
the `ScriptedBrokerVolumes.ReadJournal` and `WatchDrive` delegates. No public type
lists a base type or interface that is not public: `ScriptedWatchSource` and
`ScriptedDriveWatch` reach the index through an internal adapter instead of
implementing `IIndexWatchSource` and `IIndexDriveWatch`.

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

## One failure value and a nullable block on DriveStatus

**Decision (API review finding F2):**
`DriveStatus.Failure` is a `DriveFailure` (`Kind`, non-null `Message`), non-null exactly when the drive is
`Failed`, or `Ready` retaining a block after a failed `RescanAsync` or a failed catch-up retry scan during
`OpenAsync` (kind `ProducerFailed`, the producer's message). A successful scan publication clears it and an
`Offline` drive has none. `DriveStatus.Block` is null unless `State` is `Ready`.
`DriveFailureKind` contains failure reasons and `BlockSource` identifies an actual source;
absence is represented by the nullable containing value. A failure message is required
where it is produced; there is no empty-string fallback.

**Reason:** "did this drive fail, and why" took three fields whose combinations were not independent: a `Ready`
drive could carry a rescan failure message with kind `None`. A blockless drive exposed a block of zeros that
git-wizard detected by testing `ScanTimestamp == default`, and both consumers recombined the fields with their own
fallback text. This is the first instance of the state-plus-nullable-reason rule above.

**Revisit only if:** a consumer needs a failure that is not tied to one drive.

## One watch snapshot type, named for what it holds

**Decision (API review finding F3):**
the enum is `WatchState`; its members `NotStarted`, `Recovering` and `Faulted`
describe the watch lifecycle, and `DriveWatchStatus.State` is its property. `FileIndex.WatchStateChanged` is an
`Action<WatchStateChange>`; `WatchStateChange` carries `DriveKey Drive`, the same
`DriveWatchStatus` snapshot that `DriveStatus.Watch` reports (`Watch`), and the optional `Fault`. One builder produces
the snapshot for both `Drives` and the event, so they cannot diverge.

**Reason:** snapshots and events report the same state and version, so consumers can seed
their history without converting between shapes. Names distinguish the snapshot, event and enum.

**Revisit only if:** an event needs data that the snapshot does not carry.

## MftScanOptions configures every MFT scan

**Decision (API review finding F4):**
`MftScanOptions` configures every MFT scan: broker-backed through
`BrokerSession.CreateIndexSource(scanOptions)` and in-process through `MftIndexSources.FromLocalVolumes(scanOptions)`.
Its `RetainedFileNames` is null to retain every eligible record, empty to
retain directories only, or nonempty to retain directories plus files whose names match ordinally, ignoring case.
The test package's `InProcessBrokerScan.RetainedFileNames` follows the same convention.

**Reason:** scan options apply independently of the transport, and retention names describe
the MFT filter rather than a directory walk (`ProducerKind.Enumeration`).
git-wizard is the production caller of the filter.

**Revisit only if:** a scan setting applies to one transport only.

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

**Decision (API review findings F17 and F18):**
a public XML comment refers only to public members and states the contract the code has. `MftIndexSource` names
`BrokerSession.CreateIndexSource` and `MftIndexSources`, `JournalCheckpointLoss.SizeThatWouldHaveRetained` states that
the library reports the size and the consumer owns the growth decision, and
`BrokerDiagnostics` documents the public diagnostics configuration. A broker failure
does not fall back to a direct scan. `FileIndex.Root` documents its
`ArgumentException`, `Find` documents that null means no entry, and `Search` and `Enumerate` document
`ArgumentNullException` for a null query. `FileIndex.Root` throws `ArgumentException` with "Drive X has no block
(state: Offline)." (the drive's current `DriveState`) for a configured drive without a block, and keeps
"Drive X is not part of this index." for an unconfigured drive.

**Reason:** a reader following public documentation landed on types they cannot see, and the "not part of this
index" message was false for a drive configured in `FileIndexOptions.Drives` but `Offline` or `Failed`, hiding the
state the caller needs. Both git-wizard `Root` call sites can meet such a drive.

**Revisit only if:** `Root` gains a non-throwing form for a drive without a block.

## A drive watch's pump owns fault attribution

**Decision:** `IIndexDriveWatch` supplies a read stream and asynchronous disposal; it carries no
`Drive`. `FileIndex` attributes batches and faults to the `DriveRuntime` that started the pump, and
`DriveWatchFaultException` carries only a message and optional inner exception to classify a
`WatchFaultKind.Drive` fault. Consumers read `WatchFault.Drive`.

**Reason:** the pump already owns the authoritative drive; repeating it on the handle and the marker
exception informed nobody.

**Revisit only if:** one watch stream can represent several independently owned drive runtimes.
