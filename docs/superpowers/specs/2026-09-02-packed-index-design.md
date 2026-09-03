# Packed columnar file index: design

Status: owner-approved brainstorm output, 2026-09-02. Anchor issue:
https://gitea.fleet.sticktoitive.net/schoen/MFTLib/issues/112. Inputs: the two
code-trace reports attached to
https://gitea.fleet.sticktoitive.net/schoen/file-wizard/issues/347 and the
measurement posted there (21.3M files, 5 drives: cold settled 13.8 GB, rescan
peak 26.8 GB, warm 10.5 GB).

This spec is scaffolding for plan-writing. It gets distilled into the plan
header and deleted at the next handoff.

## 1. Goal and non-goals

Goal: one representation of a volume's file inventory that is the live index,
the on-disk cache, and the broker transfer format at the same time, small
enough that 21M files cost about 1.5 GB on disk and only touched pages in
memory, with an ergonomic C# query surface that hands out lists of value-type
handles and never asks client code to walk pointers.

Non-goals for v1: secondary indexes, name interning, a children table, subtree
exclusion, a hard-link model beyond one name per record, any migration of the
existing `.fwc` cache. All are follow-ups gated on a measurement.

## 2. Decisions locked with the owner

1. The block lives in the cache file. The client creates the drive's block
   file, maps it as a named section, and the broker opens the section by name
   and writes rows into it as it parses. Cold scan and cache save are one act.
2. Persistent by default. A no-cache mode opens the block with delete-on-close
   so the OS removes it when the process dies, for users who do not want
   whole-volume metadata on disk or want the space back.
3. Live values. Journal events mutate rows in place. A held handle never
   dangles and never reads garbage, but its values are current, not frozen. A
   deleted file reads as `IsDeleted`. Across a rescan the old block stays
   mapped until the last handle from it is released.
4. MFTLib reads the MFT; the index is substrate-neutral. The block format,
   queries, snapshots, mutation, and a directory-enumeration producer live in
   the `MFTLib.Index` namespace with no native dependency. The MFT producer is
   one writer of blocks; enumeration is the second and covers non-NTFS
   volumes, network shares, and Linux. No drive is ever "not indexed" because
   of its substrate.
5. One assembly, one NuGet package, name stays MFTLib. Layering is a namespace
   boundary enforced by an aislop architecture rule: `MFTLib.Index` never
   references `MFTLib.Mft`, `MFTLib.Broker`, or `MFTLib.Internal`. The README
   gets a sentence explaining the not-just-MFT scope.
6. Search materializes its match set. `Count` is up front, pages are slices,
   sort is a sort over 16-byte handles. No cursors, no infinite scroll.
7. No secondary indexes in v1. Every query is a parallel column scan. The
   first follow-up is a compressed-sparse-row children table for `Children()`,
   gated on measured expand latency in the tree UI.
8. Size and modified time are real columns. The native parser starts reading
   `$STANDARD_INFORMATION` and the `$DATA` sizes. Today both are always zero.

## 3. Block format

One file per volume, named by drive letter plus volume serial so a re-lettered
drive never matches the wrong block. Little-endian. Regions are 4 KB aligned.

Header, one page:

| Field | Type | Notes |
|---|---|---|
| magic | u32 | `MLIX` |
| format version | u32 | mismatch means discard and rescan |
| producer kind | u32 | 1 = MFT, 2 = enumeration |
| flags | u32 | complete, compaction needed |
| volume serial | u32 | |
| scan timestamp | i64 | UTC ticks |
| row count | u32 | highest used slot + 1 |
| slot capacity | u32 | rows region size in rows |
| name pool used | u32 | bytes |
| name pool capacity | u32 | bytes |
| USN journal id | u64 | 0 for enumeration blocks |
| USN next USN | i64 | |
| generation | u64 | bumped per mutation batch |
| row region offset | u64 | |
| name pool offset | u64 | |

Row region: one 32-byte row per MFT slot, dense by record number, so row i is
record i and no lookup table exists.

