# Block ownership

- Index contracts:
    - **Block ownership**: a cache-mode `FileIndex` holds a sibling `<block>.lock` (opened with
      `FileShare.None`, an exclusive non-blocking `flock` on Unix) for as long as it owns a canonical
      cache block, and that lock is taken before any validation, retired-sibling sweep, rename, or
      delete. A second `FileIndex` over the same cache directory, in any process, that cannot take the
      lock reports the drive `Failed` with `DriveFailureKind.InUse` on a cache-only open; a non-cache-only
      open instead scans into a private `mftlib-private-*` delete-on-close block and leaves the canonical
      cache untouched. `.lock` files are deliberately never unlinked (unlinking races a fresh
      create-and-lock), so cache-pruning tooling must leave `*.mlix.lock` alone. `FileIndexOptions.Diagnostics`
      and `BlockFileCreateOptions.Diagnostics` receive one line per block-file delete with the path and
      reason; null by default. Consumers that deliberately shared one cache block between two live indexes
      must open the second with `NoCache` or expect the in-use outcome. `CacheDirectory.DeleteCached(cacheDirectoryPath,
      driveLetters, diagnostics)` is the lock-safe way for a consumer to clear cache blocks: it takes each
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
      while the block's lock is still held. `CacheDirectory.EnumerateCached`, `InspectCached`, and
      `DeleteCached` also have callback overloads that report rejected filenames via
      `Action<CachedBlockRejection>? rejectedFile`, carrying a full `Path` and human-readable `Reason`.
      Existing overloads and canonical result lists retain their behavior. Reporting happens synchronously
      during eager enumeration, before drive filtering and any inspection/deletion lock, including for empty
      selections; callback exceptions propagate before canonical work. Rejected files are never opened, locked,
      or deleted. The deletion success logger remains separate. Consumers can collect these reports to
      preserve their own corruption diagnostics without globbing the cache directory themselves.
      `DriveStatus.CacheSlot` reports the current published block's backing:
      `CacheSlotState.OwnedCanonical` for the canonical slot held by this index,
      `PrivateFallback` for a private fallback block, and `NotApplicable` for
      NoCache and blockless (failed or offline) drives. It is independent of
      `BlockSource`: both canonical and private scans report `ProducedByScan`.
      The status is captured under the state lock; reread `FileIndex.Drives` after
      a rescan. Taking a lock for an in-flight rescan does not change the old
      private block's status; only publishing the replacement does. A failed or
      cancelled scan that leaves the old block in place leaves its backing status
      in place too. The value identifies no process and does not promise that a
      private block's slot is still held elsewhere.
