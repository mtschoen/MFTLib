# Query and writer lifetime

- Index contracts:
    - **Query lifetime**: the six entry points that scan rows (`Find`, `Search`, `Enumerate`, `EnumerateRows`,
      `Root`, and `FileEntry.Children`) each take an optional `CancellationToken`, observed
      before the first row and then at least every 4096 rows from inside `RowScanner`, and each holds a borrow
      on the `Snapshot` it reads for its whole duration. `FileEntry.Children` observes only its caller's token,
      since a handle holds no index reference; disposal waits that listing out rather than cancelling it. Every
      other `FileEntry` member reads one row and keeps the per-access `IsReleased` check instead.
      `SnapshotRelease` counts borrows; `DisposeAsync` cancels a disposal token the five `FileIndex` queries
      and `WaitForCatchUpAsync` waits are linked to, waits for the borrows on the current and retired snapshots
      to drop, and only then unmaps. A suspended `Enumerate` enumerator holds its borrow between yields, so
      disposal waits until it advances or is disposed, or, if abandoned, until garbage collection and finalization
      return the borrow. Dispose enumerators promptly; collection timing is not guaranteed. The 4096-row checkpoint
      bound applies to active scanning, not time spent suspended. Scanning on one thread while another
      disposes is therefore safe. The snapshot finalizer path
      is unaffected: a borrow holds the snapshot, so a borrowed snapshot is never collected.

    - **Row enumeration**: `FileIndex.EnumerateRows` takes its snapshot borrow when `foreach` begins
      (`GetEnumerator()`), and `foreach` releases it on completion, `break`, an exception or cancellation.
      The token is observed before the first row and at least every 4096 scanned rows, including rows
      rejected by the query. The result (`IndexRowEnumerable`), its enumerator (`IndexRowEnumerator`) and
      each `IndexRow` are ref structs: holding one across an `await`, capturing it in a lambda or storing
      it in a class or ordinary struct field does not compile. A row and its `Name` span are valid only
      until the enumerator advances or is disposed; use `ToEntry()` to retain a `FileEntry` handle.
      A caller that obtains an enumerator directly through `GetEnumerator()` must dispose it, including
      when scanning throws or `MoveNext` returns false. Copies of that enumerator share one borrow:
      dispose it once through any copy. `Current` and `MoveNext` on every copy then throw
      `ObjectDisposedException` naming `IndexRowEnumerator` before accessing the scanner or mapped memory;
      repeated `Dispose` is harmless. Already exported rows and spans must obey their lifetime themselves.
      Index disposal cancels an active borrowed scan at its cancellation checkpoint, but does not return
      that borrow for it. A reachable undisposed enumerator can keep `DisposeAsync` waiting indefinitely.
      The internal borrow object has a finalizer that can eventually return an unreachable borrow;
      garbage collection and finalization timing are not guaranteed. Dispose promptly.
      Each `GetEnumerator` takes its own snapshot, even from copies of the enumerable.

      A snapshot borrow guarantees stable ownership of that set of mapped blocks, not frozen contents.
      Journal updates write row fields in place while a watch runs. Names are appended to an immutable
      pool, with name offset, length and flags published together as one atomic descriptor word;
      `Current` captures a name span from one descriptor read, so a rename cannot tear its offset and
      length. That captured span keeps its text for its valid lifetime. Other `IndexRow` properties
      read the live row, and neither the whole row nor query filtering plus subsequent reads is atomic.
      A row can change between matching and consumption, and a scanner captures its row-count bound
      when it enters a drive. Several passes can see different contents and different mappings.

      `EnumerateRows` rejects null queries and undefined match modes at call time. Admission at
      `GetEnumerator` checks cancellation and index disposal; `MoveNext` checks cancellation during
      scanning and can reject an unresolved parent chain while applying a subtree restriction.

    - **Writer lifetime**: every internal `BlockWriter` operation holds a `BlockAccessScope` on its
      `BlockFile` for the operation's whole duration. `BlockFile.Dispose` refuses new scopes when
      it begins and waits for outstanding ones before unmapping, so a write racing disposal either
      completes against mapped memory or, if it arrives after disposal began, fails with
      `ObjectDisposedException` instead of dereferencing an unmapped view. Raw `BlockFile`
      property reads are not serialized against disposal; readers go through snapshot borrows.
