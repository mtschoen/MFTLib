# Query and writer lifetime

- Index contracts:
    - **Query lifetime**: the six entry points that scan rows (`Find`, `Search`, `Enumerate`, `EnumerateRows`,
      `Root`, and `FileEntry.Children`) each take an optional `CancellationToken`, observed
      before the first row and then at least every 4096 rows from inside `RowScanner`, and each holds a borrow
      on every drive block it reads (the snapshot) for its whole duration. `FileEntry.Children` observes only its caller's token,
      since a handle holds no index reference; disposal waits that listing out rather than cancelling it. Every
      other `FileEntry` member takes no borrow and keeps the per-access `IsReleased` check instead;
      it reads one row, except `Path` (and `ToString`, which can build it), which follows the parent chain for at most 128 hops.
      Each block's `DriveBlockRelease` counts borrows; `DisposeAsync` cancels a disposal token the five `FileIndex` queries
      and `WaitForCatchUpAsync` waits are linked to, waits for the borrows on the current and retired blocks
      to drop, and only then unmaps. A suspended `Enumerate` enumerator holds its borrow between yields, so
      disposal waits until it advances or is disposed, or, if abandoned, until garbage collection and finalization
      return the borrow. Dispose enumerators promptly; collection timing is not guaranteed. The 4096-row checkpoint
      bound applies to active scanning, not time spent suspended. Scanning on one thread while another
      disposes is therefore safe. The drive block finalizer path
      is unaffected: a borrow holds its blocks, so a borrowed block is never collected.

    - **Handle lifetime**: a retained `FileEntry` holds its own `DriveBlock` and no other drive's, so it keeps
      only that block mapped. A rescan retires only the rescanned drive's block; once no handle or borrow reaches
      a retired block its finalizer unmaps it while the index lives, whatever handles other drives hold. Index
      disposal releases every current and retired block regardless of held handles.

    - **Row enumeration**: `FileIndex.EnumerateRows` takes its snapshot borrow when `foreach` begins
      (`GetEnumerator()`), and `foreach` releases it on completion, `break`, an exception or cancellation.
      `IndexRow.Drive` and retained handles' `FileEntry.Drive` are normalized `DriveKey` values;
      `FileEntry.Drive` equals `RecordKey.Drive` and uses the same per-access release check.
      `Root` takes a `DriveKey` and rejects its invalid default value.
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

    - **Handle incarnation**: a `FileEntry` captures the sequence number its row held when it was minted
      (`ushort`, in the struct's tail padding, so it stays 16 bytes; an enumeration block, which the journal never
      mutates, captures zero). `BlockWriter.TryWriteRow` stores the supplied NTFS sequence number exactly, publishing it
      before it overwrites the columns. A `FileEntry` is bound to the NTFS file reference (record number plus
      sequence number) it was created from. Distinct file references never alias; a reference NTFS itself reuses
      (a freed record's incremented sequence number handed to the next file, which a scan with freed records
      imports as a tombstone that the next create then matches) is the same reference, and the library does not
      pretend otherwise. The journal ignores a delete or other mutation whose record names a different sequence
      number than the live row holds, because it belongs to an earlier incarnation a scan already superseded; a
      stored sequence of zero means the producer recorded none. A create that rewrites a live row with the same
      number is a refresh of one incarnation. Identity is validated per access: each
      read checks the handle against the row, and the writer publishes the new sequence before it overwrites the
      columns. A reader that pauses between its identity check and a column read can observe the replacement's
      columns, and no lock is taken on the read path. A caller that needs identity and columns together
      re-checks `IsDeleted` after reading, or reads the `FileChange` event fields. When a journal create reuses a deleted record's slot, every handle minted for the earlier file is
      superseded: `IsDeleted` is true, `Open` throws `FileNotFoundException` before any open-by-id call, `Children`
      returns an empty list, `Subtree` rejects it with `ArgumentException`, and every row column (`Name`, `Path`,
      `Parent`, `Size`, `IsSizeKnown`, `LastWriteTime`, `IsLastWriteTimeKnown`, `Attributes`, `IsDirectory`) throws
      `InvalidOperationException`, all through one shared check. `Drive` and `RecordKey` stay readable. Equality
      compares the drive block, row index and sequence number. A rename or move of the
      same file keeps its sequence number, so its handles keep tracking it. A `Deleted` `FileChange` whose slot was
      reused reports `Entry.IsDeleted`; `Path`, `PreviousPath` and `Timestamp` carry the event facts.

    - **Subtree anchors**: `SearchQuery.Subtree` names a handle's row in its drive block. `Search`, `Enumerate` and `EnumerateRows`
      reject `Subtree = default(FileEntry)` with `ArgumentException` on call entry, before any borrow. Each then
      resolves the anchor once, through one shared helper, against the snapshot the query borrowed, so a
      publication that lands after admission cannot turn validation into a check-then-use race. A released anchor
      throws `ObjectDisposedException`. An anchor whose own `DriveBlock` is not one of the borrowed blocks
      (another index, even one with the same drive key or ordinal, or a block a rescan replaced),
      or that a newer file has superseded by reusing its row slot, throws `ArgumentException`. For `Enumerate` and
      `EnumerateRows` the borrow is taken at the first `MoveNext` and at `GetEnumerator`, so those rejections
      surface there, and the borrow is returned when they do. Rescanning a different drive publishes a new
      snapshot without invalidating the anchor. A
      retained handle stays readable against its original block, and a caller reacquires an anchor from the
      current index before each query that follows a rescan of the anchor's drive.

    - **Paths**: `FileEntry.Path` is the drive block's root directory joined with the entry's
      name chain using the host separator. For a live entry of a real source it is an openable
      filesystem path, and `FileIndex.Find` accepts it back, resolving a native path against the
      longest matching indexed root directory. A dump entry renders a virtual `dump:/X` path that
      `Open` refuses, a detached deleted row renders its bare name, and `Find` skips deleted
      entries, so a deleted entry's path does not resolve.

    - **Writer lifetime**: every internal `BlockWriter` operation holds a `BlockAccessScope` on its
      `BlockFile` for the operation's whole duration. `BlockFile.Dispose` refuses new scopes when
      it begins and waits for outstanding ones before unmapping, so a write racing disposal either
      completes against mapped memory or, if it arrives after disposal began, fails with
      `ObjectDisposedException` instead of dereferencing an unmapped view. Raw `BlockFile`
      property reads are not serialized against disposal; readers go through snapshot borrows.
