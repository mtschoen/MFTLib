# Packed index block format

One block file per volume holds the inventory selected by the consumer's scan.
It is the live index, the on-disk cache, and the producer's write target at the
same time. The format is documented for anyone reading a cache file; the types
that read and write it (`BlockFile`, `BlockWriter`, `BlockHeader`, `BlockLayout`,
`FileRow`, `RowColumns`, `RowFlags`, `NamePool`) are internal to MFTLib, and tests
write blocks through `MFTLibTestExtensions`. A block is rebuildable from the filesystem, so there is no migration
code: incompatible format, identity, serial, or completeness means reject the
cache and rescan on a normal open. The stored producer kind must also match
the selected producer: warm adoption rejects a mismatch before cache-tag,
scan-shape, root and journal checks, closes the mapping and best-effort deletes the file under
the owner lock. A normal open scans with the requested producer; a cache-only open
reports `RootState.Failed` with `RootFailureKind.CacheDeclined`. A cache-only open never
scans, so it never deletes a rejected block either: every rejection on a cache-only open
leaves the file in place for a writing open to rebuild.

## Scan diagnostics

`RootBlockStatus.AccessDeniedSubtreeCount` counts subtrees the enumeration producer could
not enter during the scan that built the block, including directories that vanished before entry.
The header stores it, so a warm start reports the count of the scan that built the block.
`RootBlockStatus.SkippedRecordCount` counts records the MFT producer could not place,
including unsupported record identifiers, empty names and exhausted block capacity.
All MFT producers saturate this diagnostic at `int.MaxValue` when the scan's count is larger.
Records excluded by `MftScanOptions.IncludeFreed` contribute to neither count,
and neither does a directory `FileIndexOptions.DirectoryWalk` records without entering. These diagnostics describe the
current block's production: successful rescans replace the counts, and failed or cancelled
rescans retain them with the block. A warm start reports zero skipped records, because that
count is not stored in the block header. `RootBlockStatus.IsCapacityExhausted` reads the header's
capacity-exhausted flag independently of either count.

## File name

Two shapes, both ending in the serial of the volume that contains the root as eight
uppercase hexadecimal digits and the extension `.mlix`:

- A volume root, and every MFT block whatever the root of the view that owns it: `<drive letter>-<serial>.mlix`,
  for example `C-0BADF00D.mlix`. An MFT block is the volume's, so a directory view of it shares that name.
- A walked directory root: `D<sixteen lowercase hexadecimal digits>-<serial>.mlix`, for example
  `D0123456789abcdef-0BADF00D.mlix`. The digits are the first eight bytes of the SHA-256
  of the root key text (the normalized directory path) encoded as UTF-8. On a
  case-insensitive platform (Windows) the text is first mapped with the invariant simple uppercase
  mapping, the one `OrdinalIgnoreCase` equality of root keys uses, so every pair of equal keys
  (including sigma and final sigma) shares one name; on a case-sensitive platform it is hashed as written.

