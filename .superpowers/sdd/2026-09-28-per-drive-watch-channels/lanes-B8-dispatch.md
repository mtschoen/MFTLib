You are an implementer lane on the MFTLib "per-drive watch channels" plan (C# on Windows). Your
task is B8: the callback reentrancy guard. Every other index and broker task is merged on your
base, including the batched entry points (B7).

YOUR WORKTREE: `C:\Users\mtsch\MFTLib-worktrees\265-B8` (branch `task/265-B8`).
Expected HEAD: `ca649bba0a69bf9dc83aeaa60ebb522485063fe6`. FIRST ACTION: `git -C C:\Users\mtsch\MFTLib-worktrees\265-B8 rev-parse HEAD`
must equal that SHA; on mismatch STOP and report; never merge, rebase or reset to self-correct.

WORKSPACE = `C:\Users\mtsch\MFTLib-worktrees\impl-265\.superpowers\sdd\2026-09-28-per-drive-watch-channels`
(read-only for you, except your report file). If any file named below is missing, STOP and reply
BLOCKED naming it.

Read, in this order:
1. `WORKSPACE\lane-common.md`: lane rules (worktree, lock, build, verification, report contract).
2. `WORKSPACE\global-constraints.md`: binding constraints, state machine, test rules. In full.
3. `WORKSPACE\task-B8-brief.md`: read this first among requirements, with the exact names, the
   exception message and the test names to use verbatim.
4. Spec 2.6.8 in `docs\superpowers\specs\2026-09-28-per-drive-watch-channels-design.md` (worktree).
5. `WORKSPACE\orchestrator-rulings.md`: rows B9-Q1, B9-Q2, W40-R1.
6. The "Lock order" sections of `WORKSPACE\task-B5-report.md` and `WORKSPACE\task-B6-report.md`.

Context the brief cannot know:
- Raise sites on your base: the pump's `Changed` and `WatchFaulted` delivery, B5's
  `WatchFaulted(CatchUpLost)` raised by the scan operation while it holds X's lifecycle gate, and
  B6's recovery faults (`WatchFaulted(Drive or Apply)` before queueing, `WatchFaulted(Recovery)`
  after the recovery releases its gate). Every raise site gets the delivery marker; find them all
  (Grep for the event invocations) and list them in the report.
- Entry points to guard: the single-drive and batched (list and no-list) forms of
  `StartWatchingAsync`, `StopWatchingAsync`, `RescanAsync`, `WaitForCatchUpAsync` (unsettled only),
  and `DisposeAsync`. The recovery's own internal restart is not a consumer call and must not be
  rejected (it runs on the thread pool, outside any handler); say in the report how you ensured
  the marker does not flow into a queued recovery started from inside a raise (for example
  `ExecutionContext.SuppressFlow` or clearing the marker when queueing), and add a test that a
  recovery queued while a handler ran still restarts the watch.
- Ledger note from B1 that this task must cover: a handler that calls `StopWatchingAsync` on its
  OWN drive deadlocks on the base (it awaits its own pump's `Drained`). Include a test for the
  own-drive case as well as the cross-drive ones the brief names.
- `FileIndexOptions.OpenProgress` is not a `Changed`/`WatchFaulted` handler; ruling B9-Q1 lets it
  run without a lock on the settling thread. It is out of B8's scope unless the spec says
  otherwise; say what you decided.
- The TaskCompletionSource audit (brief): list every `new TaskCompletionSource` in `MFTLib/Index`
  with its options, and every public wait's cancellation path, in the report.
- W40-R1: every NEW test needs, in the report, the literal command (full `dotnet test` line with its
  literal `--filter`) and the real failing output, before implementation or against an uncommitted
  scratch mutation. A command template with a placeholder does not count.
- Test rules: no clock, no real-time delay or polling sleep, no elapsed-time assertion; every await
  in a test and its helpers bounded (a regression shows as a timeout, never a hung suite); owned
  cache directory; `TestGate` ordering; the `[ThreadStatic]` flag technique the brief names.
- Run the whole suite once AFTER your last edit, then aislop; report both.
- aislop gate: no finding other than the four baseline warnings in lane-common.md plus the ruled
  8-parameter `JournalBrokerHost` constructor warning. Keep CRLF line endings.
- Nothing you run may need elevation. Always pass `-NonInteractive` to `run-coverage.ps1`. Never
  end your turn to wait on a background command; poll inside the turn.

REPORT FILE: `WORKSPACE\task-B8-report.md` (the one file outside your worktree you may write). Write
the full report per lane-common.md, including the raise-site list, the guarded entry-point list, the
TaskCompletionSource audit, and a "Primary checkout" line with the output of
`git -C C:\Users\mtsch\MFTLib status --short`.

Commit on `task/265-B8` with the brief's commit message, ending with
`Co-Authored-By: Claude <noreply@anthropic.com>`.

Reply with ONLY (under 15 lines): Status (DONE | DONE_WITH_CONCERNS | BLOCKED | NEEDS_CONTEXT),
commits (short SHA and subject), one-line test summary, concerns, report path.
