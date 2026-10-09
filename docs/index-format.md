# Packed index block format

One block file per volume holds the inventory selected by the consumer's scan.
It is the live index, the on-disk cache, and the producer's write target at the
same time. The format is documented for anyone reading a cache file; the types
that read and write it (`BlockFile`, `BlockWriter`, `BlockHeader`, `BlockLayout`,
`FileRow`, `RowColumns`, `RowFlags`, `NamePool`) are internal to MFTLib, and tests
write blocks through `MFTLibTestExtensions`. A block is rebuildable from the filesystem, so there is no migration
code: incompatible format, identity, serial, or completeness means reject the
cache and rescan on a normal open.

## Scan diagnostics

`DriveBlockStatus.AccessDeniedSubtreeCount` counts subtrees the enumeration producer could
not enter during the scan, including directories that vanished before entry.
`DriveBlockStatus.SkippedRecordCount` counts records the MFT producer could not place,
including unsupported record identifiers, empty names and exhausted block capacity.
Profile-filtered records contribute to neither count. These diagnostics describe the
current block's production: successful rescans replace the counts, failed or cancelled
rescans retain them with the block, and warm starts report zero because these counts
are not stored in the block header. `DriveBlockStatus.CompactionNeeded` reads the header's
compaction flag independently of either count.

## File name

`<drive letter>-<volume serial as eight uppercase hexadecimal digits>.mlix`, for
example `C-0BADF00D.mlix`. The serial is part of the name so a re-lettered drive
never matches the wrong block.

The library owns both directions of this name: it writes the name and reads a
directory back into drive letters and serials (consumers list a directory with
`CacheDirectory.InspectCached`), so a consumer never parses the name itself and a future
format change is one edit rather than one edit per consumer.

The callback overload of `InspectCached` accepts an `Action<CachedBlockRejection>? rejectedFile`. Each non-canonical filename encountered by the existing top-level `*.mlix` enumeration is reported with its full `Path` and a human-readable `Reason` (`Invalid block filename.`). The canonical lists returned by these APIs do not include rejected files. Rejections are reported before drive filtering, including when the selected drive set is empty, because a rejected name has no validated drive identity. A lowercase drive letter or lowercase hexadecimal serial is non-canonical even on a case-insensitive filesystem.

Reporting does not open, validate, lock, rename, or delete the rejected file. Lock siblings, retired siblings, and subdirectories are not inventory candidates. The callback is synchronous, its ordering is unspecified, and it should return promptly without modifying the enumerated directory. A thrown callback exception aborts the call before any canonical inspection or deletion begins. Missing or empty directories produce no notifications. Overloads without the callback silently exclude rejected names.

```csharp
var rejected = new List<CachedBlockRejection>();
var statuses = CacheDirectory.InspectCached(cacheDirectoryPath, null, rejected.Add);
foreach (var entry in rejected)
{
    Console.WriteLine($"{entry.Path}: {entry.Reason}");
}
```

## Cache ownership

A live index owns its canonical block through a sibling `<drive>-<serial>.mlix.lock` file held
open with `FileShare.None` for the block's whole lifetime (an exclusive flock on Unix). A
second `FileIndex` that cannot take the lock never validates, renames, or deletes the block:
validating a block another index is mutating can observe a half-updated header or name
descriptor, and deleting a mapped file's name succeeds on Windows. Cache-only opens report the
drive `Failed` with `DriveFailureKind.InUse`; other opens scan into a private
`mftlib-private-<guid>-<name>` temp file with delete-on-close, the same shape `NoCache` uses
with its `mftlib-nocache-` prefix. The lock file itself is never deleted, because unlinking it
races another opener's create-and-lock; a leftover lock file matches no block-file pattern and
is re-locked in place.

### Observing a drive's backing

Read `FileIndex.Drives` and inspect `DriveBlockStatus.CacheSlot`:

| Value | Meaning |
| --- | --- |
| `CacheSlotState.OwnedCanonical` | This drive's current block belongs to the canonical cache slot held by this index. |
| `CacheSlotState.PrivateFallback` | This drive's current block was scanned privately because its canonical slot was unavailable when the scan target was selected. |
| `CacheSlotState.NotApplicable` | NoCache is enabled, or the drive has no block (failed or offline). |

