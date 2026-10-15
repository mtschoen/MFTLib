# Block ownership

## Preparing the cache directory

The application chooses a nonblank, application-owned
`FileIndexOptions.CacheDirectory`. `CacheDirectory.EnsureCreated(path)` returns
a `DirectoryInfo`, creating the directory with protection already in place or
reapplying that protection if it exists. On Windows the current user and SYSTEM
receive full control and inherited access-control entries are blocked. On Unix
the directory has owner read/write/execute permissions (`0700`). This protects
the directory; it is not a recursive repair of every existing file's permissions.

Cached `FileIndex.OpenAsync` calls `EnsureCreated` automatically before opening
drives. An application preparing the directory or writing its own sidecar before
an index open should call it explicitly first. For example, file-wizard's
`FileWizard/ContentHashSidecar.cs` calls it before using `content-hashes` inside
the index-cache directory. `CacheMode.Disabled` opens do not create a cache directory, and
dump sources prohibit cache options. Directory preparation takes no block-slot
ownership; block operations still follow the locks described below.

- Index contracts:
    - **Block ownership**: a cache-mode `FileIndex` holds a sibling `<block>.lock` (opened with
      `FileShare.None`, an exclusive non-blocking `flock` on Unix) for as long as it owns a canonical
      cache block, and that lock is taken before any validation, retired-sibling sweep, rename, or
      delete. A second `FileIndex` over the same cache directory, in any process, that cannot take the
      lock reports the drive `Failed` with `DriveFailureKind.InUse` on a cache-only open; a non-cache-only
      open instead scans into a private `mftlib-private-*` delete-on-close block and leaves the canonical
      cache untouched. `.lock` files are deliberately never unlinked (unlinking races a fresh
      create-and-lock), so cache-pruning tooling must leave `*.mlix.lock` alone. `FileIndexOptions.Diagnostics`
      receives one line with the path and reason for each block file the index deletes
      explicitly; null by default (the internal block creation options forward it). Consumers that deliberately shared one cache block between two live indexes
      must open the second with `CacheMode.Disabled` or expect the in-use outcome.
      `CacheMode.Disabled` and private fallback blocks are created delete-on-close, so the
      operating system removes them when the last handle closes, including when the process is
      killed, and that removal produces no `Diagnostics` line. The operating system likewise
      releases the owner lock when its process dies, so a killed index never strands its slot.
      `CacheDirectory.DeleteCached(cacheDirectoryPath,
      drives, diagnostics)` is the lock-safe way for a consumer to clear cache blocks: it takes each
      candidate block's owner lock non-blockingly and deletes only while holding it, the same rule `FileIndex`
      itself follows, so it never deletes, renames, or unlinks a block a live `FileIndex` still owns. A block
      whose lock is already held reports `CachedBlockDeletionOutcome.InUse` and is left untouched; a delete
      that fails after the lock is taken reports `Failed` with the filesystem error message, and neither
      outcome stops the remaining candidates. `.lock` files are never deleted by this path either, for the
      same create-and-lock race reason as above. Each attempt returns a `CachedBlockDeletionResult` (the
      `CachedBlockFile` inventory entry, the `CachedBlockDeletionOutcome`, and a `FailureReason` that is
      non-null only for `Failed`); an already-absent file is reported `Deleted`, an idempotent success rather
      than a `Failed`. A successful delete logs through the optional `diagnostics` callback with the same
      "Deleted block file '...'" shape `FileIndexOptions.Diagnostics` uses elsewhere, invoked synchronously
      while the block's lock is still held. `CacheDirectory.InspectCached(cacheDirectoryPath,
      drives = null, rejectedFile = null)` is one public method with optional drive selection
      and an optional `Action<CachedBlockRejection>? rejectedFile` callback carrying a full `Path` and
      human-readable `Reason`. Omitting the callback silently excludes rejected names. `DeleteCached`
      retains its public success logger and its internal rejection-callback overload; rejected-name
      reporting is not part of its public API. Canonical result lists contain only filenames the
      library recognizes. Reporting happens synchronously
      during eager enumeration, before drive filtering and any inspection/deletion lock, including for empty
      selections; callback exceptions propagate before canonical work. Rejected files are never opened, locked,
      or deleted. The deletion success logger remains separate. Consumers can collect these reports to
      preserve their own corruption diagnostics without globbing the cache directory themselves.
      A scan into the canonical slot that fails or is cancelled without producing a block deletes its
      partial file itself, while this index still holds the owner lock, and logs the delete (or the
      failure to delete) through `Diagnostics`; a throwing callback never replaces the scan's own exception.
      A file that already sat in the slot before the scan, such as a complete block rejected only for an
      unresumable checkpoint, is judged by what is there after the failure: it stays only while it still
      opens as a complete, valid block, and a truncated or half-written replacement is deleted. A rescan
      restores its renamed-aside previous block instead.
      `DriveBlockStatus.CacheSlot` reports the current published block's backing:
      `CacheSlotState.OwnedCanonical` for the canonical slot held by this index,
      `PrivateFallback` for a private fallback block, and null for
      a `CacheMode.Disabled` block (a blockless drive has no `Block` at all). It is independent of
      `BlockSource`: both canonical and private scans report `ProducedByScan`.
      The status is captured under the state lock; reread `FileIndex.Drives` after
      a rescan. Taking a lock for an in-flight rescan does not change the old
      private block's status; only publishing the replacement does. A failed or
      cancelled scan that leaves the old block in place leaves its backing status
      in place too. The value identifies no process and does not promise that a
      private block's slot is still held elsewhere.
