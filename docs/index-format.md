# Packed index block format

One block file per volume holds a volume's whole file inventory. It is the live
index, the on-disk cache, and the producer's write target at the same time. A
block is rebuildable from the filesystem by definition, so there is no migration
code: a version, serial, or completeness mismatch means discard and rescan.

## File name

`<drive letter>-<volume serial as eight uppercase hexadecimal digits>.mlix`, for
example `C-0BADF00D.mlix`. The serial is part of the name so a re-lettered drive
never matches the wrong block.

The library owns both directions of this name: `CacheDirectory.BlockFileName`
writes it and `CacheDirectory.EnumerateCached` reads a directory back into drive
letters and serials, so a consumer never parses the name itself and a future
format change is one edit rather than one edit per consumer.

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

## Layout

Little-endian throughout. Every region boundary is 4096-byte aligned.

| Region | Offset | Size |
| --- | --- | --- |
| Header | 0 | 4096 (104-byte declared header, including alignment padding) |
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

The format version is 2. The declared header ends at byte 104 to preserve 8-byte
alignment; the header region remains one 4096-byte page.

The complete flag is written last. A producer that dies mid-write leaves a block
without it, which a reader rejects.

The root row is a row index, not a producer-independent constant. Enumeration
blocks assign the volume root to row 0. MFT blocks preserve NTFS record indexes,
so the volume root is normally row 5. Readers begin root lookup and path descent
at this header value.

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
cursor, which is why a `FileId` from such a block reports `IsSynthetic`.

## MFT blocks

Rows are dense by NTFS record number: row i is record i, with unused slots left
empty and the volume root at row 5 (`BlockHeader.RootRow`). `RowCount` is the
highest written slot plus one. `LiveRowCount` counts records that are in use and
not tombstoned, excluding free slots and deleted files. Use the live count when
displaying a file count; `DriveStatus.LiveRowCount` reads it from the header. A
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
second change, so one real transition raises one change. A repeated reason bit is suppressed
only as the echo of what the cycle already applied: a second `USN_REASON_RENAME_NEW_NAME`
inside one open cycle carries a new name or parent and classifies again, because NTFS writes
one old-name/new-name record pair per rename and does not require a close between renames.
The rename echo is therefore keyed on the name and parent the record carries, not on the
reason bit alone. The state is runtime-only - it is not part of this format, is not
persisted, and is discarded with the block a rescan replaces. A sequence-number change on
the row resets the cycle, since the MFT segment was reused by a new file.

## Watch catch-up

When a watch session starts or a drive is re-armed after a rescan, the drive arms from its persisted
journal cursor (`USN journal id` and `USN next USN` in the block header). The broker or watch source
captures the current journal tip for that arm. Backlog journal batches between the resumed cursor and
the arm tip are streamed and applied to the block in place, advancing the header's `USN next USN` and
updating `LiveRowCount` and file rows under `_swapGate`. Once all backlog batches up to the arm tip have
been applied, an epoch-tagged `CaughtUp` marker transitions the drive's `DriveStatus.WatchCatchUp` from
`WatchCatchUpState.CatchingUp` to `WatchCatchUpState.CaughtUp` and completes `FileIndex.WaitForCatchUpAsync`.
Live journal mutations continue seamlessly from that point. If a drive's watch faults (before or after
catch-up), its state transitions to `WatchCatchUpState.Faulted` and any pending or subsequent
`WaitForCatchUpAsync` call faults with the drive's exception. The all-drives `WaitForCatchUpAsync` overload
completes when the slowest drive catches up and faults immediately upon the first drive watch failure or
cancellation. Disposing the index, cancelling the watch session, or superseding the arm through `RescanAsync`
cancels pending catch-up waits.

Calling `WaitForCatchUpAsync` is optional. The index observes its internally owned failure
notification tasks even when no caller waits for catch-up; drive failures still reach
`WatchFaulted` and `DriveStatus.WatchFailureMessage`. Pending and subsequent catch-up waits
continue to throw the drive's exception.

## Sidecars

A follow-up children table or name index is a separate file next to the block,
keyed by the block's generation. It is never a format change.