| Offset | Field | Type | Notes |
|---|---|---|---|
| 0 | parent row | u32 | row index of the parent; root points at itself |
| 4 | attributes | u32 | NTFS file attributes |
| 8 | name offset | u32 | bytes into the name pool |
| 12 | name length | u16 | UTF-16 code units |
| 14 | row flags | u16 | in use, directory, tombstone, size unknown |
| 16 | size | u64 | bytes; 0 for directories |
| 24 | modified | i64 | UTC ticks |

Name offset, name length, and row flags sit together at byte offset 8 so they
form one 8-byte aligned 64-bit descriptor word. Rows are 32 bytes and the row
region starts at a 4 KB boundary, so that word is aligned in every row, and a
rename can publish a new name with a single atomic store instead of two
independent ones that a reader could observe half of.

Capacity is the MFT slot count plus headroom (25 percent, minimum 64K rows)
so journal creates land in place at their record number.

Name pool: UTF-16, append only, sized from the parse plus the same headroom
ratio. A rename appends the new name and then publishes the new offset and
length as one store of the descriptor word. A reader sees the old name or the
new one, never a torn one. Names are not interned in v1.

Enumeration blocks have no record numbers. The producer assigns rows
sequentially in traversal order with the parent column in the same shape.
Nothing downstream can tell the difference except the producer kind and the
absence of a cursor.

Versioning: a version mismatch, a missing complete flag, or a serial mismatch
means discard and rescan. There is no migration code. The block is rebuildable
from the substrate by definition.

Sidecar room: a follow-up children table or name index is a separate file next
to the block, keyed by the block's generation, never a format change.

## 4. Producers

### 4.1 MFT producer (Windows, elevated broker)

Native parser changes:

- Extract modified time from `$STANDARD_INFORMATION` (always resident).
- Extract size from the unnamed `$DATA` attribute: value length when resident,
  data size when non-resident. Directories get 0. When `$DATA` is absent from
  the base record (attribute list in an extension record) set the size-unknown
  flag and leave 0; consumers may fall back to a file-info call for those.
- Keep one name and one parent per record, the first non-DOS `$FILE_NAME`, as
  today. Hard links are a follow-up.
- Drop path resolution from the broker path entirely. The `PathLookup` table,
  the resolve phase, and the resolve progress phase go away for block output.
  The compact entry gains size and modified time.

Broker write path:

- The client creates the block file at planned size (slot count from
  `VolumeInfo`, name pool from slot count times a per-machine average name
  length with headroom), maps it as a named section, and passes the section
  name in the `ArmAndScan` spec exactly as it passes the page-file map name
  today.
- The broker opens the section by name and writes rows and names directly as
  chunks come off the parser. Broker memory is bounded by the parse chunk
  size. There is no name table, no path pool, no merge step.
- `ScanReady` carries row count and name pool used. `ScanPayload` v2 and the
  48-byte record format are retired.
- Header is written last with the complete flag. A broker crash mid-write
  leaves an incomplete block that the client discards.
- Catch-up, cursors, watch, `ReplaceWatchCursors`, progress frames, and
  `BrokerScanProfile` keep their current protocol. `DirectoryIndex` becomes a
  producer-side filter on which rows are marked in use.

### 4.2 Enumeration producer (any OS, any filesystem)

Managed, in `MFTLib.Index`. Walks the volume with
`FileSystemEnumerable<T>` and `FileSystemEntry` so no path or name string is
allocated per entry, with large-fetch enabled on Windows so a network share
pays one round trip per buffer. Writes rows straight into the block as it
walks. Emits progress on the same shape as the broker. No cursor, no live
watch; a drive on this producer shows "rescan to refresh" on its card.

## 5. Client: `FileIndex`

### 5.1 Open and lifetime

`FileIndex.OpenAsync(FileIndexOptions, ct)`. Options: drive set, cache
directory, no-cache mode, producer policy (auto, MFT only, enumeration only),
broker launcher. Per drive it picks the producer: NTFS plus broker available
means MFT, else enumeration. A drive with a valid block warm-starts by mapping
it. A drive with no block or an invalid one cold-scans.

