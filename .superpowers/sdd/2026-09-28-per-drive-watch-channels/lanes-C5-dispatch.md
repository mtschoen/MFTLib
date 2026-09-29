You are an implementer lane on the MFTLib "per-drive watch channels" plan (C# and C++ on
Windows). Your task is C5: host liveness (operation state per pipe, a dedicated heartbeat thread,
a processing watchdog, bounded catch-up reads). It is on the broker trunk; C1 to C3b (new wire
protocol, channel host, `BrokerProcess` client, `BrokerTestHarness`, host test ports), A4
(bounded journal reads) and W40 (ranged block flush) are merged on your base.

YOUR WORKTREE: `C:\Users\mtsch\MFTLib-worktrees\265-C5` (branch `task/265-C5`).
Expected HEAD: `566718bb6f80795a6f58e50ee3441dc81220cdaf`. FIRST ACTION: `git -C C:\Users\mtsch\MFTLib-worktrees\265-C5 rev-parse HEAD`
must equal that SHA; on mismatch STOP and report; never merge, rebase or reset to self-correct.

WORKSPACE = `C:\Users\mtsch\MFTLib-worktrees\impl-265\.superpowers\sdd\2026-09-28-per-drive-watch-channels`
(read-only for you, except your report file). If any file named below is missing, STOP and reply
BLOCKED naming it.

Read, in this order:
1. `WORKSPACE\lane-common.md`: lane rules (worktree, lock, build, verification, report contract).
2. `WORKSPACE\global-constraints.md`: binding constraints and test rules. Read in full.
3. `WORKSPACE\broker-common.md`: what exists on your base and the rules for broker tasks. Read in
   full. Where it and the brief disagree, it wins.
4. `WORKSPACE\task-C5-brief.md`: read this first among requirements. It is your requirements,
   with the exact names and values to use verbatim, EXCEPT where the rulings below amend it.
5. `WORKSPACE\orchestrator-rulings.md`: rows W2-1, W4-1, W4-2, W4-3, N-1, C2-Q1 and W40-R1 bind
   this task with the same force as the brief.

Rulings and context the brief cannot know:
- W4-3 and W40 (supersede the brief's file list): the ranged flush is DONE. `BlockFile.Flush(Action<long>?)`,
  `BlockWriter.Complete(DateTime, Action<long>?)`, the `msync`/`FlushViewOfFile` code and every
  caller sweep exist on your base (see `MFTLib/Index/BlockFile.Flush.cs`). Do NOT create
  `Libc.cs`, do not edit `BlockFile*.cs`, `BlockWriter.cs` or `Kernel32.cs`, and sweep no caller.
  Your only flush work: `RealBlockSectionWriter` passes a reporter that republishes the pipe's
  state per flushed range. The brief's `BlockFile_Flush_ReportsEachRange` test already exists
  (W40); instead write a test that each flushed range restarts the scan pipe's progress clock.
- W4-1: `MFTLib/Broker/BrokerLiveness.cs` already exists (C2) with `HeartbeatInterval`,
  `ProcessingLimit`, `StallLimit` and `ControlReplyTimeout`. Use it; do not redefine it. You add
  `CatchUpBufferReadsPerCall = 256` to it.
- W2-1: `UsnJournalCatchUpSource(string driveLetter, UsnJournalCursor since)` gains
  `int maximumBufferReads` in this task, together with the bounded read loop. The bounded read is
  reached through A4's managed entry `ReadUsnJournalBounded(since, maximumBufferReads)` in
  `MFTLib/Journal/MftVolume.Journal.cs` (0 = read to the tip).
- W4-2: `BlockedWriteOnX_...` and `IdleSession_...` assert host-side behavior only, through
  `HostChannelHarness`: Y's pipe gets a heartbeat every interval, X's pipe gets none after the
  held write, the control pipe gets one every 5 s. The client assertions ("only X faults",
  "`HasEnded` stays false") belong to task C8; do not write them.
- N-1: `HeartbeatSender_RunsOnDedicatedThread` asserts, through a test hook, that the sender
  thread has `IsThreadPoolThread == false` and `IsBackground == true`. No pool saturation.
- C1 deviation: `IBrokerOperationReporter.Processing(string stepName)`; the parameter is
  `stepName`.
- C2-Q1 (owner): tests surface host errors only through production surfaces; add no harness
  member that observes host failures.
- C2 already enforces `ControlReplyTimeout` on the client for a started control write and for an
  acknowledged channel that never connects; the client stall limit is task C7's (running in
  parallel). Do not touch `MFTLib/Broker/Client/`.
- Not yours: the ledger's note about a bounded shutdown flush of the diagnostics writer (A2 minor).
- W40-R1: every NEW test needs RED evidence in the report: the exact `dotnet test` command with its
  `--filter`, and the failing output, seen before the implementation (or, if you implemented
  first, against an uncommitted scratch mutation of the production behavior it pins).
- Time: every limit reads the injected `TimeProvider`; tests use `FakeTimeProvider`; no real
  `Task.Delay`, no elapsed-time assertion, every await bounded.
- aislop gate: no finding other than the four baseline warnings in lane-common.md plus the ruled
  parameter-count warning on the 8-parameter `JournalBrokerHost` constructor. Keep CRLF endings.
- Nothing you run may need elevation. Always pass `-NonInteractive` to `run-coverage.ps1`.
- Never end your turn to wait on a background command; poll inside the turn.

REPORT FILE: `WORKSPACE\task-C5-report.md` (the one file outside your worktree you may write).
Write the full report there per lane-common.md, including a "Threads and locks" section (which
thread runs the sender, what it waits on, which lock guards each pipe writer's state, and why the
sender can never block on a pipe write) and a "Primary checkout" line with the output of
`git -C C:\Users\mtsch\MFTLib status --short`.

Commit on `task/265-C5` with the brief's commit message, ending with
`Co-Authored-By: Claude <noreply@anthropic.com>`.

Reply with ONLY (under 15 lines): Status (DONE | DONE_WITH_CONCERNS | BLOCKED | NEEDS_CONTEXT),
commits (short SHA and subject), one-line test summary, concerns, report path.
