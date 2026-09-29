You are an implementer lane on the MFTLib "per-drive watch channels" plan (C# and C++ on
Windows). Your task is C8: cross-drive liveness scenarios, an in-process `FileIndex` over
`BrokerTestHarness` with a `FakeTimeProvider` on both host and client. Everything these scenarios
exercise is merged on your base: host liveness (C5), client stall limit and reply timeouts (C7),
broker watch channels (C6), automatic recovery (B6), the lost-catch-up loop (B5).

YOUR WORKTREE: `C:\Users\mtsch\MFTLib-worktrees\265-C8` (branch `task/265-C8`).
Expected HEAD: `d1a20a999a45ad67557ea9536e759d8a1da73e98`. FIRST ACTION: `git -C C:\Users\mtsch\MFTLib-worktrees\265-C8 rev-parse HEAD`
must equal that SHA; on mismatch STOP and report; never merge, rebase or reset to self-correct.

WORKSPACE = `C:\Users\mtsch\MFTLib-worktrees\impl-265\.superpowers\sdd\2026-09-28-per-drive-watch-channels`
(read-only for you, except your report file). If any file named below is missing, STOP and reply
BLOCKED naming it.

Read, in this order:
1. `WORKSPACE\lane-common.md`: lane rules (worktree, lock, build, verification, report contract).
2. `WORKSPACE\global-constraints.md`: binding constraints and test rules. In full.
3. `WORKSPACE\broker-common.md`: broker context and rules (its "What exists" list is older than
   your base; read the code).
4. `WORKSPACE\task-C8-brief.md`: read this first among requirements, with the exact names to use
   verbatim, EXCEPT where the rulings below amend it.
5. `WORKSPACE\orchestrator-rulings.md`: rows N-2, W4-2, W5-3, C2-Q1, C5-Q1, C5-Q2, W40-R1 bind
   this task.

Rulings and context the brief cannot know:
- N-2: the cases are integration scenarios. For each, the report names the precondition it proves,
  and shows it failing by disabling that precondition in an uncommitted scratch edit where that is
  practical (literal command and real failing output, per W40-R1). Where it is not practical, say
  why.
- W4-2: C8 owns the client-side assertions C5 could not make: in a scenario where a write to X's
  pipe is held, only X faults (`Channel`, client stall limit) while Y keeps receiving heartbeats and
  keeps watching; and an idle session (no requests, no channels) past the stall limit keeps
  `BrokerProcess.HasEnded` false. Add both as tests in your file.
- W5-3: C6 named its test `HostError_IsDriveWatchFault`; your `HostErrorOverBroker_RecoversByRescan`
  owns the recovery assertion.
- C5-Q1: the host heartbeats a `Processing` pipe that made progress within the processing limit;
  C5-Q2: a pipe whose write is in flight is skipped by the heartbeat sender, and the client stall
  limit ends it.
- C2-Q1 (owner): host faults reach tests only through production surfaces; no harness member that
  observes host failures.
- If a scenario fails, it is a bug in the task that owns the behavior (C5, C6, C7, B5, B6): write
  the regression test in the owning test file, fix it minimally in production, and name it clearly
  in the report as a defect found, with the cause at file:line. If the fix is not small, stop and
  report DONE_WITH_CONCERNS with the failing test left out of the commit and described in the
  report.
- The known C1 frame-length defect was fixed by C2. The catch-up read contract: a bounded read at
  the tip returns no entries; a read returning entries without advancing fails the catch-up (fix
  W4fix).
- Test rules: `FakeTimeProvider` on both sides, advanced explicitly; no real-time delay or polling
  sleep; no elapsed-time assertion; every await in a test and in its helpers bounded; owned cache
  directory; `[DoNotParallelize]` where process-wide state is touched.
- Run the whole suite once AFTER your last edit, then aislop; report both.
- aislop gate: no finding other than the four baseline warnings in lane-common.md plus the ruled
  8-parameter `JournalBrokerHost` constructor warning. Keep CRLF line endings.
- Nothing you run may need elevation; no real elevated process. Always pass `-NonInteractive` to
  `run-coverage.ps1`. Never end your turn to wait on a background command; poll inside the turn.

REPORT FILE: `WORKSPACE\task-C8-report.md` (the one file outside your worktree you may write).
Write the full report per lane-common.md, with a per-test table (precondition proved, how shown
failing, literal command, output) and a "Primary checkout" line with the output of
`git -C C:\Users\mtsch\MFTLib status --short`.

Commit on `task/265-C8` with the brief's commit message, ending with
`Co-Authored-By: Claude <noreply@anthropic.com>`.

Reply with ONLY (under 15 lines): Status (DONE | DONE_WITH_CONCERNS | BLOCKED | NEEDS_CONTEXT),
commits (short SHA and subject), one-line test summary, concerns, report path.