The cache directory gets an explicit ACL at creation: owner and SYSTEM, no
inherited Administrators or Users entries.

In no-cache mode each block is created in the temp directory with
delete-on-close and never written to the cache directory.

### 5.2 Snapshots and handles

A `DriveBlock` is one mapped block plus a reference count. A `Snapshot` is a
reference to the set of drive blocks current at a moment. `FileEntry` is a
16-byte readonly record struct: a snapshot reference and a row locator (drive
ordinal plus row index). Property reads go straight to the mapped row.
`Name` and `Path` allocate; nothing else does.

A rescan writes a new block file beside the old one, swaps it into the current
snapshot, and drops the old block's reference. Handles from the old snapshot
keep it mapped until they are collected. The old file is deleted when its
mapping closes.

### 5.3 Paths and opening

`Path` walks the parent column upward, collecting name spans, and builds the
string once. Depth is capped and cycles are guarded exactly as the native
resolver does today.

`Open` uses the NTFS file id. It opens the volume root directory with backup
semantics as the reference handle, which needs no elevation, and calls
`OpenFileById`. Enumeration-producer entries have no file id and open by path.

### 5.4 Journal mutation

The existing watch pipeline delivers `UsnJournalEntry` batches. The index
applies them in place:

- Create: fill the row at the record number, append the name, set in use.
- Delete: set tombstone, keep the name so a Recent Changes line can still say
  what was deleted.
- Rename or move: append the new name if changed, write the parent row, then
  publish the new name offset and length as one descriptor-word store.
- Record number at or past slot capacity, or name pool exhausted: set the
  compaction-needed flag, keep applying what fits, and report the drive as
  stale so the UI offers a rescan. Compaction is the same as a rescan on the
  MFT producer.

The USN cursor is written into the header in place after each batch, the same
16-byte write that exists today, so a warm start resumes from it.

Each mutation batch bumps the generation. Sidecar indexes compare generations.

### 5.5 Queries

```csharp
public sealed class FileIndex : IAsyncDisposable
{
    public static Task<FileIndex> OpenAsync(FileIndexOptions options, CancellationToken ct);
    public Task StartWatchingAsync(CancellationToken ct);
    public event Action<FileChange> Changed;
    public IReadOnlyList<DriveStatus> Drives { get; }

    public FileEntry? Find(string fullPath);
    public IReadOnlyList<FileEntry> FindByName(string name);
    public IReadOnlyList<FileEntry> Search(SearchQuery query);
    public IReadOnlyList<FileEntry> Largest(int count, FileEntry? under = null);
    public IReadOnlyList<DuplicateGroup> DuplicateNames();
    public FileEntry Root(char drive);
}

public readonly record struct FileEntry
{
    public FileId Id { get; }            // drive + record number, or synthetic
    public string Name { get; }
    public string Path { get; }
    public long Size { get; }
    public bool SizeKnown { get; }
    public DateTime Modified { get; }
    public FileAttributes Attributes { get; }
    public bool IsDirectory { get; }
    public bool IsDeleted { get; }
    public FileEntry? Parent { get; }
    public IReadOnlyList<FileEntry> Children();
    public FileStream Open(FileAccess access);
}

public sealed record SearchQuery(
    string? NamePattern,          // substring by default, * and ? glob
    bool CaseSensitive = false,
    FileEntry? Under = null,      // subtree restriction
    bool? Directories = null,     // null = both
    long? MinSize = null, long? MaxSize = null,
    DateTime? ModifiedAfter = null, DateTime? ModifiedBefore = null);
```

Every query is a parallel scan over the relevant columns of every current
drive block and materializes a `List<FileEntry>`. `Search` returns the whole
match set; callers page by slicing. `FindByName` and `DuplicateNames` compare
UTF-16 spans against the pool with NTFS case folding. `DuplicateNames` chains
a fixed number of transient fixed-size hash sieve passes over name hashes,
narrowing the candidate set before materializing any name, and discards the
whole chain.
`Largest` is a partial sort by the size column. `Children` scans the parent
column. `Find(fullPath)` walks down from the root by name at each level.

