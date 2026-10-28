# Watch lifetime

Eligible views with one share key attach to one VolumeScan. The scan owns watch readiness, its pump, catch-up,
checkpoint facts and recovery; each IndexView owns its request, outstanding fault and delivery gates. Walks and dumps
own private scans. See [volume scan sharing](api-decisions.md#volume-scan-sharing) for the key and open/close rules.

Manual RescanAsync calls join the pending request until it takes the lifecycle gate. A call after that point queues
the next production: at most one runs and one is pending. Caller cancellation or view disposal ends only that wait;
the last departing waiter cancels and drains production, preserving canonical restoration and post-commit restart.
Automatic recovery is one ticket per scan and does not join manual requests.

- Index contracts:
    - **Root identity and lifecycle admission**: `VolumeKey` is an ASCII letter normalized once to upper
      case, with implicit conversion from `char` and no string conversion, and `RootKey` is a volume or a directory path that
      widens implicitly from it. The default of either is invalid.
      Each `FileIndex` coordinates one root (`FileIndexOptions.Root`). Start, stop, catch-up wait and rescan each expose one method taking
      `(CancellationToken cancellationToken)`, the token required, and returning
      `Task<RootOperationResult>`. Caller cancellation and disposal at
      admission fail the call; operational failures are returned in `RootOperationResult.Failure`. Disposal after
      admission cancels an admitted start or rescan as a `Failed` result carrying
      `OperationCanceledException`; it cancels a catch-up wait with `OperationCanceledException`.
    - **Fault recovery classification**: `WatchFault.IsRecovering` is true for `Volume` and
      `Apply`, and for `CatchUpLost` only when its exception is a
      `JournalCatchUpLostException` with `IsRecoveryStopped` false. It is false for `Subscriber`,
      `Channel`, `Recovery`, `RescanRestart`, and other catch-up exceptions. It describes recovery
      associated with that notification, not the root's subsequent live-watch health.
      A `Subscriber` fault leaves the watch running. `WatchFaulted` names the root;
      notifications run after status has been updated.
      During `Recovering`, queries use the current complete block, which may lag the volume.
      `Faulted` covers failed recovery, channel loss, refused start and exhausted catch-up;
      inspect `FailureMessage` and the fault exception before starting or rescanning again.
      Observe automatic recovery rather than starting a competing lifecycle operation.
      Pipe EOF, failure, stall or invalid frames report `WatchFaultKind.Channel` with an
      `IOException` and no automatic recovery; reconnect the process when needed, then
      rescan or reopen the affected state. A host-reported root error is `WatchFaultKind.Volume`.
    - **One snapshot shape**: `RootStatus.Watch` and `WatchStateChange.Watch` are the same
      `RootWatchStatus`, built by one helper under `StateLock`, so a consumer seeds its history
      from `Status.Watch` and applies events by the same `StateVersion` check. The version is
      exact, including zero. `WatchStateChange.Fault` exists only on events: a snapshot retains
      `Watch.FailureMessage`, never a fault.
    - **Catch-up recovery exhaustion**: `RootWatchStatus.IsRecoveryStopped` records exhausted
      automatic recovery from consecutive scan catch-up losses, independently of watch requests
      and refused-start faults. It is set under the stop-decision state lock before loss
      notification or open completion. Successful catch-up publication clears it with the internal
      count reset; failed or cancelled production, no-block scans, and stopping the watch preserve
      it. An unresumable refusal alone does not set it.
    - **Watch start readiness**: `StartWatchingAsync(token)` returns once the root's channel is connected and
      `StartWatch` is written, the handle is published and its pump is scheduled. It does
      not wait for the backlog; `WaitForCatchUpAsync` follows the root's caught-up marker.
    - **Watch source**: a root's watch starts through `FileIndexOptions.MftSource`'s watch
      source, the same object that produced the root's block. A null `MftSource` or a source
      with no watch capability makes start `NotApplicable`, with no failure exception and
      without recording a watch request. A root with no MFT-backed block has the same result.
      A watch-capable source's failed start or an unresumable cursor returns `Failed` with its exception.
    - **Watch and catch-up lifetime**: each MFT-backed VolumeScan has one
      internal `IIndexVolumeWatch`, pump, stop source, and catch-up slot, shared by its attached views. `StartWatchingAsync(token)` begins at
      `WatchState.CatchingUp`; journal batches through the tip captured at start are applied
      before `VolumeCaughtUp` moves the root to `CaughtUp`. `WaitForCatchUpAsync(token)` follows that instance:
      its result succeeds when the root catches up and is `Failed` with the watch fault or the cancellation
      of an instance retired by stop or rescan. Disposal cancels the wait. A pump fault settles the wait last: the fault,
      its `LiveWatch` checkpoint loss and its recovery ticket (`Recovering`) are recorded first,
      then the failed result settles, then `WatchFaulted` is raised, so a consumer reading `Status` from
      the failed result sees the same state a `WatchFaulted` handler sees. A stop in that
      window retains the watch's fault in the wait's result. A `Volume` or
      `Apply` fault publishes `Recovering`, raises `WatchFaulted`, and automatically rescans and
      restarts the root. A second fault before the restarted watch reaches `CaughtUp`, or a failed
      recovery scan, raises `WatchFaulted(Recovery)` and leaves the root `Faulted` until a consumer
      calls `RescanAsync(token)` or `StartWatchingAsync(token)`; a failed automatic restart likewise
      reads `Faulted`, never `NotStarted`. `Channel` faults never recover.
      A rescan keeps a healthy watch and its pending catch-up waits attached throughout
      production. Failed or cancelled production leaves them untouched; retirement cancels
      pending waits when the replacement commits. From that commit until the replacement
      registers, a root whose watch is requested reads `CatchingUp`, and a
      `WaitForCatchUpAsync(token)` issued then attaches to a restart-pending waiter on the root's
      runtime: the replacement instance adopts it as its own catch-up waiter, a refusal over an
      unresumable block or a lost catch-up faults it, and stop or disposal cancels it. Changes applied before that commit can be
      delivered after it and repeated by replacement catch-up. Queries can lag until the new
      watch reports `CaughtUp`. A successful manual scan whose replacement watch cannot start
      returns normally and raises `RescanRestart` once, with the start failure as its inner
      exception. Both its exception message and `RootWatchStatus.FailureMessage` identify the rescan's
      replacement and the failed watch start. The root stays `Faulted` without automatic
      recovery until a consumer starts or rescans it; stop returns that fault once as `Failed`.
      A failed automatic recovery, including its restart, reports `Recovery`.
    - **Watch request**: `RootWatchStatus.IsRequested` exposes `IndexView.WatchRequested`. A
      consumer start sets it before invoking the source, and it stays set when the source throws
      or when the start is refused because the block's journal cursor cannot be resumed; stop,
      disposal and a start its own caller cancelled clear only that view's request. The scan's watch is wanted exactly
      while its requester set is nonempty. A second start joins readiness without another source call. A stop with
      other requests remaining returns immediately, consuming only its own outstanding fault. A rescan restarts only
      while any view requests the watch, so the rescan a refusal asks for restarts the refused root, and a rescan
      never starts a root nobody asked to watch. A start answering `NotApplicable`
      records no request. A restart whose rescan replaced the block with one that cannot be
      watched withdraws the request and supersedes any retained faulted watch with its failure
      message, so the root reads `NotStarted` either way. `RootWatchStatus.State` derives, in order: `Recovering`; the current instance's
      state; `Faulted` for a failed or refused start; `CatchingUp` while requested with no
      instance; otherwise `NotStarted`.
    - **Watch state events**: `FileIndex.WatchStateChanged` delivers a `WatchStateChange`
      (`Volume`, `Watch`, `Fault`) for every change of the root's derived
      `State`, where `Watch` is the root's `RootWatchStatus` after the change and its
      `StateVersion` is the counter: zero
      at open unless open-time scans exhausted catch-up retries (which leaves the root `Faulted`
      with version 1), one more per change. Every section that changes an
      input of `GetWatchStateLocked` (start registration, a start whose source threw or its
      caller cancelled, a refused start, catch-up, pump fault settlement, recovery queued and
      ended, lost-catch-up retry and its end, rescan commit and recovery clear, a replacement's
      refusal or failed restart, stop, disposal) calls `NoteWatchStateLocked` before releasing
      `StateLock`; it rederives the state, and only when it differs from the last noted one bumps
      the shared version and queues a view-specific change on every attached IndexView. `RaiseWatchStateChanged` runs after
      every such section, and at the start of `RaiseWatchFaulted` and in the pump's fault
      settlement `finally`, with neither `StateLock` nor a write gate held. It holds the view's
      `DeliveryLock` (taken before `StateLock`, never under it) from taking the queue until the last
      handler returns, so changes are delivered one at a time in version order, and a
      caller that finds the queue empty waits for a delivery another thread is making. That is
      what keeps a fault's `WatchFaulted` behind the state change it caused (`Fault` set) when a
      concurrent stop took that change from the queue. A `Volume` or `Apply` fault moves the root to `Faulted` and then, once
      its recovery is queued, to `Recovering`; both carry the fault. A finished recovery is
      `CatchingUp` when the restarted watch registers and `CaughtUp` when it catches up, so no
      consumer polls for it. Delivery uses `Deliver`, so the reentrancy rules below apply, and a
      throwing handler is discarded without starving the others. `WatchFaulted` delivery takes the
      view's separate `FaultDeliveryLock` with no other lock held, so its fault callbacks
      never overlap even after a recovery releases the lifecycle gate; a blocked fault handler
      delays only the next fault. A handler must not block waiting
      for another change of its root. A start, rescan or recovery delivers while holding the
      lifecycle gate. A consumer that also reads `Status`, where state and version are
      read together, applies an event only when its version is newer than the last it applied,
      because its read can be newer than an event still being delivered. A
      readiness decision tagged with the version it read is therefore superseded by any later
      fault, which is how a consumer orders its own publication against the library's fault
      settlement without a callback under the lock. A disposal that stops the watch delivers the final change
      before DisposeAsync returns; another view's disposal leaves the watch running. Delivery admission closes and
      disposal drains that view's already-entered handlers; none start after its gate closes. Status already throws then.
    - **Runtime state machine**: each FileIndex has one IndexView attached to a VolumeScan; each actual watch start creates
      a distinct `WatchInstance` with its own generation, cancellation sources, handle, pump,
      catch-up slot, armed block, fault, and `Drained` task. `Current` holds at most one instance,
      and `Retiring` holds its predecessor until teardown finishes. A pump applies a batch only
      while it is still the current running instance armed against the published block; every
      completion, fault, checkpoint report, recovery ticket, and cleanup performs the same
      identity check, so a retiring pump cannot change its successor's block or state.
      Start linearizes when the returned handle is published and the instance becomes running;
      stop linearizes when `WatchRequested` is cleared and the instance becomes retiring; rescan
      and recovery linearize when the replacement block is committed; disposal linearizes when
      the view is marked disposed and its token is cancelled; last detach closes the scan. A rescan or recovery holds the
      lifecycle gate through production, commit-time retirement, draining, and an eligible restart.
      Cancellation/disposal checks precede retirement; a healthy current
      watch retires in the same state-lock section as block and status publication.
      An already faulted current watch stays until restart registration supersedes it. The first
      publication retires the old watch even when catch-up was lost and the scan must retry.
      Recovery is
      ticketed to the faulted instance and its block and is dropped if either is not current.
      A fault during production can recover after a failed scan; after a successful commit its
      block is stale, and even a delayed queue cannot publish `Recovering` over the replacement.
      A manual commit clears the superseded ticket and recovery state under the publication
      lock, before draining; an automatic recovery's own commit keeps its ticket and state.
      Stop during a recovery or rescan wins: it clears the request, prevents the restart, and
      reports that view's outstanding fault once as Failed. Fault recipients are captured under the state lock at
      settlement, then delivered outside it. Each view retains the first outstanding fault of a watch generation;
      a later generation's fault or RescanRestart replaces it. Subscriber faults stay on the throwing view, once per
      watch generation per view. A rescan retains its retired watch's subscriber fault through draining and replacement
      startup until stop consumes it, a superseding consumer start clears it, or RescanRestart replaces it.
      A fresh consumer start supersedes a
      recovery and discards the faulted instance's outstanding fault. If a restart itself failed
      before publishing a handle in a consumer start or automatic recovery, stop clears the
      request and refused-start fault with a successful result. A manual rescan's failed restart
      retains a faulted instance with a RescanRestart copy on each requesting view, returned once by its stop as Failed.
      Last-detach disposal cancels and drains all instances and recovery work without
      throwing a watch fault.
    - **Open and scan progress**: `OpenAsync` settles the configured root before returning the index or throwing.
      Open-time catch-up retries and the exhausted root's `Ready` unresumable block follow
      [checkpoint loss](checkpoint-loss.md).
      `FileIndexOptions.ScanProgress` samples a scan while its producer runs, then `VolumeScan`
      emits one `IndexScanPhase.Finished` sample after the producer's last sample for each
      scan, for MFT and enumeration producers, in opens and rescans.
      Samples fan out with each receiving view's Root; diagnostics deduplicate equal sinks. The creating view supplies
      the scan's clock, directory-walk options and scan hooks. Query and disposal hooks remain per view.
      `IndexScanProgress.Outcome` is non-null only on that terminal sample: `Succeeded`,
      `Failed` or `Cancelled`. A rescan or lost-catch-up retry emits its own `Finished`.
      A throwing `Finished` handler is contained
      and logged to `FileIndexOptions.Diagnostics`, never replacing the scan's result or exception.
    - **Change routing**: a batch applies once under the scan's write gate, refreshes every view binding, and captures
      the attached recipients before releasing the gate. Current or previous path containment uses
      LookupEngine.TryMatchRootPrefix, including equal paths; a block-root view receives every change. Each Entry is
      rebound to the recipient's RootBlock with its captured sequence. A move across view boundaries is Renamed on
      both views. Changed delivery does not require a watch request; scan fault delivery does.
    - **Lock order**: the order is the lifecycle gate, the write gate, then
      `StateLock`. No gate is acquired while `StateLock` is held; disposal takes the lifecycle gate and then the
      write gate. Production holds the lifecycle gate but neither
      the write gate nor `StateLock`; publication takes the write gate and commits the block,
      status, and pending result under `StateLock`, retiring a healthy watch in that same
      section. After releasing both `StateLock` and the write gate, it requests stop and awaits
      the old pump's `Drained` and any predecessor's, holding only the lifecycle gate. `Changed`,
      `WatchFaulted` and `WatchStateChanged` run with no
      write gate and no `StateLock`; pumps hold no gate when raising any of them. The scan path
      may raise `WatchFaulted(CatchUpLost)` or `WatchFaulted(RescanRestart)` while
      holding the lifecycle gate, after draining the old pump, and starts, rescans and recoveries
      raise `WatchStateChanged` while holding the lifecycle gate.
    - **Callback reentrancy**: a `Changed`, `WatchFaulted` or `WatchStateChanged` handler runs on the root's pump (or, for `WatchFaultKind.CatchUpLost` and `WatchFaultKind.RescanRestart`, on the scan operation that holds the lifecycle gate, and for a state change on whichever start, stop, rescan or recovery made it), so a lifecycle call that waits for the pump or lifecycle gate can deadlock. `FileIndex.Reentrancy.cs` sets one process-wide `AsyncLocal` delivery marker around every handler invocation of every index (`Deliver`, used by `RaiseChanged`, `RaiseWatchFaulted` and `RaiseWatchStateChanged`); `StartWatchingAsync`, `StopWatchingAsync`, `RescanAsync`, `DisposeAsync` and an unsettled `WaitForCatchUpAsync` check it synchronously at entry and return an already faulted task carrying `InvalidOperationException`, whichever index the call names. Scans may be independent and views may share one scan, so the check cannot be per index: a handler of index T that stopped index U while a handler of U stopped T would block both pumps. A handler of one index that triggers a handler of another stays inside a handler for the whole chain. The marker flows into awaits and work the handler starts; its `Active` flag is cleared when the invocation returns, so work queued from a handler that runs afterwards is allowed. Queries, `Status`, `QueryUsnJournalSettings` and a settled catch-up wait are never rejected. The recovery's restart and a rescan's restart use internal helpers that carry no check. Every waiter a handler can settle or cancel completes its continuations asynchronously (`RunContinuationsAsynchronously`, `AwaitQueuedAsync` for token cancellation), so a continuation never runs on the handler's stack.
      `BrokerSession.GrowUsnJournalAsync` is outside the index and is allowed from its callbacks.
