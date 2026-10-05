# Query and writer lifetime

- Index contracts:
    - **Query lifetime**: the eight entry points that scan rows (`Find`, `FindByName`, `Search`, `Enumerate`, `Largest`,
      `DuplicateNames`, `Root`, and `FileEntry.Children`) each take an optional `CancellationToken`, observed
      before the first row and then at least every 4096 rows from inside `RowScanner`, and each holds a borrow
      on the `Snapshot` it reads for its whole duration. `FileEntry.Children` observes only its caller's token,
      since a handle holds no index reference; disposal waits that listing out rather than cancelling it. Every
      other `FileEntry` member reads one row and keeps the per-access `IsReleased` check instead.
      `SnapshotRelease` counts borrows; `DisposeAsync` cancels a disposal token the seven `FileIndex` queries
      and `WaitForCatchUpAsync` waits are linked to, waits for the borrows on the current and retired snapshots
      to drop, and only then unmaps. A suspended `Enumerate` enumerator holds its borrow between yields, so
      disposal waits until it advances or is disposed, or, if abandoned, until garbage collection and finalization
      return the borrow. Dispose enumerators promptly; collection timing is not guaranteed. The 4096-row checkpoint
      bound applies to active scanning, not time spent suspended. Scanning on one thread while another
      disposes is therefore safe. The snapshot finalizer path
      is unaffected: a borrow holds the snapshot, so a borrowed snapshot is never collected.

    - **Writer lifetime**: every internal `BlockWriter` operation holds a `BlockAccessScope` on its
      `BlockFile` for the operation's whole duration. `BlockFile.Dispose` refuses new scopes when
      it begins and waits for outstanding ones before unmapping, so a write racing disposal either
      completes against mapped memory or, if it arrives after disposal began, fails with
      `ObjectDisposedException` instead of dereferencing an unmapped view. Raw `BlockFile`
      property reads are not serialized against disposal; readers go through snapshot borrows.