Both Ready and Stale blocks retain their backing classification. This is
separate from `DriveBlockStatus.Source`: a canonical cold scan and a private
fallback scan both read `ProducedByScan`; a canonical warm start reads
`WarmStartedFromCache`.

A returned status is a point-in-time value. Reread `index.Drives` after a
rescan. If the previous owner exits, an existing private block stays private
until a successful rescan publishes a canonical replacement. Acquiring the
slot while that scan runs does not relabel the old private block. A failed
or cancelled scan which retains the old block retains its backing status.
During a canonical rescan the old file may be temporarily renamed aside;
the backing classification describes its slot, not a path-existence guarantee.
A scan into the canonical slot that fails or is cancelled without producing a block
deletes its partial file before it returns, while its index still holds the slot's owner lock, and reports the
delete (or a failure to delete) through `FileIndexOptions.Diagnostics`; a file that sat in the slot before the
scan stays only while it still opens as a complete, valid block; a rescan's previous block
file is restored.

The value contains no PID, process name, or user, and PrivateFallback does
not assert that another owner is still alive. It adds observability only:
normal opens still scan privately on contention, cache-only opens still
fail with InUse, and private blocks still use delete-on-close. No block
format or native ABI version changes are involved.

## Deleting cached blocks

`CacheDirectory.DeleteCached(cacheDirectoryPath, driveLetters = null, diagnostics = null)` is the
lock-safe way to clear cache blocks: it enumerates the directory the same way `InspectCached`
does, then for each candidate takes that block's owner lock non-blockingly and deletes the block
file only while holding it, so it is bound by the same ownership rule as `FileIndex` itself and
can never delete, rename, or unlink a block a live index still owns. A block whose lock is held
(or cannot be opened) reports `CachedBlockDeletionOutcome.InUse` and is left on disk untouched; a
delete that fails once the lock is taken reports `Failed` with the filesystem exception message.
Neither outcome stops the remaining candidates in the same call. A missing cache directory
returns an empty result, and an inventory entry that is already gone by the time its delete runs
is reported `Deleted`, an idempotent success rather than a failure.

Each attempt is a `CachedBlockDeletionResult`: the `CachedBlockFile` inventory entry that
identifies it, the `CachedBlockDeletionOutcome`, and a `FailureReason` string that is non-null
only for `Failed`. Lock files are never deleted by this path, for the same create-and-lock race
reason as above, so a `*.mlix.lock` file survives every outcome. A successful delete invokes the
optional `diagnostics` callback synchronously, while the block's lock is still held, with the
same "Deleted block file '...'" shape `FileIndexOptions.Diagnostics` uses elsewhere in the
library.

`DeleteCached` has an internal overload that takes the same filename-rejection callback after the success logger, which stays the third argument and is invoked under the canonical block's owner lock. Rejected entries never become deletion results and are always left untouched. Supplying a null rejection callback disables reporting. The public three-argument overload passes null.

## Layout

Little-endian throughout. Every region boundary is 4096-byte aligned.

| Region | Offset | Size |
| --- | --- | --- |
| Header | 0 | 4096 (112-byte declared header, including alignment padding) |
| Rows | 4096 | `slot capacity * 32`, rounded up to a page |
| Sequence region | after rows | `slot capacity * 2`, rounded up to a page |
| Name pool | after sequence region | `name pool capacity`, rounded up to a page |

## Header

| Offset | Field | Type | Notes |
| --- | --- | --- | --- |
| 0 | magic | u32 | `MLIX`, stored as `0x58494C4D` |
| 4 | format version | u32 | Mismatch means discard and rescan |
| 8 | producer kind | u32 | 1 = MFT, 2 = enumeration |
| 12 | flags | u32 | 1 = complete, 2 = compaction needed |
| 16 | volume serial | u32 | |
| 20 | root row | u32 | Row index of the volume root; 0 for enumeration, normally 5 for MFT |
| 24 | scan timestamp | i64 | UTC ticks |
| 32 | row count | u32 | Highest used slot plus one |
| 36 | slot capacity | u32 | Rows region size, in rows |
| 40 | name pool used | u32 | Bytes |
| 44 | name pool capacity | u32 | Bytes |
| 48 | USN journal id | u64 | Zero for enumeration blocks |
| 56 | USN next USN | i64 | |
| 64 | generation | u64 | Bumped once per mutation batch |
| 72 | row region offset | u64 | |
| 80 | name pool offset | u64 | |
| 88 | live row count | u32 | Rows in use and not tombstoned; maintained by `BlockWriter` |
| 96 | sequence region offset | u64 | |
| 104 | consumer FourCC | u32 | Four ASCII bytes in source order; zero means unspecified when version is also zero |
| 108 | consumer version | u32 | Opaque consumer-owned version; compared exactly |

