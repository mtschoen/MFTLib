You are an implementer lane on the MFTLib "per-drive watch channels" plan (C# and C++ on
Windows). Your task is B6: automatic recovery of a drive whose watch faults. It is on the index
trunk; B1 to B5 (per-drive watch state machine, ported index tests, per-drive gates, publication
under the state lock, the lost-catch-up loop) are merged on your base.

YOUR WORKTREE: `C:\Users\mtsch\MFTLib-worktrees\265-B6` (branch `task/265-B6`).
Expected HEAD: `__HEAD__`. FIRST ACTION: `git -C C:\Users\mtsch\MFTLib-worktrees\265-B6 rev-parse HEAD`
must equal that SHA; on mismatch STOP and report; never merge, rebase or reset to self-correct.

WORKSPACE = `C:\Users\mtsch\MFTLib-worktrees\impl-265\.superpowers\sdd\2026-09-28-per-drive-watch-channels`
(read-only for you, except your report file). If any file named below is missing, STOP and reply
BLOCKED naming it.

Read, in this order:
1. `WORKSPACE\lane-common.md`: lane rules (worktree, lock, build, verification, report contract).
2. `WORKSPACE\global-constraints.md`: binding constraints, the per-drive state machine (its
   Recovery row is yours), test rules. In full.
3. `WORKSPACE\index-port-common.md`: pointers into the index watch code. (Its statement "There is
   NO automatic recovery yet" is what you change; its porting rules do not apply to your new tests.)
4. `WORKSPACE\task-B6-brief.md`: read this first among requirements, with the exact names to use
   verbatim.
5. `WORKSPACE\task-B5-report.md`: what B5 built (lock order, the lost-catch-up loop in
   `FileIndex.CatchUp.cs`, `PendingDriveResult`, decisions it left to you). Read its "Lock order"
   and "Decisions" sections.
6. `WORKSPACE\orchestrator-rulings.md`: rows B5-Q1, N-4, W40-R1 bind this task.

Context the brief cannot know:
- B5 left to you (its report says so): the loop does not yet re-check `WatchRequested` between
  attempts, and `WaitForCatchUpAsync(X)` while X reads `Recovering` throws "not being watched";
  spec 2.6.4 says such a wait faults at once with X's fault. Both are recovery behavior: implement
  them here, each with a failing-first test.
- Lock order (B5, binding): X's lifecycle gate, then X's write gate, then `_stateLock` (a `Lock`,
  never held across an await). No gate is acquired while holding `_stateLock`. `Changed` and
  `WatchFaulted` are raised with no write gate and no `_stateLock` held; the lost-catch-up operation
  is the one raiser that holds X's lifecycle gate.
- In parallel with you, task C6 builds the broker watch channels and ports broker watch tests; it
  asserts post-fault state only for `Channel` faults. Your changes to
  `FileIndexWatchRecoveryFaultTests.cs` and `FileIndexWatchFaultTests.cs` name each changed
  expectation in the commit message, per the brief.
- Handler reentrancy (blocking lifecycle calls inside a handler) is task B8's; do not write a test
  that blocks inside a handler on such a call; queue it with `Task.Run` if a test needs one.
- Owner spec gaps still open (do not decide them differently from the current code; if your code
  must pick, pick the least surprising behavior and name it in the report): what a stop does after
  a failed restart; whether a fresh start discards a faulted instance's outstanding fault.
- W40-R1: every NEW test needs RED evidence in the report: the exact command with its literal
  `--filter` and the failing output, before implementation or against an uncommitted scratch
  mutation. If the permission classifier denies a mutation edit, record the denial and move on;
  do not work around it.
- Test rules: fake producers and `TestGate` ordering, no clock, no real-time delay or polling
  sleep, every await bounded, owned cache directory.
- aislop gate: no finding other than the four baseline warnings in lane-common.md plus the ruled
  8-parameter `JournalBrokerHost` constructor warning. Keep CRLF line endings.
- Nothing you run may need elevation. Always pass `-NonInteractive` to `run-coverage.ps1`. Never
  end your turn to wait on a background command; poll inside the turn.

REPORT FILE: `WORKSPACE\task-B6-report.md` (the one file outside your worktree you may write).
Write the full report per lane-common.md, including a "Lock order" section for every new site and
a "Primary checkout" line with the output of `git -C C:\Users\mtsch\MFTLib status --short`.

Commit on `task/265-B6` with the brief's commit message, ending with
`Co-Authored-By: Claude <noreply@anthropic.com>`.

Reply with ONLY (under 15 lines): Status (DONE | DONE_WITH_CONCERNS | BLOCKED | NEEDS_CONTEXT),
commits (short SHA and subject), one-line test summary, concerns, report path.
