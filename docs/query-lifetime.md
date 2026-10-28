# Query and writer lifetime

- Index contracts:
    - **Query lifetime**: the six query operations (`Find`, `Search`, `Enumerate`, `EnumerateRows`, `Root`, and `FileEntry.Children`) accept an optional CancellationToken and hold a borrow on every block they read for the operation's duration, and on no other. Each `FileIndex` owns one root; `FileIndexSet` coordinates read-only queries (`Find`, `Search`, `Enumerate`, `EnumerateRows`) across a fixed ordered list of `FileIndex` instances, while `Root(CancellationToken = default)` lives on each `FileIndex`. On a single `FileIndex`, the block is chosen under the index state lock when the query is admitted: `Find` borrows the index's block when the root is a prefix of the path (none otherwise), `Root` borrows the index's block (throwing `InvalidOperationException` naming the root and its `RootState` when the index has no block), and `Search`, `Enumerate` and `EnumerateRows` borrow the index's block (validating any `Subtree` anchor against it). On a `FileIndexSet`, each member's borrow is taken one member at a time, never across two members at once: `Find` resolves the outermost available member whose root contains the path (a member is available when it has a block it could lend) and delegates to that member, with no fallback on a miss; a `Subtree`-anchored `Search`, `Enumerate` or `EnumerateRows` validates the anchor against the member blocks and borrows only the matching member's block; and an unscoped `Search`, `Enumerate` or `EnumerateRows` visits, in `Indices` order, only the outermost available members (those under no other available member), borrowing and releasing one member's block at a time, so no file is returned twice. A directory view binds its root row at admission and restricts the whole query to that row's subtree, intersected with any explicit anchor. A query held on one index therefore never delays the finalization, unmapping or aside-file deletion of another index's retired block. `Children` borrows only its own handle's block. They observe cancellation before the first row read. Operations that scan rows check at least every 4096 scanned rows, including rejected rows; Root resolves its one root row directly. RowScanner supplies the cadence for Find, Search, EnumerateRows and Children; Enumerate implements the same cadence in its streaming loop. Children observes only its caller's token because a handle holds no index reference; disposal waits for that listing rather than cancelling it.
      Row-backed `FileEntry` reads take no borrow and retain the per-access `IsReleased`
      check; `Root` and `RecordKey` are also release-checked. Equality, hashing,
      `IsValid` and `IsDisposed` use stored identity/state and remain available after
      release. `ToString()` also remains safe and renders a disposed marker after
      release. Row-column getters access the mapped row; `Path` (and `ToString()`
      when formatting a live path) follows the parent chain for at most 128 hops.
      Each block's `RootBlockRelease` counts borrows; `DisposeAsync` cancels a disposal token the five `FileIndex` queries
      and `WaitForCatchUpAsync` waits are linked to, waits for the borrows on the current and retired blocks
      to drop, and only then unmaps. A suspended `Enumerate` enumerator holds its borrow between yields, so
      disposal waits until it advances or is disposed, or, if abandoned, until garbage collection and finalization
      return the borrow. Dispose enumerators promptly; collection timing is not guaranteed. The 4096-row checkpoint
      bound applies to active scanning, not time spent suspended. Scanning on one thread while another
      disposes is therefore safe. The root block finalizer path
      is unaffected: a borrow holds its block, so a borrowed block is never collected.

    - **Handle lifetime**: a retained `FileEntry` holds its own `RootBlock` and no other index's, so it keeps
      only that block mapped. A rescan retires the index's previous block; once no handle or borrow reaches
      a retired block its finalizer unmaps it while the index lives. Index
      disposal releases the current and retired blocks regardless of held handles. Each runtime retains retired release states, not retired RootBlock objects. Disposal stops the watch, takes the lifecycle gate and then the write gate, drains retired releases, and atomically detaches the current block before releasing its mapping outside the state lock.

    - **Row enumeration**: `FileIndex.EnumerateRows` and `FileIndexSet.EnumerateRows` take a snapshot borrow when `foreach` begins
      (`GetEnumerator()`), and `foreach` releases it on completion, `break`, an exception or cancellation.
      `IndexRow.Root` and retained handles' `FileEntry.Root` are `RootKey` values;
      `FileEntry.Root` equals `RecordKey.Root` and uses the same per-access release check.
      `FileIndex.Root(CancellationToken = default)` returns the root `FileEntry` of that index.
      The token is observed before the first row and at least every 4096 scanned rows, including rows
      rejected by the query. The result (`IndexRowEnumerable`), its enumerator (`IndexRowEnumerator`) and
      each `IndexRow` are ref structs: holding one across an `await`, capturing it in a lambda or storing
      it in a class or ordinary struct field does not compile. A row and its `Name` span are valid only
      until any copy of the enumerator advances or is disposed; use `ToEntry()` to retain a `FileEntry` handle.
      A caller that obtains an enumerator directly through `GetEnumerator()` must dispose it, including
      when scanning throws or `MoveNext` returns false. Copies of that enumerator share one borrow
      and one name-resource owner. Advancing one copy invalidates older copies; their `Current`
      and `MoveNext` throw `InvalidOperationException`. Use only the advancing copy for further
      iteration. Dispose once through any copy, including a stale copy, to return the active
      bitset and borrow. `Current` and `MoveNext` on every copy then throw
      `ObjectDisposedException` naming `IndexRowEnumerator` before accessing the scanner or mapped memory;
      repeated `Dispose` is harmless. Already exported rows and spans must obey their lifetime themselves.
      Index disposal cancels an active borrowed scan at its cancellation checkpoint, but does not return
      that borrow for it. A reachable undisposed enumerator can keep `DisposeAsync` waiting indefinitely.
      The internal borrow object has a finalizer that can eventually return an unreachable borrow;
      garbage collection and finalization timing are not guaranteed. Dispose promptly.
      Each `GetEnumerator` takes its own snapshot, even from copies of the enumerable.

      A snapshot borrow guarantees stable ownership of that set of mapped blocks, not frozen contents.
      Journal updates write row fields in place while a watch runs. Names are appended to an immutable
      pool, with name identifier and flags published together as one atomic descriptor word;
      Each successful `MoveNext` captures a name from one descriptor read and its immutable entry. UTF-16 names
      borrow mapped text; ASCII names use the enumerator's reusable character buffer.
      That captured span keeps its text for its valid lifetime. Other `IndexRow` properties
      read the live row, and neither the whole row nor query filtering plus subsequent reads is atomic.
      A row can change between matching and consumption, and a scanner captures its row-count bound
      when it enters a root. Several passes can see different contents and different mappings.

      Name-filtered queries prepare matches while holding the same borrow: exact queries collect the
      identifiers of the matching entries from one hash chain; substring and glob queries scan the
      published name entries into a rented bitset before scanning rows. Identifiers appended after
      that entry pass do not match it. The entry pass checks cancellation before starting and every
      4096 name entries, and bitset ownership ends on query completion or enumerator disposal.

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
      number is a refresh of one incarnation. Row-backed access validates incarnation: each such
      read checks the handle against the row, and the writer publishes the new sequence before it overwrites the
      columns. A reader that pauses between its identity check and a column read can observe the replacement's
      columns, and no lock is taken on the read path. A caller that needs identity and columns together
      re-checks `IsDeleted` after reading, or reads the `FileChange` event fields. When a journal create reuses a deleted record's slot, every handle minted for the earlier file is
      superseded: `IsDeleted` is true, `Open` throws `FileNotFoundException` before any open-by-id call, `Children`
      returns an empty list, `Subtree` rejects it with `ArgumentException`, and every row column (`Name`, `Path`,
      `Parent`, `Size`, `IsSizeKnown`, `LastWriteTime`, `IsLastWriteTimeKnown`, `Attributes`, `IsDirectory`) throws
      `InvalidOperationException`, all through one shared check. `Root` and `RecordKey` stay readable for a superseded handle while its block remains unreleased. Equality
      compares the root block, row index and sequence number. A rename or move of the
      same file keeps its sequence number, so its handles keep tracking it. A `Deleted` `FileChange` whose slot was
      reused reports `Entry.IsDeleted`; `Path`, `PreviousPath` and `Timestamp` carry the event facts.

    - **Implicit subtree of a directory view**: a directory root on an NTFS volume reads the whole-volume block, so
      every row scan restricts rows to the view's subtree with a `SubtreeMembership` (an explicit `Subtree` anchor
      uses the same test). One membership per root scan is shared by its parallel partitions: two bit sets say
      whether a row's verdict is known and whether it is inside, a writer publishes `inside` with `Interlocked.Or`
      before `known`, and a reader reads `known` with `Volatile.Read` before `inside`, so each directory's ancestry is
      climbed once per query. Verdicts are deterministic, so racing writers agree. Admission rechecks the view's row
      and answers empty when the directory is out of the block.
    - **Subtree anchors**: `SearchQuery.Subtree` names a handle's row in its root block. `Search`, `Enumerate` and `EnumerateRows`
      reject `Subtree = default(FileEntry)` with `ArgumentException` on call entry, before any borrow. Each then
      resolves the anchor once, through one shared helper, against the snapshot the query borrowed, so a
      publication that lands after admission cannot turn validation into a check-then-use race. A released anchor
      throws `ObjectDisposedException`. An anchor whose own `RootBlock` is not one of the borrowed blocks
      (another index, even one with the same root key or ordinal, or a block a rescan replaced),
      or that a newer file has superseded by reusing its row slot, throws `ArgumentException`. For `Enumerate` and
      `EnumerateRows` the borrow is taken at the first `MoveNext` and at `GetEnumerator`, so those rejections
      surface there, and the borrow is returned when they do. Rescanning a different root publishes a new
      snapshot without invalidating the anchor. A
      retained handle stays readable against its original block, and a caller reacquires an anchor from the
      current index before each query that follows a rescan of the anchor's root.

    - **Paths**: `FileEntry.Path` is the root block's root directory joined with the entry's
      name chain using the host separator. For a live entry of a real source it is an openable
      filesystem path, and `FileIndex.Find` accepts it back, resolving a native path against the
      longest matching indexed root directory (on a `FileIndexSet`, the outermost available member whose root contains the path). A dump entry renders a virtual `dump:/X` path that
      `Open` refuses, a detached deleted row renders its bare name, and `Find` skips deleted
      entries, so a deleted entry's path does not resolve.

    - **Writer lifetime**: every internal `BlockWriter` operation holds a `BlockAccessScope` on its
      `BlockFile` for the operation's whole duration. `BlockFile.Dispose` refuses new scopes when
      it begins and waits for outstanding ones before unmapping, so a write racing disposal either
      completes against mapped memory or, if it arrives after disposal began, fails with
      `ObjectDisposedException` instead of dereferencing an unmapped view. Raw `BlockFile`
      property reads are not serialized against disposal; readers go through snapshot borrows.
      `JournalMutator.Apply` additionally holds one access scope for the whole batch, including
      incarnation checks, handle creation and event path materialization between writer calls.
      Disposal waits for that scope too; nested writer calls after disposal begins can still
      throw `ObjectDisposedException`, returning the batch scope as application unwinds.