The format version is 3. The declared header ends at byte 112; the header
region remains one 4096-byte page and every existing field retains its offset.
Version-2 blocks fail the existing format check and are rebuilt on the next
normal open. Cache-only opens cannot rebuild them and decline the drive.

The complete flag is written last. A producer that dies mid-write leaves a block
without it, which a reader rejects.

The root row is a row index, not a producer-independent constant. Enumeration
blocks assign the volume root to row 0. MFT blocks preserve NTFS record indexes,
so the volume root is normally row 5. Readers begin root lookup and path descent
at this header value.

## Consumer cache identity

`FileIndexOptions.CacheTag` is an optional `CacheTag`, constructed from exactly
four ASCII characters and a `uint` version. The all-zero default means
unspecified, not match-any. Codes are case-sensitive and neither component is
interpreted by MFTLib. The consumer increments its version whenever its scan
retention policy changes the rows it stores. `DirectoryScanFileNames` distinguishes
full retention (null), directories alone (empty), and directories plus matching names.
Freed-record inclusion also changes the retained rows. For example:

    CacheTag = new CacheTag("GITW", 1)

Both fields are initialized before a block is marked complete, for initial
scans, rescans and private/no-cache blocks. The internal
`MftBlockProducer` delegate must copy `MftBlockProduceRequest.CacheTag` into the block it
creates; returning a differently tagged block is a producer failure.
`SyntheticMftProducer` in `MFTLibTestExtensions` does this for tests. The built-in
`BrokerMftBlockProducer` forwards it through the client-created block target.

After structural validation, warm opening compares both fields to the
requested tag before adopting the block or checking its journal cursor.
A mismatch records `BlockValidationResult.WrongCacheTag`, disposes and
best-effort deletes the rejected cache under its owner lock, then cold-scans.
With `InitialOpenCacheOnly`, it instead reports `DriveState.Failed` and
`DriveFailureKind.CacheTagMismatch`. `RescanAsync` can recover that drive.
Both paths emit a `Diagnostics` line identifying the stored and requested
tags. ASCII control characters in tag diagnostics are escaped.

`CacheDirectory.InspectCached` returns `CachedBlockStatus.CacheTag` for an
Available block. Invalid or InUse results have null tags because their headers
were not safely inspected. Cache filenames still contain only drive and serial.
The tag does not provide shared-cache multiplexing or validate scan filters.

## Row

One 32-byte row per slot, dense by record number, so row i is record i and there
is no lookup table.

| Offset | Field | Type | Notes |
| --- | --- | --- | --- |
| 0 | parent row | u32 | Row index of the parent; the root points at itself |
| 4 | attributes | u32 | NTFS file attributes |
| 8 | name offset | u32 | Bytes into the name pool (descriptor word low 32 bits) |
| 12 | name length | u16 | UTF-16 code units (descriptor word bits 32..47) |
| 14 | row flags | u16 | 1 in use, 2 directory, 4 tombstone, 8 size unknown, 16 subtree skipped (descriptor word bits 48..63) |
| 16 | size | i64 | Bytes; zero for directories and size-unknown rows |
| 24 | modified | i64 | UTC ticks |

A deleted row has the in-use and tombstone flags together, whatever deleted it: a journal delete sets
tombstone on a live row, and a scan that imports freed records writes the same two flags, so
`LiveRowCount` excludes both. A freed row whose parent chain verified at scan time keeps its parent
row. Any other freed row is detached: its parent column holds `uint.MaxValue`, no row has that index,
so it is nobody's child, its parent walk ends at once and its path is its bare name. Only a create
reuses a deleted row's slot; the journal never edits, renames or deletes it again.

Deleted rows are reverse-indexed by parent slot in memory only. `DeletedChildLinks`, owned by `BlockFile`, is built lazily once per block, kept current at every row write, rename, tombstone and detach, and consulted when a journal create reuses a slot, so only that slot's deleted descendants are detached. Each parent holds a hash set of its deleted children, so linking and unlinking a row is constant time however many deleted siblings share the parent. The index is not persisted in the block format: a block opened from a cache file carries no links until the first create that needs them.

