# Watch lifetime

- Index contracts:
    - **Watch start readiness**: `StartWatchingAsync(X)` returns once X's channel is connected and
      `StartWatch` is written.
    - **Watch and catch-up lifetime**: each MFT-backed drive has an independent
      `IIndexDriveWatch`, pump, stop source, and catch-up slot. `StartWatchingAsync(X)` begins at
      `WatchCatchUpState.CatchingUp`; journal batches through the tip captured at start are applied
      before `DriveCaughtUp` moves X to `CaughtUp`. `WaitForCatchUpAsync(X)` follows that instance:
      it completes when X catches up, faults with X's watch fault, and is cancelled when stop,
      rescan, or disposal retires the instance. The list and all-drive overloads fan out to the
      requested drives concurrently and return one `DriveOperationResult` per drive. A `Drive` or
      `Apply` fault publishes `Recovering`, raises `WatchFaulted`, and automatically rescans and
      restarts only X. A second fault before the restarted watch reaches `CaughtUp`, or a failed
      recovery scan, raises `WatchFaulted(Recovery, X)` and leaves X `Faulted` until a consumer
      calls `RescanAsync(X)` or `StartWatchingAsync(X)`. `Channel` faults never recover.
      A rescan keeps a healthy watch and its pending catch-up waits attached throughout
      production. Failed or cancelled production leaves them untouched; retirement cancels
      pending waits when the replacement commits. Changes applied before that commit can be
      delivered after it and repeated by replacement catch-up. Queries can lag until the new
      watch reports `CaughtUp`. A successful manual scan whose replacement watch cannot start
      returns normally and raises `RescanRestart` once, with the start failure as its inner
      exception. Both its exception message and `WatchFailureMessage` identify the rescan's
      replacement and the failed watch start. The drive stays `Faulted` without automatic
      recovery until a consumer starts or rescans it; stop rethrows that fault once.
      A failed automatic recovery, including its restart, reports `Recovery`.
    - **Per-drive state machine**: each configured drive has a `DriveRuntime`; each start creates
      a distinct `WatchInstance` with its own generation, cancellation sources, handle, pump,
      catch-up slot, armed block, fault, and `Drained` task. `Current` holds at most one instance,
      and `Retiring` holds its predecessor until teardown finishes. A pump applies a batch only
      while it is still the current running instance armed against the published block; every
      completion, fault, checkpoint report, recovery ticket, and cleanup performs the same
      identity check, so a retiring pump cannot change its successor's block or state.
      Start linearizes when the returned handle is published and the instance becomes running;
      stop linearizes when `WatchRequested` is cleared and the instance becomes retiring; rescan
      and recovery linearize when the replacement block is committed; disposal linearizes when
      `_disposed` is set and its token is cancelled. A rescan or recovery holds the drive's
      lifecycle gate through production, commit-time retirement, draining, and an eligible restart.
      Snapshot creation and cancellation/disposal checks precede retirement; a healthy current
      watch retires in the same state-lock section as block, status, and snapshot publication.
      An already faulted current watch stays until restart registration supersedes it. The first
      publication retires the old watch even when catch-up was lost and the scan must retry.
      Recovery is
      ticketed to the faulted instance and its block and is dropped if either is not current.
      A fault during production can recover after a failed scan; after a successful commit its
      block is stale, and even a delayed queue cannot publish `Recovering` over the replacement.
      A manual commit clears the superseded ticket and recovery state under the publication
      lock, before draining; an automatic recovery's own commit keeps its ticket and state.
      Stop during a recovery or rescan wins: it clears the request, prevents the restart, and
      rethrows the stopped instance's outstanding fault once. A rescan retains a retired watch's
      subscriber fault through draining and replacement startup, until stop consumes it, the
      replacement handle is published, or `RescanRestart` supersedes it with the start failure.
      A fresh consumer start supersedes a
      recovery and discards the faulted instance's outstanding fault. If a restart itself failed
      before publishing a handle in a consumer start or automatic recovery, stop clears the
      request and refused-start fault without rethrowing it. A manual rescan's failed restart
      retains a faulted instance whose `RescanRestart` fault stop rethrows once. `DisposeAsync` cancels and drains all instances and recovery work without
      throwing a watch fault.
    - **Concurrent open**: `OpenAsync` settles every configured drive concurrently and waits for
      them all before publishing the first snapshot or throwing. `FileIndexOptions.OpenProgress`
      reports once for each drive that settles, from that drive's settling thread with no lock
      held. Callbacks may overlap and arrive out of `IndexDriveOpened.SettledCount` order, so a
      consumer keeps the report with the largest count. A cancelled settle claims no count and
      reports nothing; a cancelled or failed open may therefore have reported only some drives.
    - **Lock order**: for drive X the order is X's lifecycle gate, X's write gate, then
      `_stateLock`. No gate is acquired while `_stateLock` is held, and no operation except
      disposal holds gates for two drives; disposal takes every lifecycle gate and then every
      write gate in ascending drive-letter order. Production holds the lifecycle gate but neither
      the write gate nor `_stateLock`; publication takes the write gate and commits the block,
      snapshot, and pending result under `_stateLock`, retiring a healthy watch in that same
      section. After releasing both `_stateLock` and the write gate, it requests stop and awaits
      the old pump's `Drained` and any predecessor's, holding only the lifecycle gate. `Changed`
      and `WatchFaulted` run with no
      write gate and no `_stateLock`; pumps hold no gate when raising either event. The scan path
      may raise `WatchFaulted(CatchUpLost, X)` or `WatchFaulted(RescanRestart, X)` while
      holding X's lifecycle gate, after draining the old pump.
    - **Callback reentrancy**: a `Changed` or `WatchFaulted` handler runs on a drive's pump (or, for `WatchFaultKind.CatchUpLost` and `WatchFaultKind.RescanRestart`, on the scan operation that holds the drive's lifecycle gate), so a lifecycle call that waits for a pump can deadlock across drives (X's handler stops Y while Y's handler stops X). `FileIndex.Reentrancy.cs` sets an `AsyncLocal` delivery marker around every handler invocation (`Deliver`, used by `RaiseChanged` and `RaiseWatchFaulted`); `StartWatchingAsync`, `StopWatchingAsync`, `RescanAsync`, `DisposeAsync`, their batched forms and an unsettled `WaitForCatchUpAsync` check it synchronously at entry and return an already faulted task carrying `InvalidOperationException`, whichever drive they name. The marker flows into awaits and work the handler starts; its `Active` flag is cleared when the invocation returns, so work queued from a handler that runs afterwards is allowed. Queries, `Drives`, `QueryUsnJournalSettings` and a settled catch-up wait are never rejected. The recovery's restart and a rescan's restart use internal helpers that carry no check. Every waiter a handler can settle or cancel completes its continuations asynchronously (`RunContinuationsAsynchronously`, `AwaitQueuedAsync` for token cancellation), so a continuation never runs on the handler's stack.
