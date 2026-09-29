You are an implementer lane on the MFTLib "per-drive watch channels" plan (C# and C++ on
Windows). Your task is B5: split the index-wide gates per drive, publish snapshots under the state
lock, add the lost-catch-up retry, and fix disposal order. It is on the index trunk; B1 to B4
(per-drive watch state machine and the ported index tests) are merged on your base, and so is
W40 (ranged block flush).

YOUR WORKTREE: `C:\Users\mtsch\MFTLib-worktrees\265-B5` (branch `task/265-B5`).
Expected HEAD: `566718bb6f80795a6f58e50ee3441dc81220cdaf`. FIRST ACTION: `git -C C:\Users\mtsch\MFTLib-worktrees\265-B5 rev-parse HEAD`
must equal that SHA; on mismatch STOP and report; never merge, rebase or reset to self-correct.

WORKSPACE = `C:\Users\mtsch\MFTLib-worktrees\impl-265\.superpowers\sdd\2026-09-28-per-drive-watch-channels`
(read-only for you, except your report file). If any file named below is missing, STOP and reply
BLOCKED naming it.

Read, in this order:
1. `WORKSPACE\lane-common.md`: lane rules (worktree, lock, build, verification, report contract).
2. `WORKSPACE\global-constraints.md`: binding constraints, state machine, test rules. Read in full.
3. `WORKSPACE\index-port-common.md`: what B1 built (pointers into the code). Its "Porting rules"
   section does not apply to you: you write new tests test-first.
4. `WORKSPACE\task-B5-brief.md`: read this first among requirements. It is your requirements,
   with the exact names and values to use verbatim.
5. `WORKSPACE\orchestrator-rulings.md`: rows B4-Q1, N-4, C1-Q1, W4-3 and W40-R1 bind this task
   with the same force as the brief.

Rulings and context the brief cannot know:
- C1-Q1: `JournalCheckpointLossDetection.ScanCatchUp` ALREADY EXISTS (added by C1). Do not add it;
  drop `MFTLib/Index/JournalCheckpointLoss.cs` from your file list unless you need it for
  something else.
- N-4: `WatchFaultKind` final order `{ Subscriber, Drive, Apply, CatchUpLost, Channel, Recovery }`
  (CatchUpLost inserted before Channel, not appended). `WatchCatchUpState.Recovering` sits between
  `CaughtUp` and `Faulted`.
- W4-3: `BlockWriter.Complete(DateTime, Action<long>?)` already exists with every caller swept
  (task W40). Do not change its signature or sweep callers.
- B4-Q1: spec 2.6.4 (line 481): a rescan whose producer returns no block throws
  `InvalidOperationException` carrying `DriveStatus.MftProducerFailureMessage`. The code on your
  base returns normally. Implement the spec rule in your production loop with a failing-first
  test, and update the B4-ported test that pins the old behavior (find it in
  `MFTLib.Tests/Index/` by searching for the "scan fails without throwing" case).
- `MftBlockProduceResult.CatchUpLoss` exists (C2). The index never reads the journal for it.
- Coverage gap left by B4 (cover it if your restructure touches it, which it will): a
  `StartWatchingAsync` issued during a rescan of an UNRESUMABLE drive must judge the fresh block,
  not the old one. Add a test for it.
- Handler reentrancy (a blocking lifecycle call from inside a `Changed` or `WatchFaulted`
  handler) is B8's; do not write a test that blocks inside a handler on such a call. Your
  `CatchUpLost_HandlerRunsWithLifecycleGateHeld` test queues the call with `Task.Run`, as the
  brief says.
- W40-R1: every NEW test needs RED evidence in the report: the exact `dotnet test` command with
  its `--filter`, and the failing output, seen before the implementation (or, if you implemented
  first, against an uncommitted scratch mutation of the production behavior it pins).
- Open spec gaps the owner will decide later (do not decide them in code; if your code has to
  pick, pick the least surprising behavior and name it in your report): what a stop does after a
  failed restart; whether a fresh start discards a faulted instance's outstanding fault.
- aislop gate: no finding other than the four baseline warnings in lane-common.md plus the ruled
  parameter-count warning on the 8-parameter `JournalBrokerHost` constructor. Keep CRLF line
  endings in files that have them.
- Nothing you run may need elevation. Always pass `-NonInteractive` to `run-coverage.ps1`.
- Never end your turn to wait on a background command; poll inside the turn.
- Files over about 500 lines: split partial classes by responsibility as the codebase does.

REPORT FILE: `WORKSPACE\task-B5-report.md` (the one file outside your worktree you may write).
Write the full report there per lane-common.md, including a "Lock order" section that lists every
site that takes a lifecycle gate, a write gate or `_stateLock`, and in which order, and a
"Primary checkout" line with the output of `git -C C:\Users\mtsch\MFTLib status --short`.

Commit on `task/265-B5` with the brief's commit message, ending with
`Co-Authored-By: Claude <noreply@anthropic.com>`.

Reply with ONLY (under 15 lines): Status (DONE | DONE_WITH_CONCERNS | BLOCKED | NEEDS_CONTEXT),
commits (short SHA and subject), one-line test summary, concerns, report path.
