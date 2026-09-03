# Packed index block format

One block file per volume holds a volume's whole file inventory. It is the live
index, the on-disk cache, and the producer's write target at the same time. A
block is rebuildable from the filesystem by definition, so there is no migration
code: a version, serial, or completeness mismatch means discard and rescan.

## File name

`<drive letter>-<volume serial as eight uppercase hexadecimal digits>.mlix`, for
example `C-0BADF00D.mlix`. The serial is part of the name so a re-lettered drive
never matches the wrong block.

## Layout

Little-endian throughout. Every region boundary is 4096-byte aligned.

| Region | Offset | Size |
| --- | --- | --- |
| Header | 0 | 4096 (88 bytes used) |
| Rows | 4096 | `slot capacity * 32`, rounded up to a page |
| Name pool | after rows | `name pool capacity`, rounded up to a page |

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
the new one and never a torn descriptor. Names are not interned in v1. Maximum
name length is 32767 UTF-16 code units.

## Enumeration blocks

An enumeration producer has no record numbers, so it assigns rows sequentially
in traversal order with the parent column in the same shape. Nothing downstream
can tell the difference except the producer kind and the absence of a journal
cursor, which is why a `FileId` from such a block reports `IsSynthetic`.

## Sidecars

A follow-up children table or name index is a separate file next to the block,
keyed by the block's generation. It is never a format change.