Hot internals and a `Scan(...)` escape hatch expose ref-struct enumerators over
spans for consumers that need them. The public surface is lists.

## 6. Consumers

### 6.1 file-wizard

Delete: `FileDatabase` and its partials, `FileMetadata`, `CachedFileIndex`,
`FileWizardCache`, `FileWizardCacheFormat`, `RecordPathResolver`,
`UsnDeltaApplicator`, the `.fwc` format, the recursive scan, and
`BrokerScanMapSizing`. `JournalWatcher`, the cross-drive rename correlator,
and the Recent Changes feed stay and consume `FileIndex.Changed`.

CLI: `--find` becomes `Search`; `--largest` becomes `Largest`; `--duplicates`
becomes `DuplicateNames`; `--path` becomes `Under`. `--cache-only` maps blocks
without a producer. `--drives` from PR 345 becomes `FileIndexOptions.Drives`.

MAUI: drive cards read `Drives`; dashboard totals read counts off the header;
the #338 search page calls `Search` and pages the returned list with a count.

Content dedup: candidate selection groups on the size column; hashing opens
candidates by id.

### 6.2 git-wizard

`FindByName(".git")`, filter `IsDirectory`, take `Parent.Path`. The watch path
consumes `Changed` and no longer keeps its own record-number map.
`BrokerScanProfile.DirectoryIndex` with `keepFileNames` keeps working as the
producer-side filter.

### 6.3 Documentation

MFTLib README: one paragraph on the index and the enumeration producer, one
sentence on the name. `docs/broker-integration.md`: block write path replaces
the `ScanPayload` section. Both consumer READMEs: the cache is whole-volume
file metadata in your profile, readable by anything running as you; no-cache
mode exists.

## 7. Error handling

- Incomplete, wrong version, or wrong serial block: discard, cold scan, log why.
- Broker crash mid-write: the complete flag is never set; same as above.
- Slot or name pool exhaustion during watch: compaction-needed flag, drive
  reported stale, rescan offered. Never a crash, never a silent drop.
- Journal wrap: existing MFTLib#99 behavior, watch from current with a Warning.
- Drive unavailable at open: block stays on disk, drive reported offline,
  handles from it are never created.
- Enumeration producer access denied on a subtree: row marked, subtree skipped,
  counted in the drive's warning, traversal continues.

## 8. Testing and measurement

Linux and Windows, no elevation: the enumeration producer against generated
temp trees; every query; every mutation path including capacity exhaustion;
snapshot swap with held handles; block validation on corrupted headers.

Windows, no elevation: the MFT producer against `GenerateSyntheticMFT` files
through `ParseMFTFromFile`, checking size and modified extraction against
known synthetic values; the broker write path through the existing in-process
pipe harness with a `RecordingMmfWriter` replaced by a block writer.

Attended, per the crew rule filed as schoen-lab#2360: `scripts/measure-memory`
before and after on the same 21.3M-file machine. Targets: cold settled client
under 1 GB, warm under 300 MB, cold scan wall clock bounded by MFT read time,
a name search under 200 ms warm, and the block sizes in section 3's arithmetic
(about 1.5 GB total for these five drives).

## 9. Sequencing

Starts after file-wizard PR 345 lands. Five plans, in order, each its own
issue and PR train:

1. `MFTLib.Index`: block format, `FileIndex`, snapshots, queries, mutation,
   enumeration producer. Fully testable on Linux.
2. Native size and modified extraction, the block-writing MFT producer, the
   broker write path, `ScanPayload` retirement.
3. file-wizard port and deletion of the old index and cache.
4. git-wizard port.
5. Docs, README, aislop architecture rule, measurement, then the 0.3.0 track
   resumes.

## 10. Deferred, with the trigger that revives each

- Children table (CSR): tree UI expand latency measured above 50 ms.
- Name interning: name pool measured above 40 percent of block size.
- Secondary name index: `FindByName` measured above 200 ms warm.
- Subtree exclusion: a user asks.
- Hard links as multiple rows per record: a consumer needs the second name.
