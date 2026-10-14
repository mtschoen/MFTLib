# Public API decisions

Owner rulings on the shape of the public surface, each with the reason it was made. Read this page before
proposing to remove, move or reshape a public member. A review that finds "MFTLib never calls this itself" or
"this is not an MFT primitive" has found a fact, not a reason: check the ruling here first, and reopen one only
with evidence that its stated reason no longer holds (for example, the consumers' needs have diverged).

Record every new ruling here in the same change that implements or files it: the decision, the reason, and
what would justify revisiting it.

## Metadata reports observations and freshness without hidden reads

**Decision (2026-10-14, implementation pending
[MFTLib issue 503 (metadata freshness)](https://gitea.fleet.sticktoitive.net/schoen/MFTLib/issues/503)):**
the index reports the filesystem metadata it actually observed and its freshness, without implicit filesystem
reads to refresh it. A journal event's timestamp is event time (`FileChange.Timestamp`), not a filesystem
last-write observation. `RowFlags.LastWriteTimeUnknown = 32` tracks unknown last-write time alongside
`RowFlags.SizeUnknown = 8`. Both `FileEntry` and `IndexRow` expose `IsLastWriteTimeKnown`, and `IndexRow`
exposes `LastWriteTime` and `Attributes` for full row-column parity with `FileEntry`. Rows with
`LastWriteTimeUnknown` read `LastWriteTime == DateTime.MinValue` and are excluded when `SearchQuery` specifies
`LastWriteTimeMinimum` or `LastWriteTimeMaximum`, matching `SizeUnknown`.

**Reason:** the owner wants fast read-only queries and explicitly chose honest freshness over hidden reads, plus
full `IndexRow` column parity so row loops never need `ToEntry()` just to inspect `LastWriteTime`,
`IsLastWriteTimeKnown`, or `Attributes`. The audit reproduced a rename replacing a scanned 2020 last-write value
with the rename's 2026 event time, which then matched a recent-write query. Journal-created entries also lack
metadata the journal does not carry, and journal modifications (`DataOverwrite`, `DataExtend`, `DataTruncation`,
`BasicInfoChange`) invalidate previously scanned metadata until the next scan. Until implemented, the current
journal mutation does not meet this ruling.

**Revisit only if:** consumers explicitly request a separately identified metadata-refresh operation or need
freshness information that the observation/invalidation model cannot express.

## A subtree anchor belongs to the queried index's current drive block

**Decision (2026-10-14, implementation pending
[MFTLib issue 504 (subtree identity)](https://gitea.fleet.sticktoitive.net/schoen/MFTLib/issues/504)):**
all three query forms (`Search`, `Enumerate`, and `EnumerateRows`) eagerly validate a non-null `Subtree` anchor
on call entry against the snapshot actually borrowed by the query. They throw `ObjectDisposedException` when the
anchor's block has been released, and `ArgumentException` when the anchor is `default(FileEntry)`, belongs to
another `FileIndex`, belongs to a replaced drive block, or has been superseded by row reuse. A retained handle
remains readable against its original block until disposed, and rescanning another drive does not invalidate an
anchor whose own drive block remains current.

**Reason:** private drive ordinals and row numbers are coordinates, not ownership, and `default(FileEntry)` is
not a valid subtree anchor. The audit reproduced a C-drive root selecting D-drive rows in another index,
including after the first index was disposed, as well as an old directory handle selecting a different directory
at the same row after rescan and `default(FileEntry)` silently returning an empty result. Comparing whole
snapshot identity would overcorrect when another drive rescans while the anchor's drive block stays current.
Until implemented, the current ordinal-only checks do not meet this ruling.

**Revisit only if:** consumers need an explicitly specified rebinding operation that verifies directory identity
across block replacement.

## OpenAsync validates drive keys and producer policies before filesystem side effects

**Decision (2026-10-14, implementation pending
[MFTLib issue 505 (OpenAsync input validation)](https://gitea.fleet.sticktoitive.net/schoen/MFTLib/issues/505)):**
`FileIndex.OpenAsync` validates `FileIndexOptions.Drives` up front, before creating `CacheDirectory` or running
any producer: duplicate normalized `DriveKey` entries throw `ArgumentException`, and an undefined
`ProducerPolicy` value throws `ArgumentOutOfRangeException`.

**Reason:** `OpenAsync` is a dictionary-keyed, per-drive API (`Drives`, `Root(DriveKey)`), so duplicate drive
keys and undefined enum values are caller configuration bugs that must fail fast with no filesystem side effects.

**Revisit only if:** never; configuration validation before side effects is a baseline contract.

## Cold production failures settle per drive across both producer policies

**Decision (2026-10-14,
[MFTLib issue 506 (per-drive enumeration settlement)](https://gitea.fleet.sticktoitive.net/schoen/MFTLib/issues/506)):**
a cold-production failure on a `ProducerPolicy.Enumeration` drive settles that drive as `DriveState.Failed` with
`DriveFailureKind.ProducerFailed` while healthy sibling drives remain usable, matching `ProducerPolicy.Mft` and
`RescanAsync`. A failed rescan preserves the previous block and reports `DriveStatus.Failure` with
`DriveFailureKind.ProducerFailed`. Argument validation performed at admission (null options, an undefined
`CacheMode`, a missing cache directory, dump-source option conflicts) throws before any drive is produced. A drive
whose production cannot start or fails for an operational reason, including an MFT drive that needs a scan when
`FileIndexOptions.MftSource` is null, settles as `DriveState.Failed` with `DriveFailureKind.ProducerFailed` while
sibling drives remain usable. Caller cancellation (`OperationCanceledException`) aborts the whole operation, and a
cancelled `OpenAsync` releases every adopted block.

**Reason:** `ProducerPolicy` is configured per drive so one index can mix MFT-scanned and directory-walked
drives. An operational production failure must have the same per-drive isolation regardless of the selected
producer, both during initial open and during rescan. Missing drives settle Offline, and enumeration skips
missing or inaccessible subtrees; neither is an operational producer failure by itself.

**Revisit only if:** a consumer explicitly asks for an all-or-nothing open mode across all drives.

## FileEntry handles are bound to the row's sequence number

**Decision (2026-10-14, implementation pending
[MFTLib issue 507 (FileEntry row-reuse identity)](https://gitea.fleet.sticktoitive.net/schoen/MFTLib/issues/507)):**
`FileEntry` captures the row's `ushort` sequence number in its existing 16-byte struct layout (`BlockFile?`,
`uint RowIndex`, `byte DriveOrdinal`, `ushort SequenceNumber`). When USN journal creation reuses a deleted row
slot with a new sequence number, or when `EnumerationProducer` reuses a deleted slot and increments its
sequence number, previously retained `FileEntry` handles with the older sequence number do not silently
alias the new file: `IsDeleted` reads `true`, `Open` throws `FileNotFoundException`, `Children` returns empty,
`Subtree` queries reject the superseded anchor with `ArgumentException`, and reading overwritten row columns
(`Name`, `Path`, `Parent`, `IsDirectory`, `Size`, `IsSizeKnown`, `LastWriteTime`, `IsLastWriteTimeKnown`,
`Attributes`) throws `InvalidOperationException`.

**Reason:** NTFS reuses MFT segment numbers when creating new files after deletions. Because `FileEntry` already
has 2 bytes of tail padding in its 16-byte layout and `BlockFile.SequenceNumbers` already stores a `ushort` per
row, binding `FileEntry` to the row's sequence number prevents silent data corruption (opening or querying the
wrong file through a retained handle) at zero struct-size cost.

**Revisit only if:** consumers require cached tombstone snapshots of overwritten columns on superseded handles
beyond `IsDeleted`.

## Elevation helpers live in MFTLib

**Decision (2026-10-13, API review finding F9):** `IElevationProvider` and `ElevationUtilities` stay public in
MFTLib, unchanged.

**Reason:** both consumers had their own elevation code, it turned out to be the same code, and it was hoisted
here so it exists once. The 2026-10-12 API review recommended moving it back out because the library does not
call it; a comparison of the consumers on 2026-10-13 found their use identical, so removal would only recreate
the duplication:

- Both decide broker-backed versus in-process scanning with `IsElevated()` and `CanSelfElevate()`, which is a
  decision about MFT scanning.
- Both add Windows Defender process exclusions with the same sequence: already elevated, run directly; able to
  self-elevate, `TryRunElevated`; otherwise an elevated PowerShell.
- The samples self-elevate through the same provider ([elevation](elevation.md)).

The broker launcher does not use `TryRunElevated`, on purpose: that call waits for the elevated child to exit,
and the broker has to keep running on its pipe. That is not evidence the helper is unused.

**Implementation (2026-10-13, [MFTLib issue 472](https://gitea.fleet.sticktoitive.net/schoen/MFTLib/issues/472)):**
the Defender sequence is `ElevationUtilities.TryAddDefenderProcessExclusions(processNames, relaunchArguments, elevation = null)`,
one method on the existing type; [elevation](elevation.md#windows-defender-process-exclusions) holds its contract.

**Revisit only if:** one consumer needs elevation behavior the other cannot share.

## Journal retention sizing is reported by the library; growth is a consumer decision

**Decision (2026-10-13, API review finding F12,
[MFTLib issue 488](https://gitea.fleet.sticktoitive.net/schoen/MFTLib/issues/488)):**
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

**Decision (2026-10-13, API review finding F6):** the directory-walk producer stays public. It is how an index
is built where the MFT is not available: Linux, non-NTFS volumes, and a run without elevation. The producer
choice belongs to each drive, not to the whole open, so one index can hold an MFT-scanned drive beside a
directory-walked one ([MFTLib issue 468](https://gitea.fleet.sticktoitive.net/schoen/MFTLib/issues/468)).

**Reason:** the review found no shipping caller and recommended moving the producer into the test package. The
absence of a caller was a gap in the consumers, not the intent: file-wizard documents the producer but exposes
no way to select it
([file-wizard issue 561](https://gitea.fleet.sticktoitive.net/schoen/file-wizard/issues/561)), and git-wizard
carries a separate hand-rolled walk
([git-wizard issue 311](https://gitea.fleet.sticktoitive.net/schoen/git-wizard/issues/311)). The whole-open
policy predated the per-drive refactor.

**Unchanged rule:** a drive whose MFT scan fails is `Failed`; it is never rebuilt by a directory walk on the
caller's behalf. Enumeration runs only for a drive that asks for it.

**Revisit only if:** both consumers decide against indexing anything but elevated NTFS volumes.

## One cache mode, not two booleans

**Decision (2026-10-13, API review finding F15, cache half):** `FileIndexOptions.CacheMode`, a nullable enum with
`Persistent`, `ExistingOnly` and `Disabled`, replaces `NoCache` and `InitialOpenCacheOnly`. `Persistent` warm-starts
from the cache directory, scans what is missing and writes blocks back. `ExistingOnly` opens only from existing
blocks and never scans. `Disabled` reads and writes no cache directory; its blocks are private and deleted on
close. `CacheDirectory` is required and nonblank for `Persistent` and `ExistingOnly` and ignored for `Disabled`.
Unset resolves to `Disabled` for a dump source and `Persistent` otherwise.

**Reason:** the two booleans described one choice and could contradict each other (no cache, and open only from
cache), a combination with no meaning that a normal open accepted. Each consumer had rebuilt a single cache mode on
top of the pair. One value cannot express the contradiction.

**Revisit only if:** a fourth way to use the cache appears that is not one of the three.

## A dump source describes its own drive and cache

**Decision (2026-10-13, API review finding F10):** a dump source is fully self-describing, and there is exactly one
way to write a dump open: set `FileIndexOptions.MftSource` and nothing else about drives or cache. The source
supplies its one virtual drive (letter, root, serial zero, `ProducerPolicy.Mft`), which `FileIndex.OpenAsync` adds
to the index. `Drives` must be empty, and `CacheMode` (every value, `Disabled` included), `CacheDirectory` and
`CacheTag` must be unset; setting any of them throws `ArgumentException` naming it. The first half of F10 is ruled as
no change: `MftIndexSources.FromLocalVolumes`, `MftIndexSources.FromMftDumpFile` and `MftScanOptions.IncludeFreed`
stay public as sample-reached features, as owner decisions 17, 20, 22, 23 and 25 intended when they were added.

**Reason:** the caller had to restate facts the source already knows (the letter, the dump root, serial zero, an
uncached open), and the library threw when any part of the restatement was off. A setting that can only be right in
one way is not a setting.

**Revisit only if:** a dump gains a legitimate cached form, for example a persisted scan of an image.

## One key type for drives, one method per lifecycle verb

**Decision (2026-10-13, API review findings F7 and F8, ruled together):** a `DriveKey` struct replaces the bare
`char` drive letter (and the test package's `string` letters) across the public surface. It normalizes case
once, converts implicitly from `char` and never from `string`. Each lifecycle verb of `FileIndex` (start
watching, stop watching, wait for catch-up, rescan) is one method taking `IEnumerable<DriveKey>` and returning
one `DriveOperationResult` per requested drive, with no overloads
([MFTLib issue 470](https://gitea.fleet.sticktoitive.net/schoen/MFTLib/issues/470)).

**Reason:** the letter was the key of the whole API with no owner: case rules, parameter types and collection
types differed from member to member, both consumers up-cased what the library returned, and the four verbs had
an uneven set of single, list and all-drives overloads. A bare `IEnumerable<char>` would accept a `string`
("CD" as two drives), which is why the key has its own type. The struct is also the one place a key could later
become something other than an ASCII letter for Linux and enumeration roots.

**Deferred on purpose:** whether a key may be a label rather than a letter.

**Implementation note (2026-10-13):** the default struct value is invalid; its `Letter` and `ToString`
and public key-accepting operations reject it with `ArgumentException`. Key-bearing record constructors
and `Drive` init accessors validate it too. Members carrying keys are named `Drive`; the letter accessor
on the key itself is `Letter`. Lifecycle requests are materialized once, reject duplicates before work
starts and preserve request order, matching the list contract. Empty sequences succeed with no results
unless cancelled. These choices keep identity and admission in one owner without adding public validation
members or a string conversion.

## A boolean that reports state takes the Is prefix

**Decision (2026-10-13, API review finding F15, naming half):** a public boolean property that reports state on a
status or result type takes the `Is`, `Has` or `Can` prefix: `DriveWatchStatus.IsSupported`, `IsRequested` and
`IsRecoveryStopped`, `JournalCatchUpLostException.IsRecoveryStopped`, `DriveBlockStatus.IsCompactionNeeded` and
the test package's `PowerShellLaunch.IsElevated`. A boolean option that reads as an instruction takes no prefix
(`CaseSensitive`, `IncludeDeleted`, `IncludeFreed`, `CatchUpOnStart`). `BooleanNamingTests` enforces the rule
by reflection over the compiled MFTLib and MFTLibTestExtensions assemblies, with an explicit allowlist of the option types (`MftScanOptions`,
`SearchQuery` and `ScriptedWatchSource`)
([MFTLib issue 477](https://gitea.fleet.sticktoitive.net/schoen/MFTLib/issues/477)).

**Reason:** thirteen state flags already took `Is` and six did not, so a reader could not tell a state flag from
an option by its name, and one status record mixed both styles. The framework guideline leaves the prefix
optional, so this is a house rule for consistency, not a correction of an error. The allowlist names types
rather than matching a suffix because `SearchQuery` and `ScriptedWatchSource` do not end in `Options`.

**Revisit only if:** a public type needs a boolean that is neither state nor an instruction.

## One name per concept across the public surface

**Decision (2026-10-13, API review finding F16):** a file's last write is `LastWriteTime`, a scan's completion
is `ScanTimestamp`, a journal event's time is `Timestamp`, a byte size is `Size` with no unit suffix, a count
ends in `Count`, and a bound is the property name followed by `Minimum` or `Maximum`. Applied in MFTLib and
MFTLibTestExtensions: `SyntheticRow.LastWriteTime`, `ScanTimestamp` on `SyntheticBlockOptions`,
`SyntheticDriveHeader` and `SyntheticMftProducer`, `CachedBlockFile.Size`, `IndexScanProgress.WrittenRowCount`
and `ExpectedRowCount`, and `SearchQuery.SizeMinimum` and `SizeMaximum`. A parameter that takes a `DriveKey` is
named `drive` (or `drives` for a sequence), never `driveLetter`
([MFTLib issue 479](https://gitea.fleet.sticktoitive.net/schoen/MFTLib/issues/479)).

**Reason:** the surface named the same concept several ways, so a reader had to learn each type's word for a
last write time, a scan time, a size, a count and a query bound. The test package had dropped the `Utc` suffix
without aligning to the production names, and no record shows the difference was chosen. Names that mirror an
NTFS structure (`UsnJournalSettings` and the test package's `SyntheticJournalWindow.MaximumSize`) are outside
the rule.

**Revisit only if:** a new concept has no existing word, or an NTFS structure's own name conflicts with the rule.

## Every way to obtain an index source is on MftIndexSources

**Decision (2026-10-13, API review finding F19):** `MftIndexSources.Unavailable(reason)` replaces
`MftIndexSource.Unavailable(reason)`, with the same contract. `MftIndexSource` keeps no public factory, so the
flat `MFTLib` namespace holds every way to obtain a source: `BrokerSession.CreateIndexSource`,
`MftIndexSources.FromLocalVolumes`, `MftIndexSources.FromMftDumpFile` and `MftIndexSources.Unavailable`
([MFTLib issue 480](https://gitea.fleet.sticktoitive.net/schoen/MFTLib/issues/480)).

**Reason:** the factory shipped on the type two days before the factory class existed, and no record gave a
reason to leave it there. It stays a factory rather than becoming a null `FileIndexOptions.MftSource` because
the factory exists to carry the application's user-facing reason into `DriveFailure.Message`: on every path a
null source and an unavailable source have the same outcome (the drive fails with `ProducerFailed`, a batched
watch start reports `NotApplicable`) and only the message differs, so one slot holds either a real source or
the reason there is none.

**Revisit only if:** the index reports a producer-less drive's reason some other way.

## A method named Try returns quickly

**Decision (2026-10-13, API review finding F23):** `ElevatedEntryPoint.RunBrokerIfRequested(arguments)`
replaces `ElevatedEntryPoint.TryHandle(arguments)`, with the same parameter, return value and behavior
([MFTLib issue 481](https://gitea.fleet.sticktoitive.net/schoen/MFTLib/issues/481)).

**Reason:** the method runs the broker for its whole life when the process was started with the broker flag,
exits the process with code 1 on rejected arguments, and otherwise returns false at once. A `Try` name in .NET
promises a quick attempt that reports failure by returning false. The name came from file-wizard when the code
was hoisted into MFTLib and was never revisited.

**Revisit only if:** the method stops blocking for the broker's lifetime.

## Names of the two index progress options, and what stays as it is

**Decision (2026-10-13, API review finding F21):** `FileIndexOptions.ScanProgress` replaces `Progress` (scan
progress samples, only while a scan runs) and `FileIndexOptions.DriveOpenProgress` replaces `OpenProgress` (one
`IndexDriveOpened` per drive that settles during open, warm starts included)
([MFTLib issue 485](https://gitea.fleet.sticktoitive.net/schoen/MFTLib/issues/485)). `BrokerSession.Connecting`
stays an event. The mix of events, `IProgress` options and the `Diagnostics` callback stays, because each style
follows what it reports.

**Reason:** neither old name said which option reported what, so each summary defined itself against the other.
`BrokerSession.Connecting` stays because git-wizard production
(`GitWizard/Discovery/RepositoryDiscoveryCoordinator.Watch.cs`) and the Watch sample use it.

**Revisit only if:** a consumer stops using `BrokerSession.Connecting`.

## The elevation provider types and the three query methods stay

**Decision (2026-10-13, API review findings F11 and F26, no change):** `IElevationProvider`, `ElevationResult`,
`WindowsRunasElevationProvider` and both `BrokerSession` constructor parameters stay as they are (F11).
`ProducerPolicy.Enumeration` and all three query methods `Search`, `Enumerate` and `EnumerateRows` stay (F26).

**Reason:** F11 found no defect to correct in the elevation provider shape or the session constructors. For F26,
`EnumerateRows` is a `ref struct` and cannot be used with LINQ or `await`, and `Search` is the only form that
cannot hold a snapshot borrow open, so each of the three serves a caller the others cannot.

**Revisit only if:** `EnumerateRows` stops being a `ref struct`, or a consumer needs elevation behavior the
current provider types cannot express.

## Open progress counts settle order and total

**Decision (2026-10-13, API review finding F13):** `IndexDriveOpened` keeps both `SettledDriveCount` and
`TotalDriveCount`. The Watch sample now prints one line per settled drive, so the notification has a sample
caller.

**Reason:** the notification was built to order for file-wizard's "drive k of N" display during warm starts
([file-wizard issue 382](https://gitea.fleet.sticktoitive.net/schoen/file-wizard/issues/382)). Drives settle
concurrently, so the settle counter is what lets a handler order overlapping reports, and the total keeps each
report self-describing. A single consumer is the design, not drift. The gap the review found was that no sample
showed it.

**Revisit only if:** file-wizard stops rendering open progress.

## A state enum plus a nullable reason

**Decision (2026-10-13, API review finding F14, widened to one rule):** a state enum is the field callers branch
on; the reason or error that accompanies it is a nullable property, non-null only when there is something to
report; a reason enum has no "nothing wrong" member. `CachedBlockStatus.Validation` is null unless
`Availability` is `Invalid`, and `BlockValidationResult` has no `Valid`. `DriveBlockStatus.CacheSlot` is a
nullable `CacheSlotState`, null when the block is not backed by a cache slot, and `CacheSlotState` has no
`NotApplicable`
([MFTLib issue 476](https://gitea.fleet.sticktoitive.net/schoen/MFTLib/issues/476)).

**Reason:** two fields stating one fact can contradict each other, and both consumers branch on the state enum
and only print the reason. Six of the eight state-plus-reason pairs on the public surface already had this
shape: `DriveStatus.Failure`, `WatchStateChange.Fault`, `DriveOperationResult.Failure`,
`CachedBlockDeletionResult.FailureReason`, `IndexScanProgress.Outcome` and `DriveWatchStatus.FailureMessage`.
`DriveStatus` set the pattern on 2026-10-12 when `DriveFailureKind.None` and `BlockSource.None` were deleted.

**Revisit only if:** a consumer needs to distinguish "checked and valid" from "not checked" on a row it cannot
already distinguish by the state enum.

## One cache inspection method, documented directory preparation

**Decision (2026-10-13, API review finding F20):** `CacheDirectory.InspectCached`
is one public method with optional `IEnumerable<DriveKey>? drives = null`
and `Action<CachedBlockRejection>? rejectedFile = null` parameters.
`CacheDirectory.EnsureCreated` stays public and unchanged, with its application
usage documented. `DeleteCached` stays unchanged; its rejected-filename callback
overload remains internal
([MFTLib issue 486 (cache inspection and creation documentation)](https://gitea.fleet.sticktoitive.net/schoen/MFTLib/issues/486)).

**Reason:** inspection's two overloads differed only by optional rejection
reporting; one method follows the existing one-method-per-verb rule and supports
both callers directly. file-wizard's `FileWizard/ContentHashSidecar.cs` calls
`EnsureCreated` before writing inside its cache directory, so directory preparation
is a production API readers need to discover. Cached index opens already call it
automatically. No consumer needs rejected filenames from public cache deletion.

**Revisit only if:** a concrete consumer requirement changes these responsibilities,
such as a production need for rejected-filename reporting during deletion.

## File entries expose their drive and keep distinct state questions

**Decision (2026-10-13, API review finding F24):** add `FileEntry.Drive`, a
`DriveKey` equal to `RecordKey.Drive`, with the same default-value and disposal
behavior as the entry's other data properties. `FileIndex.Drives` keeps its name.
`FileEntry.IsValid` and `IsDisposed` stay separate
([MFTLib issue 483 (add FileEntry.Drive)](https://gitea.fleet.sticktoitive.net/schoen/MFTLib/issues/483)).

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

**Decision (2026-10-13, API review finding F22):** `WatchStateChange` and
`DriveOperationResult` follow the receive-only record rule: explicit internal
constructors, public init-only properties, and no positional `Deconstruct`.
Tests construct them through `SyntheticNotifications.CreateWatchStateChange`
and `SyntheticNotifications.CreateDriveOperationResult` in `MFTLib.TestExtensions`
([MFTLib issue 482 (restrict hand-out record construction)](https://gitea.fleet.sticktoitive.net/schoen/MFTLib/issues/482)).

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

**Decision (2026-10-13, API review finding F25):** the doubles in `MFTLib.TestExtensions`
follow three shape rules
([MFTLib issue 484 (test package shapes)](https://gitea.fleet.sticktoitive.net/schoen/MFTLib/issues/484)).
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

**Decision (2026-10-12, API review finding F1,
[MFTLib pull request 462 (BrokerSession.Ended and disposal)](https://gitea.fleet.sticktoitive.net/schoen/MFTLib/pulls/462)):**
`BrokerSession.Ended` is the one member that reports a broker end. It completes once, shortly after the launched
process ends, with the end reason, and never at disposal. A session that never launched, or that is disposed while
its process is alive, never completes it. `HasEnded` does not exist; a caller that wants a flag reads
`Ended.IsCompleted`. A process end that completed before disposal began is still published. After `DisposeAsync`
every use throws `ObjectDisposedException`; a use after the process ended throws `InvalidOperationException`
naming the reason.

**Reason:** both members also completed when the owner disposed the session, so each consumer wrote code to tell
a broker death from its own disposal (file-wizard's `BrokerEndSubscription`, git-wizard's latch in
`MftIndexSession`), and `HasEnded` differed from `Ended.IsCompleted` only by an observer lag. One member with one
meaning removes both workarounds.

**Revisit only if:** a consumer needs to await its own session's disposal through the same task.

## One failure value and a nullable block on DriveStatus

**Decision (2026-10-12, API review finding F2,
[MFTLib pull request 463 (DriveStatus failure and block)](https://gitea.fleet.sticktoitive.net/schoen/MFTLib/pulls/463)):**
`DriveStatus.Failure` is a `DriveFailure` (`Kind`, non-null `Message`), non-null exactly when the drive is
`Failed`, or `Ready` retaining a block after a failed `RescanAsync` or a failed catch-up retry scan during
`OpenAsync` (kind `ProducerFailed`, the producer's message). A successful scan publication clears it and an
`Offline` drive has none. `DriveStatus.Block` is null unless `State` is `Ready`.
`DriveStatus.FailureKind` and `FailureMessage` do not exist, and `DriveFailureKind` and `BlockSource` have no
`None` member. A failure message is required where it is produced; there is no empty-string fallback.

**Reason:** "did this drive fail, and why" took three fields whose combinations were not independent: a `Ready`
drive could carry a rescan failure message with kind `None`. A blockless drive exposed a block of zeros that
git-wizard detected by testing `ScanTimestamp == default`, and both consumers recombined the fields with their own
fallback text. This is the first instance of the state-plus-nullable-reason rule above.

**Revisit only if:** a consumer needs a failure that is not tied to one drive.

## One watch snapshot type, named for what it holds

**Decision (2026-10-12, API review finding F3,
[MFTLib pull request 464 (watch state types)](https://gitea.fleet.sticktoitive.net/schoen/MFTLib/pulls/464)):**
the enum is `WatchState` (formerly `WatchCatchUpState`; its members `NotStarted`, `Recovering` and `Faulted`
describe the watch lifecycle) and `DriveWatchStatus.State` is its property. `FileIndex.WatchStateChanged` is an
`Action<WatchStateChange>`; `WatchStateChange` carries `DriveKey Drive` (formerly `DriveLetter`), the same
`DriveWatchStatus` snapshot that `DriveStatus.Watch` reports (`Watch`), and the optional `Fault`. One builder produces
the snapshot for both `Drives` and the event, so they cannot diverge. `DriveWatchState` and
`DriveStatus.ToWatchState()` do not exist.

**Reason:** `DriveStatus.Watch` and the event payload carried the same state and version, and `ToWatchState`
existed only to convert one shape into the other; both consumers called it to seed their history. The five names
`DriveState`, `DriveStatus`, `DriveWatchState`, `DriveWatchStatus` and `WatchCatchUpState` could not be told
apart as snapshot, event or enum.

**Revisit only if:** an event needs data that the snapshot does not carry.

## MftScanOptions configures every MFT scan

**Decision (2026-10-12, API review finding F4,
[MFTLib pull request 465 (scan options names)](https://gitea.fleet.sticktoitive.net/schoen/MFTLib/pulls/465)):**
`MftScanOptions` (formerly `BrokerScanOptions`) configures every MFT scan: broker-backed through
`BrokerSession.CreateIndexSource(scanOptions)` and in-process through `MftIndexSources.FromLocalVolumes(scanOptions)`.
Its `RetainedFileNames` (formerly `DirectoryScanFileNames`) is null to retain every eligible record, empty to
retain directories only, or nonempty to retain directories plus files whose names match ordinally, ignoring case.
The test package's `InProcessBrokerScan.RetainedFileNames` follows. Semantics are unchanged.

**Reason:** the type name implied a broker though the in-process path has none, and the member name implied a
directory walk though it is the retained-file-name filter of an MFT scan (`ProducerPolicy.Enumeration` is the
directory walk). Neither name said what it held. git-wizard is the production caller of the filter.

**Revisit only if:** a scan setting applies to one transport only.

## Search bounds are named for what they compare, and say what they exclude

**Decision (2026-10-12, API review finding F5,
[MFTLib pull request 466 (SearchQuery names)](https://gitea.fleet.sticktoitive.net/schoen/MFTLib/pulls/466)):**
`SearchQuery.LastWriteTimeMinimum` and `LastWriteTimeMaximum` (formerly `ModifiedAfter` and `ModifiedBefore`) are
inclusive bounds on `FileEntry.LastWriteTime`, which is UTC. The raw ticks of the supplied `DateTime` are compared
with no `DateTime.Kind` conversion, so the caller supplies a UTC value. A size bound excludes a row whose size is
unknown even when its stored size satisfies the comparison. `Subtree` (formerly `Under`) restricts the result to
the subtree below an entry, the entry included, and `IsDirectory` (formerly `Directories`) is true for directories
only, false for files only and null for no filter. The size bounds were later renamed by the one-name-per-concept
rule above.

**Reason:** "After" and "Before" read as exclusive but behave as inclusive. The size bounds silently changed which
rows exist, and file-wizard relied on that with `MinimumSize: long.MinValue` to drop unknown sizes. `Directories`
and `Under` did not say what they held. The review also proposed deleting the filters no consumer uses
(`CaseSensitive`, the time bounds, `MaximumSize`, `IncludeDeleted`); that part was not adopted, and they remain
for the Direct sample, which exercises them.

**Revisit only if:** a consumer needs an exclusive bound or a size filter that keeps unknown sizes.

## Public XML comments name public members, and Root says why it refused

**Decision (2026-10-12, API review findings F17 and F18,
[MFTLib pull request 461 (documentation sweep)](https://gitea.fleet.sticktoitive.net/schoen/MFTLib/pulls/461),
[MFTLib pull request 469 (XML comments)](https://gitea.fleet.sticktoitive.net/schoen/MFTLib/pulls/469) and
[MFTLib pull request 471 (Root message)](https://gitea.fleet.sticktoitive.net/schoen/MFTLib/pulls/471)):**
a public XML comment refers only to public members and states the contract the code has. `MftIndexSource` names
`BrokerSession.CreateIndexSource` and `MftIndexSources`, `JournalCheckpointLoss.SizeThatWouldHaveRetained` states that
the library reports the size and the consumer owns the growth decision (the sweep's historical link to
`BrokerSession.GrowUsnJournalAsync` was later replaced under the journal-growth decision above), and
`BrokerDiagnostics` no longer links an internal `Log` or mentions a fallback from the broker to a direct scan,
because none exists. `FileIndex.Root` documents its
`ArgumentException`, `Find` documents that null means no entry, and `Search` and `Enumerate` document
`ArgumentNullException` for a null query. `FileIndex.Root` throws `ArgumentException` with "Drive X has no block
(state: Offline)." (the drive's current `DriveState`) for a configured drive without a block, and keeps
"Drive X is not part of this index." for an unconfigured drive. Exception type and public surface are unchanged.

**Reason:** a reader following public documentation landed on types they cannot see, and the "not part of this
index" message was false for a drive configured in `FileIndexOptions.Drives` but `Offline` or `Failed`, hiding the
state the caller needs. Both git-wizard `Root` call sites can meet such a drive.

**Revisit only if:** `Root` gains a non-throwing form for a drive without a block.
