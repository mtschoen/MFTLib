# Block ownership

## Preparing the cache directory

`CacheMode.Persistent` (the default for a non-dump source) and `ExistingOnly`
require a nonblank `FileIndexOptions.CacheDirectory` and a non-null `FileIndexOptions.Root`.
Missing configuration throws `ArgumentException` before directory creation.
`CacheMode.Disabled` needs no directory and ignores a supplied path without
resolving or creating it. Dump sources validate first and supply their own volume:
`Root`, `CacheMode` and `CacheDirectory`
must be null, and `CacheTag` must be default. Other values throw
`ArgumentException` naming the option, including every non-null cache mode.
A dump is never cached.

The application chooses a nonblank, application-owned
`FileIndexOptions.CacheDirectory`. `CacheDirectory.EnsureCreated(path)` returns
a `DirectoryInfo`, creating the directory with protection already in place or
reapplying that protection if it exists. On Windows the current user and SYSTEM
receive full control and inherited access-control entries are blocked. On Unix
the directory has owner read/write/execute permissions (`0700`). This protects
the directory; it is not a recursive repair of every existing file's permissions.

Cached `FileIndex.OpenAsync` calls `EnsureCreated` automatically before opening
roots. An application preparing the directory or writing its own sidecar before
an index open should call it explicitly first. For example, file-wizard's
`FileWizard/ContentHashSidecar.cs` calls it before using `content-hashes` inside
the index-cache directory. `CacheMode.Disabled` opens do not create a cache directory.
Dump sources require null `CacheMode` and `CacheDirectory` and default `CacheTag`. Directory preparation takes no block-slot
ownership; block operations still follow the locks described below.

- Index contracts:
    - **Block ownership**: a cache-mode root's `RootRuntime` holds a sibling `<block>.lock` (opened with
      `FileShare.None`, an exclusive non-blocking `flock` on Unix) for as long as it owns a canonical
      cache block, and that lock is taken before any validation, retired-sibling sweep, rename, or
      delete. A second `FileIndex` over the same cache directory, in any process, that cannot take the
      lock reports the root `Failed` with `RootFailureKind.InUse` on a cache-only open; a non-cache-only
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
      roots = null)` is the lock-safe way for a consumer to clear cache blocks: it takes each
      candidate block's owner lock non-blockingly and deletes only while holding it, the same rule `FileIndex`
      itself follows, so it never deletes, renames, or unlinks a block a live `FileIndex` still owns. A block
      whose lock is already held reports `CachedBlockDeletionOutcome.InUse` and is left untouched; a delete
      that fails after the lock is taken reports `Failed` with the filesystem error message, and neither
      outcome stops the remaining candidates. `.lock` files are never deleted by this path either, for the
      same create-and-lock race reason as above. Each attempt returns a `CachedBlockDeletionResult` (the
      `CachedBlockFile` inventory entry, the `CachedBlockDeletionOutcome`, and a `FailureMessage` that is
      non-null only for `Failed`); an already-absent file is reported `Deleted`, an idempotent success rather
      than a `Failed`. Consumers render or log the returned results after the call returns. `CacheDirectory.InspectCached(cacheDirectoryPath,
      roots = null, rejectedFile = null)` is one public method with optional root selection
      and an optional `Action<CachedBlockRejection>? rejectedFile` callback carrying a full `Path` and
      human-readable `Reason`. Omitting the callback silently excludes rejected names. `DeleteCached`
      keeps an internal overload accepting `Action<CachedBlockRejection>? rejectedFile`;
      rejected-name reporting is not part of its public API. Canonical result lists contain only filenames the
      library recognizes. Reporting happens synchronously
      during eager enumeration, before root filtering and any inspection/deletion lock, including for empty
      selections; callback exceptions propagate before canonical work. Rejected files are never opened, locked,
      or deleted. Consumers can collect these reports to
      preserve their own corruption diagnostics without globbing the cache directory themselves.
      A scan into the canonical slot that fails or is cancelled without producing a block deletes its
      partial file itself, while `RootRuntime` still holds the owner lock, and logs the delete (or the
      failure to delete) through `Diagnostics`; a throwing callback never replaces the scan's own exception.
      A file that already sat in the slot before the scan, such as a complete block rejected only for an
      unresumable checkpoint, is judged by what is there after the failure: it stays only while it still
      opens as a complete, valid block, and a truncated or half-written replacement is deleted. A rescan
      restores its renamed-aside previous block instead; a throwing callback there neither replaces the scan's
      exception nor stops the previous block from returning to the canonical slot.
      `RootBlockStatus.CacheLocation` reports the current published block's backing, read from the root owner's adopted `LastResult.CacheLocation`. The root owner (`RootRuntime`) holds the canonical lock (`CanonicalLock`) when it owns the slot, but `LastResult.CacheLocation` describes the published block:
      `CacheLocation.OwnedCanonical` for the canonical slot held by this index,
      `PrivateFallback` for a private fallback block, and null for
      a `CacheMode.Disabled` block (a blockless root has no `Block` at all). It is independent of
      `BlockOrigin`: both canonical and private scans report `ProducedByScan`.
      The status is captured under the state lock; reread `FileIndex.Status` after
      a rescan. Taking a lock for an in-flight rescan does not change the old
      private block's status; only publishing the replacement does. A failed or
      cancelled scan that leaves the old block in place leaves its backing status
      in place too. The value identifies no process and does not promise that a
      private block's slot is still held elsewhere.