Name offset, name length, and row flags sit adjacent at byte offset 8 to form
an 8-byte aligned 64-bit descriptor word. Rows are 32 bytes and the row region
starts at 4096 (a 4 KB boundary), so byte offset 8 in every row is 8-byte
aligned. A rename or flag update publishes via a single atomic 64-bit store
(`FileRow.WriteDescriptorWord`) without tearing.

The read rule is narrower than the write rule, and the difference is the whole
point of the layout:

- **The name offset and the name length must come from one
  `FileRow.ReadDescriptorWord` on a live block.** Reading the two fields
  separately can straddle a concurrent rename and pair a new offset with an old
  length, which is exactly the torn read this layout exists to prevent. Never
  pair a direct `NameOffsetBytes` read with a direct `NameLengthUnits` read.
- **The flags may be read on their own.** The field is 2-byte aligned, so it
  cannot tear by itself, and the scan loops read it directly through `IsInUse`,
  `IsDirectory`, `IsDeleted`, `SizeKnown` and `SubtreeSkipped`. Pairing such a
  read with a separate name read is safe because the name pool is append-only: a
  span built from a descriptor that is one rename stale still points at valid,
  immutable characters. Routing the flag predicates through the descriptor word
  would cost a 64-bit read plus three shifts per row on the hottest loop in the
  library and buy no correctness.
- **Every write goes through `FileRow.WriteDescriptorWord`**, including a write
  that only means to change the flags, which must round-trip the offset and the
  length through the same call.
- **A `BlockWriter` operation and `BlockFile.Dispose` never overlap.** Every
  writer operation holds a block access scope for its full duration; disposal
  refuses new scopes when it begins and waits for outstanding ones before
  unmapping the view. A writer that arrives after disposal began fails with
  `ObjectDisposedException`, never a torn or unmapped access. The raw
  `BlockFile` properties are not part of this guarantee: their check-then-use
  pattern protects a single owner, and readers are expected to hold a snapshot
  borrow instead.

## Sequence region

One `ushort` per row slot, densely stored after the row region and before the
name pool. The sequence region stores one sequence number per slot so readers can
derive a 64-bit file identifier in the form `(sequenceNumber << 48) | recordNumber`.
For slot capacity *N*, the region uses `N * 2` bytes, then aligns to 4 KB like all
other block regions.

## Capacity

Slot capacity is the estimated row count plus headroom of 25 percent or 65536
rows, whichever is larger, so journal creates land in place at their record
number. Name pool capacity uses the same 25 percent ratio with a floor of one
mebibyte (1048576 bytes). Exhausting either sets the compaction-needed flag; the
producer or mutator keeps applying what fits and the drive is reported stale so
the caller can offer a rescan. Compaction is a rescan.

## Name pool

UTF-16, append only. A rename appends the new name to the name pool and then
atomically updates the row descriptor word (name offset, name length, and row
flags) with a single 64-bit store, so a concurrent reader sees the old name or
the new one and never a torn descriptor. Names are not interned. Maximum
name length is 32767 UTF-16 code units.

## Enumeration blocks

An enumeration producer has no record numbers, so it assigns rows sequentially
in traversal order with the parent column in the same shape. Nothing downstream
can tell the difference except the producer kind and the absence of a journal
cursor, which is why an `IndexRecordKey` from such a block reports `ProducerKind.Enumeration`.

## MFT blocks

Rows are dense by NTFS record number: row i is record i, with unused slots left
empty and the volume root at row 5 (`BlockHeader.RootRow`). `RowCount` is the
highest written slot plus one. `LiveRowCount` counts records that are in use and
not tombstoned, excluding free slots and deleted files. Use the live count when
displaying a file count; `DriveBlockStatus.LiveRowCount` reads it from the header. A
size-unknown row carries `RowFlags.SizeUnknown` and a zero size when no usable data size was
found in the base record, including data in an extension record that the parser
does not follow or a negative non-resident data size. A known zero size means an
empty file or a directory.

The non-elevated client plans capacities from `NtfsVolumeInformation` and creates
the block file and its named section. The elevated broker opens that section and
writes rows and names directly from parse batches, without resolving full paths.
It stamps the armed journal cursor and writes the completed header last; a crash
before completion leaves a block the client discards.