The serial is part of the name so a re-lettered volume never matches the wrong block. A hashed
name cannot be turned back into a path, so every block stores its root key text in page 0
(see [Root key text](#root-key-text)); the name is derived from that text and the header's
volume serial, and a block whose file name differs from the derived name is invalid.
The `.mlix.lock` sibling, the `.retired-*` siblings and the private and cache-disabled
temporary names all derive from the same name.

The library owns both directions of this name: it writes the name and lists a
directory back into roots and serials (consumers list a directory with
`CacheDirectory.InspectCached`), reading the stored key of a directory-shaped file, so a consumer never parses
the name itself and a future format change is one edit rather than one edit per consumer.
`InspectCached` and `DeleteCached` compute the expected name stem of each selected root forward, keep that root as
the candidate's identity and open no file outside the selection. Slot ownership comes first: each candidate's owner lock
is tried before any block byte is read or validated, and a held lock reports `InUse` under the selected root whatever the
key storage holds. Once the lock is held, the stored key of a directory-shaped file names the result. Only an unselected
directory-shaped file has no identity until its key page is read; when its lock is held by another owner that page is read
to say which root is in use, and a file whose key cannot be read (or whose name is not the one the key derives) has no
identity to report and is reported through `rejectedFile` like any other non-canonical name, never deleted.

`InspectCached` accepts an optional `Action<CachedBlockRejection>? rejectedFile` parameter. Each non-canonical filename encountered by the existing top-level `*.mlix` enumeration is reported with its full `Path` and a human-readable `Message` (`Invalid block filename.`). The canonical lists returned by these APIs do not include rejected files. Rejections are reported before volume filtering, including when the selected volume set is empty, because a rejected name has no validated volume identity. A lowercase drive letter, an uppercase hash digit or a lowercase hexadecimal serial is non-canonical even on a case-insensitive filesystem.

Reporting does not open, validate, lock, rename, or delete the rejected file. Lock siblings, retired siblings, and subdirectories are not inventory candidates. The callback is synchronous, its ordering is unspecified, and it should return promptly without modifying the enumerated directory. A thrown callback exception aborts the call before any canonical inspection or deletion begins. Missing or empty directories produce no notifications. A call that omits `rejectedFile` silently excludes rejected names.

```csharp
var rejected = new List<CachedBlockRejection>();
var statuses = CacheDirectory.InspectCached(cacheDirectoryPath, null, rejected.Add);
foreach (var entry in rejected)
{
    Console.WriteLine($"{entry.Path}: {entry.Message}");
}
```

Each `CachedBlockStatus` carries a `CachedBlockFile` (volume, volume serial, full path, size and
last-write time) and an `Availability` of `Available`, `InUse` or `Invalid`. `Validation` is
null unless the block is invalid, and then gives the rejection reason.

## Cache ownership

A live index owns its canonical block through a sibling `<volume>-<serial>.mlix.lock` file held
open with `FileShare.None` for the block's whole lifetime (an exclusive flock on Unix). A
second `FileIndex` that cannot take the lock never validates, renames, or deletes the block:
validating a block another index is mutating can observe a half-updated header or name
descriptor, and deleting a mapped file's name succeeds on Windows. Cache-only opens report the
root `Failed` with `RootFailureKind.InUse`; other opens scan into a private
`mftlib-private-<guid>-<name>` temp file with delete-on-close, the same shape `CacheMode.Disabled` uses
with its `mftlib-cache-disabled-` prefix. The lock file itself is never deleted, because unlinking it
races another opener's create-and-lock; a leftover lock file matches no block-file pattern and
is re-locked in place.

### Observing a root's backing

Read `FileIndex.Status` and inspect `RootBlockStatus.CacheLocation`:

| Value | Meaning |
| --- | --- |
| `CacheLocation.OwnedCanonical` | This root's current block belongs to the canonical cache slot held by this index. |
| `CacheLocation.PrivateFallback` | This root's current block was scanned privately because its canonical slot was unavailable when the scan target was selected. |
| null | The block is not backed by a cache slot: `CacheMode.Disabled` is in effect. A failed or offline root has no `RootStatus.Block` to inspect. |

Ready blocks retain their backing classification, including when capacity is exhausted. This is
separate from `RootBlockStatus.Origin`: a canonical cold scan and a private
fallback scan both read `ProducedByScan`; a canonical warm start reads
`WarmStartedFromCache`.

A returned status is a point-in-time value. Reread `index.Status` after a
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
not assert that another owner is still alive. It describes backing only;
the ownership rules above govern contention and private-block lifetime.

## Deleting cached blocks

`CacheDirectory.DeleteCached(cacheDirectoryPath, roots = null)` is the
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
identifies it, the `CachedBlockDeletionOutcome`, and a `FailureMessage` string that is non-null
only for `Failed`. Lock files are never deleted by this path, for the same create-and-lock race
reason as above, so a `*.mlix.lock` file survives every outcome. Consumers render or log `CachedBlockDeletionResult` values after the call returns.

`DeleteCached` has an internal overload taking
`Action<CachedBlockRejection>? rejectedFile` after the root selection.
Rejected entries never become deletion results and are always left untouched.
Supplying a null rejection callback disables reporting. The public two-parameter
method supplies null. Rejection callbacks run during eager enumeration, before
root filtering and canonical deletion; a callback exception aborts the call
before any canonical block is locked or deleted.

## Layout

Little-endian throughout. Every region boundary is 4096-byte aligned.

| Region | Offset | Size |
| --- | --- | --- |
| Header | 0 | 4096 (160-byte declared header, including alignment padding) |
| Rows | 4096 | `slot capacity * 32`, rounded up to a page |
| Sequence region | after rows | `slot capacity * 2`, rounded up to a page |
| Name entry table | after sequence region | `(entry capacity + 1) * 12`, rounded up to a page |
| Bucket directory | after name entries | `bucket count * 4`, rounded up to a page |
| Name byte pool | after buckets | `name pool capacity`, rounded up to a page |

Entry zero is reserved. The header stores the entry capacity `E`, the number of allocatable
entries excluding that reserved entry. The volume plan sets `E` to the slot capacity `S`, and a rescan
or growing walk can create a block with a larger independent `E`. Every region offset and size is computed
from the stored `S`, `E` and pool capacity `P`. The bucket count is the smallest power of two at least
`max(1024, ceil(E / 4))`. New bucket heads are zero.

## Header

| Offset | Field | Type | Notes |
| --- | --- | --- | --- |
| 0 | magic | u32 | `MLIX`, stored as `0x58494C4D` |
| 4 | format version | u32 | Mismatch means discard and rescan |
| 8 | producer kind | u32 | 1 = MFT, 2 = enumeration |
| 12 | flags | u32 | 1 = complete, 2 = capacity exhausted |
| 16 | volume serial | u32 | |
| 20 | root row | u32 | Row index of the indexed root; 0 for enumeration, normally 5 for MFT |
| 24 | scan timestamp | i64 | UTC ticks |
| 32 | row count | u32 | Highest used slot plus one |
| 36 | slot capacity | u32 | Rows region size, in rows |
| 40 | name pool used | u32 | Bytes |
| 44 | name pool capacity | u32 | Bytes |
| 48 | USN journal identifier | u64 | Zero for enumeration blocks |
| 56 | USN next USN | i64 | |
| 64 | generation | u64 | Change version: bumped once per applied mutation batch; a rescan starts its replacement block at the superseded block's value plus one (a first scan with no predecessor starts at 1); a warm start adopts the cached value. Read as `RootBlockStatus.Generation`; comparable only within one `FileIndex` |
| 72 | row region offset | u64 | |
| 80 | name pool offset | u64 | |
| 88 | live row count | u32 | Rows in use and not tombstoned; maintained by `BlockWriter` |
| 96 | sequence region offset | u64 | |
| 104 | consumer FourCC | u32 | Four ASCII bytes in source order; zero means unspecified when version is also zero |
| 108 | consumer version | u32 | Opaque consumer-owned version; compared exactly |
| 112 | scan shape | u64 | Library identity of the row-shaping options; see [Scan shape](#scan-shape) |
| 120 | access-denied subtree count | u32 | Subtrees the enumeration producer could not enter; zero for MFT blocks |
| 124 | name entry count | u32 | Published entries, excluding entry zero |
| 128 | name entry capacity | u32 | Allocatable entries `E`, excluding entry zero; equal to `S` in the volume plan, or larger after independent growth |
| 132 | bucket count | u32 | `max(1024, ceil(E / 4))` rounded up to a power of two |
| 136 | name entry region offset | u64 | |
| 144 | bucket region offset | u64 | |
| 152 | reserved | u64 | Written as zero |

The format version is 8. The declared header ends at byte 160 within a
4096-byte header page; the root key text follows it. A version mismatch rejects the block: normal opens
rebuild it, and cache-only opens decline the root and leave the block in place.

## Root key text

Page 0 holds the block's root key after the declared header, for every block:

| Offset | Field | Type | Notes |
| --- | --- | --- | --- |
| 160 | key text length | u16 | UTF-16 code units, 1 to 1967 |
| 162 | key text | UTF-16 code units | The volume letter (`C`) or the normalized directory path, without a trailing separator |

A reader accepts the key only when the count fits the page (`162 + 2 * count <= 4096`), the text
parses to a root key whose own text is exactly the stored text, and the file name equals the name derived from that key
and the header's volume serial. Malformed text reports `InconsistentRegions`; a name that is not the derived one reports
`WrongRootDirectory`. Either discards the block and cold-scans. The text is written once when the block is created and
never changes.

The path length limit follows from the page: `RootKey.FromDirectory` throws `ArgumentException` for a normalized
directory path longer than 1967 UTF-16 code units, so no walked root can produce a block whose key does not fit.

The complete flag is written last. A producer that dies mid-write leaves a block
without it, which a reader rejects.

The root row is a row index, not a producer-independent constant. Enumeration
blocks assign the configured starting directory, which can be any directory and not
only a volume root, to row 0. MFT blocks preserve NTFS record indexes, so the
volume root is normally row 5. Readers begin root lookup and path descent
at this header value.

## Consumer cache identity

`FileIndexOptions.CacheTag` is an optional `CacheTag`, constructed from exactly
four ASCII characters and a `uint` version. The all-zero default means
unspecified, not match-any. Codes are case-sensitive and neither component is
interpreted by MFTLib. The tag carries only meaning the consumer owns and MFTLib
cannot see. The options that shape the rows are the library's to compare, as the
[scan shape](#scan-shape). For example:

    CacheTag = new CacheTag("GITW", 1)

Both fields are initialized before a block is marked complete, for initial
scans, rescans and private and `Disabled` blocks. The internal
`MftBlockProducer` delegate must copy `MftBlockProduceRequest.CacheTag` into the block it
creates; returning a differently tagged block is a producer failure.
`SyntheticMftProducer` in `MFTLibTestExtensions` does this for tests. The built-in
`BrokerMftBlockProducer` forwards it through the client-created block target.

After structural validation, warm opening compares both fields to the
requested tag before adopting the block or checking its journal cursor.
A mismatch records `BlockValidationResult.WrongCacheTag`, disposes and
best-effort deletes the rejected cache under its owner lock, then cold-scans.
With `CacheMode.ExistingOnly`, it instead leaves the block in place and reports `RootState.Failed` and
`RootFailureKind.CacheIdentityMismatch`. `RescanAsync` can recover that root.
Both paths emit a `Diagnostics` line identifying the stored and requested
tags. ASCII control characters in tag diagnostics are escaped.

`CacheDirectory.InspectCached` returns `CachedBlockStatus.CacheTag` for an
Available block. Invalid or InUse results have null tags because their headers
were not safely inspected. Cache filenames still contain only volume and serial.
The tag does not provide shared-cache multiplexing.

## Scan shape

The header's scan shape at offset 112 identifies the options that shaped the block's
rows. It is the first eight bytes, read as a little-endian u64, of SHA-256 over a
canonical byte encoding. Integers are little-endian, a flag is one byte (0 or 1), and
a string set is a u32 count followed by each string as a u32 UTF-8 byte count and its
bytes, deduplicated and sorted ordinally after folding. Every name or path string in
those sets is well-formed UTF-16, so the UTF-8 form is lossless: admission rejects an
unpaired surrogate in a pruned directory name or a pruned path before any cache work.
A pruned directory name pattern sequence is a u32 count followed by each distinct
`(Regex.ToString(), (uint)Regex.Options)` pair, sorted ordinally by pattern text and then
by numeric `RegexOptions`: each pattern text is written as its u32 UTF-16 code-unit count
followed by the little-endian u16 code units as given, and then its `RegexOptions` as a
u32 (`MatchTimeout` is excluded).

| Producer | Canonical encoding, in order |
| --- | --- |
| MFT | producer kind u32 (1); `MftScanOptions.IncludeFreed` flag |
| Enumeration | producer kind u32 (2); pruned directory names; pruned directory attributes u32; `PruneDotDirectories` flag; pruned directory paths; pruned directory name patterns |

For an enumeration block, exact pruned names and pruned paths fold to upper-invariant on
Windows and stay as given elsewhere, matching the root's path comparison; regex pattern
text is never folded by the root comparison because each `Regex` carries its own
`RegexOptions`. Paths are normalized with `Path.GetFullPath` and
`Path.TrimEndingDirectorySeparator`, and only the paths equal to or beneath the root's
directory are encoded, so editing a pruned path under one root leaves every other volume's
block valid. MFT blocks ignore `FileIndexOptions.DirectoryWalk`, including
`PrunedDirectoryNamePatterns`. The MFT `IncludeFreed` value comes from the scan options of
`FileIndexOptions.MftSource`; an open with no source computes it as false.

The index computes the expected shape for each root and passes it to the producer in
`MftBlockProduceRequest.ScanShape`, or writes it itself for an enumeration block. A
custom `MftBlockProducer` copies the value into `BlockFileCreateOptions.ScanShape`, as it
copies the cache tag; returning a block with another shape is a producer failure.
`BrokerMftBlockProducer` forwards it through the client-created block target.

Warm opening compares the scan shape after the cache tag. A mismatch records
`BlockValidationResult.WrongScanShape`, disposes and best-effort deletes the block under
its owner lock, and cold-scans. With `CacheMode.ExistingOnly` the block stays in place and the root fails with
`RootFailureKind.CacheIdentityMismatch`, the kind a cache-tag mismatch reports, and
`RescanAsync` can recover it. Both paths emit a `Diagnostics` line with the stored and
requested shapes as sixteen hexadecimal digits. The shape has no public accessor, and
`CacheDirectory.InspectCached` does not report it.

## Row

One 32-byte row per slot, dense by record number, so row i is record i and there
is no lookup table.

| Offset | Field | Type | Notes |
| --- | --- | --- | --- |
| 0 | parent row | u32 | Row index of the parent; the root points at itself |
| 4 | attributes | u32 | NTFS file attributes |
| 8 | name identifier | u32 | Index into the name entry table; zero means no name (descriptor word low 32 bits) |
| 12 | reserved | u16 | Zero (descriptor word bits 32..47) |
| 14 | row flags | u16 | 1 in use, 2 directory, 4 tombstone, 8 size unknown, 16 subtree skipped, 32 last-write time unknown (descriptor word bits 48..63) |
| 16 | size | i64 | Bytes; zero for directories and size-unknown rows |
| 24 | modified | i64 | UTC ticks of the last-write time a scan observed; zero for last-write-unknown rows |

The size and modified columns hold only what a producer observed. A journal record carries neither a size nor
a last-write time, only its own event time (`FileChange.Timestamp`), which is never stored in the row. The
journal therefore marks observations unknown instead of replacing them, and a row flagged unknown reads
`DateTime.MinValue` as its last-write time:

| Journal event | Size | Modified | Attributes |
| --- | --- | --- | --- |
| Create, or hydration of a row the index never had | zero, size unknown | zero, last-write unknown | from the record |
| `DataOverwrite`, `DataExtend` or `DataTruncation` | zero, size unknown | zero, last-write unknown | from the record |
| `BasicInfoChange` | kept, with its flag | zero, last-write unknown | from the record |
| Rename, move, close, any other reason | kept, with its flag | kept, with its flag | from the record |

A reason a record repeats from its open cycle never invalidates twice, and a close record that reports nothing new
only refreshes the attributes. A rescan writes the producer's observations and clears an unknown flag only
for a row and field whose producer obtains a known value. An MFT record with
no usable size remains size-unknown, and an enumeration root whose metadata
cannot be read remains last-write-time-unknown. A last-write range
query excludes every row flagged last-write-unknown, as a size range query excludes size-unknown rows.

A deleted row has the in-use and tombstone flags together, whatever deleted it: a journal delete sets
tombstone on a live row, and a scan that imports freed records writes the same two flags, so
`LiveRowCount` excludes both. A freed row whose parent chain verified at scan time keeps its parent
row. Any other freed row is detached: its parent column holds `uint.MaxValue`, no row has that index,
so it is nobody's child, its parent walk ends at once and its path is its bare name. Only a create
reuses a deleted row's slot; the journal never edits, renames or deletes it again.

Freed records are opt-in: `MftScanOptions.IncludeFreed` (default false) makes a cold scan of a
live volume, direct or through the broker, import them, and the dump source never does. A freed
row's parent chain verifies when every parent through the root is a directory whose stored
sequence equals the sequence the child's name referenced, or is one higher when that parent was
freed too (16-bit wraparound), within 128 components and without a cycle. Queries return
deleted rows only when `SearchQuery.IncludeDeleted` is true; `FileIndex.Find`, `FileIndex.Root`
and `FileEntry.Children` return live rows only.

Deleted rows are reverse-indexed by parent slot in memory only. `DeletedChildLinks`, owned by `BlockFile`, is built lazily once per block, kept current at every row write, rename, tombstone and detach, and consulted when a journal create reuses a slot, so only that slot's deleted descendants are detached. Each parent holds a hash set of its deleted children, so linking and unlinking a row is constant time however many deleted siblings share the parent. The index is not persisted in the block format: a block opened from a cache file carries no links until the first create that needs them.

Name identifier, reserved bits, and row flags sit adjacent at byte offset 8 to form
an 8-byte aligned 64-bit descriptor word. Rows are 32 bytes and the row region
starts at 4096 (a 4 KB boundary), so byte offset 8 in every row is 8-byte
aligned. A rename or flag update publishes via a single atomic 64-bit store
(`FileRow.WriteDescriptorWord`) without tearing.

The read rule is narrower than the write rule, and the difference is the whole
point of the layout:

- **Read the name identifier with one `FileRow.ReadDescriptorWord` on a live
  block.** Resolve that identifier through its immutable name entry, which supplies
  the byte offset, character length and encoding together. A rename publishes either
  the old identifier or the new one.
- **The flags may be read on their own.** The field is 2-byte aligned, so it
  cannot tear by itself, and the scan loops read it directly through `IsInUse`,
  `IsDirectory`, `IsDeleted`, `SizeKnown` and `SubtreeSkipped`. Pairing such a
  read with a separate name read is safe because entries and the name pool are append-only: a
  span built from a descriptor that is one rename stale still points at valid,
  immutable characters. Routing the flag predicates through the descriptor word
  would cost a 64-bit read plus three shifts per row on the hottest loop in the
  library and buy no correctness.
- **Every write goes through `FileRow.WriteDescriptorWord`**, including a write
  that only means to change the flags, which must round-trip the name identifier
  through the same call.
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
name entry table. The sequence region stores one sequence number per slot so readers can
derive a 64-bit file identifier in the form `(sequenceNumber << 48) | recordNumber`.
For slot capacity *N*, the region uses `N * 2` bytes, then aligns to 4 KB like all
other block regions.

## Capacity

The volume plan clamps the row estimate to 1,717,986,917 rows, then sets slot capacity `S` to the estimate plus
25 percent or 65536 rows, whichever is larger. Entry capacity `E` starts equal to `S`. The name byte pool reserves
`P = pageAlign(max(1 MiB, 24 * S))`, with no second headroom, clamped to 2147479552 bytes (`0x7FFFF000`) so every
pool span length fits an `int`. One planner serves the MFT and enumeration producers. The reservation is generous:
measured use is 2.7 to 15.2 bytes per slot.

Exhausting rows, name entries or name bytes sets the capacity-exhausted flag. A clear flag asks for no growth, even
when a region is full. With the flag set, the header identifies the exhausted regions: entries are exhausted when
`NameEntryCount >= E`; the pool is exhausted when at most 510 bytes remain; and rows are exhausted when
`RowCount >= S`, or when neither entries nor pool is exhausted. The row fallback covers a write at a record index
past `S`, which leaves `RowCount` below `S`. A name of at most 255 UTF-16 units needs at most 510 bytes plus one
alignment byte, so a pool refusal always leaves at most 510 bytes. When a row refusal coincides with entry or pool
exhaustion, rows keep the volume plan: fresh MFT geometry already covers every current record.

A rescan computes the volume plan, then doubles only the exhausted regions and applies those values as independent
minima. Slot and entry capacities clamp at 2147483646, leaving room for reserved name entry zero in the int-indexed
entry span; the pool clamps at `0x7FFFF000`. Every other region keeps its volume-plan value. For MFT, the index sends
these minima to the producer, which combines them with fresh volume geometry where that geometry is known.

An enumeration walk grows the same way without starting again: FileIndex copies the used rows, sequence numbers,
name entries and name bytes out, disposes and deletes the exhausted block, and restores them into a block with only
the exhausted regions doubled. Row indices and name identifiers are unchanged; bucket chains are rebuilt for the
replacement `E`, and the walk continues from its frontier without reading a directory twice. Growth is geometric,
so copies are bounded. Only a tree that still cannot fit at the clamps publishes an exhausted block.

An MFT producer or mutator keeps what fits and the volume remains Ready and queryable. Read
`RootBlockStatus.IsCapacityExhausted` to offer a rescan. The flag remains until a successful rescan publishes a
replacement without it; failed or cancelled rescans retain the old block and its flag.

## Name entries, byte pool and hash chains

Names are interned by exact spelling: two rows share a name entry exactly when their names are equal as ordinal
UTF-16 text, so `Makefile` and `makefile` are two entries and a case-only rename interns the new spelling and swaps
the row's identifier. Peers that kept the old spelling keep it, because entries are immutable. Each nonzero
identifier selects one append-only 12-byte entry:

| Entry offset | Field | Type | Meaning |
| --- | --- | --- | --- |
| 0 | pool byte offset | u32 | Offset within the byte pool |
| 4 | length and encoding | u16 | Low 15 bits: UTF-16 code-unit count; high bit: UTF-16 encoding |
| 6 | hash tag | u16 | High 16 bits of the name hash |
| 8 | next identifier | u32 | Previous head of this bucket; zero ends the chain |

An all-ASCII name stores one byte per character (0 through 127). Any other name, including one with an unpaired
surrogate, stores UTF-16LE, starting at an even byte offset; pool usage includes alignment padding. Maximum
name length is 32767 UTF-16 code units. `FileEntry.Name` constructs a string from either encoding exactly as written.
`IndexRow.Name` borrows UTF-16 text directly or uses the enumerator's reusable character buffer for ASCII text.

The stable hash is FNV-1a 64 over the little-endian UTF-16 units after the one fold routine: invariant simple
uppercase per UTF-16 unit, with `Rune` casing for a valid surrogate pair and an unpaired surrogate folding to
itself. Every casing of a name therefore hashes alike and shares one chain and one tag. A bucket is
`hash & (bucket count - 1)`. An exact lookup walks the bucket's decreasing identifier chain, compares the tag, then
compares the text ordinally; hash and tag collisions never establish identity without comparing the text.

The single writer runs under the root's existing write gate. On a hit it reuses the identifier, even when storage is
full. On a miss it checks pool and entry capacity, writes the bytes, writes the whole entry with its previous-head
link, publishes pool usage and entry count, and finally publishes the bucket head with a volatile write. Readers
acquire the published count or head before reading the immutable storage. The row descriptor is published afterwards
in one aligned 64-bit store. An interruption before bucket publication leaves unreachable storage, not a link to
unwritten bytes. Entries and bytes remain valid after rename or deletion.

### Validation

| Stage | Checks |
| --- | --- |
| Eager at open, constant cost | Magic and version, every region offset equal to the layout for the stored `S`, `E` and `P`, total size, `RowCount <= S`, `NameEntryCount <= E`, `NamePoolUsed <= P`, `NamePoolUsed >= NameEntryCount`, the bucket count the formula gives for `E`, root key, file name, producer, cache tag and scan shape. Section validation in the broker runs the same set on a private header copy. The four name-region facts report `BlockValidationResult.InvalidNameDescriptor`; the other geometry checks report `InconsistentRegions` |
| Lazy at each read | `NamePool.Read`: the identifier is within the published entry count, the length is nonzero, the offset plus bytes lie within the used pool, and a UTF-16 offset is even. A failure writes a `Diagnostics` line naming the block, the identifier and the failed check, then throws `InvalidDataException` to the query. A chain walk requires each identifier within the published count and each `Next` strictly smaller; a failed step ends the walk as not found |
| Not run in production | Tag correctness, bucket membership, reachability, ASCII validity and per-name rehash. A corrupt chain can only cause a missed exact match or a duplicate entry, never an out-of-bounds read. Tests run the full pass through an internal integrity checker |

Nothing is rebuilt or rehashed at open: an open of a block touches no name entry, bucket or pool page. The writer
trusts the chains it finds; a block written by MFTLib under the owner-only cache directory is the only input, and the
next rescan rewrites it.

## Name query passes

A query resolves the matching name entries once and then tests each row's atomically read identifier. An exact,
case-sensitive query walks the chain for the pattern and takes the single ordinal match. An exact, ignore-case query
walks that one chain and collects every entry the fold matches (an inline set of four identifiers, then a rented
array); `Find` under ignore case prefers the child spelled exactly as the segment, else the lowest row. Substring and
glob queries first scan the published name entries into a rented `ulong[]` bitset, then scan rows by testing the bit
for each row's identifier. The same setup serves materialized search and both enumeration APIs. A query without a name
predicate skips name matching.

Ignore-case matching is defined once: a name matches a pattern exactly when the ordinal match of the fold of the name
against the fold of the pattern succeeds. The pattern is folded and encoded once per query. An ASCII entry uses
byte operations when the folded pattern is ASCII (a pattern whose fold is not ASCII cannot match it, which is decided
in constant time); a UTF-16 entry is folded into a reusable buffer and matched as characters. A case-sensitive query
compares the original pattern ordinally, so a case-sensitive substring is an ordinal search for the original text. A
glob is always wildcard matching (`?` one unit, `*` any run), never reduced to one literal test. `ı` (U+0131) keeps its
own fold because .NET leaves it alone under invariant casing; `ſ` (U+017F) folds to ASCII `S`.

The entry pass captures its count once, so identifiers appended afterwards do not match that pass. Contents are not
frozen: an existing row can change between matching and consumption. Borrow ownership spans both passes.
Cancellation is checked before setup, at least every 4096 name entries, before the first row and at least every 4096
scanned rows, including rejected rows. Bitsets are returned on completion, disposal and failed setup. See
[query lifetime](query-lifetime.md).

## Measuring name storage

`Benchmark.exe index-storage --synthetic 3000000` measures managed synthetic input generation and the production MFT
row writer. The run uses seed 42: random 20-character ASCII names, every tenth record a directory, `.git` and `HEAD`
once per hundred records, and root record 5. These mostly distinct random names give little deduplication benefit;
real-drive name repetition is a different workload.

The reported scan write time excludes block allocation, completion flush and cleanup. Storage output separates pool
usage, occupied name entries (12 bytes per entry, excluding reserved entry zero), page-rounded reserved entry storage
and the whole bucket directory. A single run is a throughput observation, not a statistical performance comparison.
The temporary block is deleted after measurement.

## Enumeration blocks

An enumeration producer has no record numbers. Its single writer assigns dense
row numbers as directory results arrive, with root row 0 parenting itself and
every other parent row preceding its children. Row order between directories is
unspecified and can differ between scans. Parent columns retain the same shape
as other blocks. Enumeration blocks have no journal cursor and their
`IndexRecordKey` reports `ProducerKind.Enumeration`.

## MFT blocks

Rows are dense by NTFS record number: row i is record i, with unused slots left
empty and the volume root at row 5 (`BlockHeader.RootRow`). `RowCount` is the
highest written slot plus one. `LiveRowCount` counts records that are in use and
not tombstoned, excluding free slots and deleted files. Use the live count when
displaying a file count; `RootBlockStatus.LiveRowCount` reads it from the header. A
size-unknown row carries `RowFlags.SizeUnknown` and a zero size when no usable data size was
found in the base record, including data in an extension record that the parser
does not follow or a negative non-resident data size. A known zero size means an
empty file or a directory.

The non-elevated client plans capacities from `NtfsVolumeInformation` and creates
the block file and its named section. The elevated broker opens that section and
writes rows and names directly from parse batches, without resolving full paths.
It stamps the armed journal cursor and writes the completed header last; a crash
before completion leaves a block the client discards.

The section header is shared with a less trusted process, so the broker treats it
as input. `NamedBlockSection.OpenExisting` maps the whole section, takes one private
copy of the header and validates it with `BlockHeader.ValidateSection`: magic,
version, row and name-entry counts and name-pool used within their capacities, the
bucket count the formula gives for the entry capacity, every region offset
equal to the `BlockLayout` value for those capacities, and a total block size equal
to the mapped length. A rejected section fails the scan request with an `Error`
frame naming the `BlockValidationResult`. Every `BlockFile` keeps the capacities of
the copy it was built from and derives its region offsets from them; spans and
writer bounds checks never read the mapped header. The writer tracks row count and
name-pool used and name-entry count itself and publishes each change to the header without reading it
back: the creator writes the header's capacities and offsets once, the writer
publishes its cursors, and only validation and display read them. After the broker reports the scan
ready, the client validates the completed header and adopts its published cursors
only when its capacities equal the client block's own.

Journal mutation coalesces NTFS close records. NTFS ends every open cycle with a record
that repeats the cycle's reasons plus `USN_REASON_CLOSE`, and the watch reads with
`ReturnOnlyOnClose = 0`, so every intermediate record arrives. `JournalMutator` tracks,
per root block, the reasons each row's open cycle already reported; a close record that
adds no new reason refreshes the row's attributes without emitting a
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

An MFT-backed `FileIndex` has one watch instance and catch-up state for its root. Each
lifecycle method returns one `RootOperationResult` for the index's root. When
`FileIndex.StartWatchingAsync(cancellationToken)` starts the root's watch, or a rescan restarts
one whose watch is still requested, the root arms from the journal cursor persisted in its
block header (`USN journal identifier` and `USN next USN`). `StartWatchingAsync` returns once
the channel is connected and the start request is written; catch-up is observed separately.

The source captures the journal tip for the arm. Backlog batches between the persisted
cursor and that tip are streamed and applied to the block in place, advancing the header's
`USN next USN` and updating `LiveRowCount` and file rows under the volume's write gate. A
`VolumeCaughtUp` item transitions `RootWatchStatus.State` from
`WatchState.CatchingUp` to `WatchState.CaughtUp`. Live mutations continue on
the same watch instance.

`FileIndex.WaitForCatchUpAsync(cancellationToken)` returns one `RootOperationResult` for the
index's root, following its current watch instance or pending replacement. Its result succeeds
immediately when the root is already caught up, is `Failed` with the root's exception in
`Failure` when the watch failed or its last start was refused, and is `NotApplicable` when the
root's watch is not requested and it has no current watch. Stop or committed replacement retires
a pending watch instance and settles its wait as `Failed` with an `OperationCanceledException`
in `Failure`. A rescan keeps a healthy watch and its waits attached during production and retires
the old watch only when the replacement commits; a wait issued after that commit and before the
replacement registers follows the pending replacement. Failed or cancelled production leaves the
healthy watch and its waits untouched. Caller cancellation cancels the wait task without
stopping the root's watch; index disposal cancels the wait task through the index's disposal
token.

A `Volume` or `Apply` fault moves the root to `WatchState.Recovering` and starts an
automatic rescan. A wait issued during recovery settles as `Failed` with the exception that
ended the prior watch. After recovery starts the replacement watch and the root reads
`CatchingUp`, call `WaitForCatchUpAsync(cancellationToken)` again to follow that instance. If
recovery fails, or the replacement watch faults before reaching `CaughtUp`, the index raises a
`Recovery` fault and leaves the volume `Faulted` until a consumer starts or rescans it. A
`Channel` fault does not recover automatically. A successful manual rescan whose replacement
watch cannot start raises `RescanRestart` and leaves the root `Faulted` without automatic
recovery; the scan returns normally. The fault and `RootWatchStatus.FailureMessage` name the
rescan, and the start failure is the inner exception. Stop returns the fault once as `Failed`.
Across a successful swap, old `Changed` events can arrive after publication and repeat during
the new watch's catch-up; queries can lag until it reports `CaughtUp`.

Calling the wait is optional. The index observes the watch's failure even when no caller waits;
faults still reach `WatchFaulted`, `RootWatchStatus.FailureMessage`, and
`RootWatchStatus.State`.

## Sidecars

A follow-up children table or name index is a separate file next to the block.
It is never a format change. A sidecar is bound to the block file it was built from,
not to `generation`, which restarts when a cache is deleted or a process starts anew.
