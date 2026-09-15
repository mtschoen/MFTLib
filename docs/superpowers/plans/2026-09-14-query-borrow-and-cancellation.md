# Query cancellation and dispose-waits-for-borrows

Owner ruling 2026-09-14 (attended session, after the file-wizard live test crashed):
"yes to both. Sure the consumer has to be careful but this sounds like a footgun the
library shouldn't let you fire." This amends ruling #145's letter, not its spirit:
`DisposeAsync` still always unmaps and never waits on the garbage collector, but it now
cancels in-flight queries and waits for them to exit before it unmaps.

## The crash

FileWizardMaui died with `AccessViolationException` inside `DuplicateNameFinder.Find` on a
thread-pool worker while the UI thread was in `FileIndex.DisposeAsync` ->
`Snapshot.ReleaseNow` -> `SnapshotRelease.ReleaseAndWait` -> `DriveBlock.Release` ->
`BlockFile.Dispose` -> `UnmapViewOfFile`. `CurrentSnapshot` hands out the live snapshot with
no reference taken; `RowScanner` captures `Span<FileRow>` and `Span<char>` once and never
rechecks; `DisposeAsync` force-releases the current and retired snapshots with no reader
gate. Consumer-side drains (file-wizard `fix/duplicate-refresh-drain`) close the hole for
one consumer; this plan closes it in the library for every consumer.

## Contract after this change

1. Every public query on `FileIndex` takes an optional `CancellationToken`
   (`DuplicateNames`, `Largest`, `Find`/lookup, search, and any other entry point that
   scans rows or resolves entries through a `Snapshot`). A query observes its token at
   least once per drive block and at least every 4096 rows; cancellation surfaces as
   `OperationCanceledException`.
2. A query holds a borrow on the snapshot it reads for its whole duration. A borrow is a
   counter on `SnapshotRelease` (so it protects retired snapshots too, not only the
   current one), taken under the existing state lock, refused with
   `ObjectDisposedException` once the index is disposed or the snapshot released.
3. `FileIndex.DisposeAsync` sets the disposed flag, cancels a disposal token that every
   query's effective token is linked to, waits until every borrow on the current and
   retired snapshots has dropped, then unmaps as it does today. Snapshot release paths
   (`ReleaseNow`, `ReleaseAsync`, `ReleaseAllRetiredSnapshotsAsync`) wait for borrows the
   same way. The finalizer path is unchanged: an unreachable snapshot cannot be borrowed.
4. The `DisposeAsync` remarks that call a reader racing disposal "a consumer bug, not a
   library guarantee" are replaced by the new contract. `docs/index-format.md` and any
   other doc that states the old contract change with it.
5. `FileEntry` keeps its per-access `IsReleased` check; it is a different, cheaper guard
   for handles held after a query returns and is not part of this change.

## Design notes

- Put the cancellation check inside `RowScanner.MoveNext` with a row counter, so every
  scanning engine gets it from one place; the scanner takes the token in its constructor.
  Engines that iterate without `RowScanner` check the token themselves per block.
- The effective token is `CancellationTokenSource.CreateLinkedTokenSource(callerToken,
  disposalToken)` created per query; dispose it when the query returns. Avoid allocating
  when both tokens are `None`? No: correctness first, keep it simple; measure with the
  Benchmark project only if a profile shows it.
- Borrow shape: a `readonly struct` or small class returned by `Snapshot.Borrow()` that
  decrements on `Dispose`; `SnapshotRelease` owns the counter and a
  `TaskCompletionSource` that completes when the count reaches zero while a release is
  pending. Release waits on that task; no spin, no timeout.
- Keep the public API source-compatible: tokens are optional trailing parameters.
- Keep files under ~500 lines; `FileIndex.cs` is already partial across files, follow
  that split for new code.

## Tests (TDD, each red before green)

- `Snapshot.Borrow` refuses after release with `ObjectDisposedException`.
- `DisposeAsync` does not complete while a borrow is held on a worker, completes once the
  borrow is released, and the block is unmapped afterwards (extend
  `FileIndexBlockReleaseTests`).
- Retired snapshot release waits for a borrow the same way.
- `DisposeAsync` cancels an in-flight `DuplicateNames` on a worker: the query ends with
  `OperationCanceledException` or `ObjectDisposedException`, `DisposeAsync` completes, and
  the process is intact. Use a synthetic index large enough that the query is running when
  dispose starts; assert only on the outcome set, never on elapsed time.
- Caller cancellation: a pre-cancelled token makes each query throw
  `OperationCanceledException` before touching rows; a token cancelled mid-scan stops the
  scan (a `RowScanner` unit test with a token cancelled from a callback after N rows).
- Every new public parameter is exercised at least once per entry point.

## Gates

`scripts/run-coverage.ps1 -NonInteractive` (Windows) or `scripts/coverage-linux.sh`;
`aislop scan .` with `failBelow: 100`; em-dash and prose rules from AGENTS.md; TEST-REPORT.md
rewritten for this branch with real numbers. Consumers: file-wizard passes its drain
token into `DuplicateNames` after the pin move; git-wizard is unaffected at source level.