Journal mutation coalesces NTFS close records. NTFS ends every open cycle with a record
that repeats the cycle's reasons plus `USN_REASON_CLOSE`, and the watch reads with
`ReturnOnlyOnClose = 0`, so every intermediate record arrives. `JournalMutator` tracks,
per drive block, the reasons each row's open cycle already reported; a close record that
adds no new reason restamps the row's `ModifiedTicks` and attributes without emitting a
second change, so one real transition raises one change. Writes inside the create cycle (records
whose reasons still include `USN_REASON_FILE_CREATE` before the cycle's close) are part of the
creation and raise no extra changes, so a create-write-close cycle emits only `Created`.
A repeated reason bit is suppressed
only as the echo of what the cycle already applied: a second `USN_REASON_RENAME_NEW_NAME`
inside one open cycle carries a new name or parent and classifies again, because NTFS writes
one old-name/new-name record pair per rename and does not require a close between renames.
The rename echo is therefore keyed on the name and parent the record carries, not on the
reason bit alone. The state is runtime-only - it is not part of this format, is not
persisted, and is discarded with the block a rescan replaces. A sequence-number change on
the row resets the cycle, since the MFT segment was reused by a new file.

## Watch catch-up

Each MFT-backed drive has its own watch handle and catch-up state. When
`FileIndex.StartWatchingAsync(char, CancellationToken)` starts a drive, or a rescan restarts
one whose watch is still requested, that drive arms from the journal cursor persisted in its
block header (`USN journal id` and `USN next USN`). Other drives' handles and blocks are
untouched.

The source captures the journal tip for the arm. Backlog batches between the persisted
cursor and that tip are streamed and applied to the block in place, advancing the header's
`USN next USN` and updating `LiveRowCount` and file rows under the drive's write gate. A
`DriveCaughtUp` item transitions `DriveWatchStatus.CatchUpState` from
`WatchCatchUpState.CatchingUp` to `WatchCatchUpState.CaughtUp`. Live mutations continue on
the same handle.

`FileIndex.WaitForCatchUpAsync(char, CancellationToken)` follows that drive's current
handle. It completes immediately when the drive is already caught up, faults with the
drive's exception when the watch failed or its last start was refused, and throws
`InvalidOperationException` when the drive's watch is not requested and it has no current
watch. Stop and index disposal cancel a pending wait when they retire that handle. A rescan
keeps it attached during production and cancels it only when the replacement commits and
retires the old handle; a wait issued after that commit and before the replacement registers
follows the replacement handle.
Failed or cancelled production leaves the watch and its waits untouched. Cancelling the wait's own
token cancels only the wait, not the drive's watch.

A `Drive` or `Apply` fault moves the drive to `WatchCatchUpState.Recovering` and starts an
automatic rescan. A wait issued during recovery faults immediately with the exception that
ended the prior watch. After recovery starts the replacement handle and the drive reads
`CatchingUp`, call the wait again to follow that handle. If recovery fails, or the
replacement handle faults before reaching `CaughtUp`, the index raises a `Recovery` fault
and leaves the drive `Faulted` until a consumer starts or rescans it. A `Channel` fault does
not recover automatically. A successful manual rescan whose replacement watch cannot start
raises `RescanRestart` and leaves the drive `Faulted` without automatic recovery; the scan
returns normally. The fault and `DriveWatchStatus.FailureMessage` name the rescan, and the start failure
is the inner exception. Stop rethrows the fault once. Across a successful swap, old `Changed`
events can arrive after publication and repeat during the new watch's catch-up; queries can
lag until it reports `CaughtUp`.

`WaitForCatchUpAsync(char, CancellationToken)` waits for one drive and throws its failure.
The token-only all-drive overload returns one
`DriveOperationResult` per configured drive in request order after every drive has settled.
A failed drive has `DriveOperationOutcome.Failed` and carries the exception in `Failure`;
it does not end the waits for other drives. A drive with no watch is `NotApplicable`. The
overload with only a `CancellationToken` uses every drive in `FileIndexOptions.Drives`
order. Caller cancellation cancels the batched call; disposal cancels it through the
index's disposal token.

Calling either wait form is optional. The index observes each handle's failure even when
no caller waits; faults still reach `WatchFaulted`, `DriveWatchStatus.FailureMessage`, and
`DriveWatchStatus.CatchUpState`.

## Sidecars

A follow-up children table or name index is a separate file next to the block,
keyed by the block's generation. It is never a format change.
