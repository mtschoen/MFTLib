You are an implementer lane on the MFTLib "per-drive watch channels" plan (C# and C++ on
Windows). Your task is B7: the batched `FileIndex` entry points (list and no-list forms of start,
stop, rescan and catch-up wait). Every per-drive piece is merged on your base: the per-drive watch
state machine (B1), gates and publication (B5), recovery (B6), concurrent open (B9), and the
broker watch channels (C6).

YOUR WORKTREE: `C:\Users\mtsch\MFTLib-worktrees\265-B7` (branch `task/265-B7`).
Expected HEAD: `d1a20a999a45ad67557ea9536e759d8a1da73e98`. FIRST ACTION: `git -C C:\Users\mtsch\MFTLib-worktrees\265-B7 rev-parse HEAD`
must equal that SHA; on mismatch STOP and report; never merge, rebase or reset to self-correct.

WORKSPACE = `C:\Users\mtsch\MFTLib-worktrees\impl-265\.superpowers\sdd\2026-09-28-per-drive-watch-channels`
(read-only for you, except your report file). If any file named below is missing, STOP and reply
BLOCKED naming it.

Read, in this order:
1. `WORKSPACE\lane-common.md`: lane rules (worktree, lock, build, verification, report contract).
2. `WORKSPACE\global-constraints.md`: binding constraints, state machine, test rules. In full.
3. `WORKSPACE\task-B7-brief.md`: read this first among requirements, with the exact names and
   signatures to use verbatim, EXCEPT where the context below amends it.
4. Spec section 3, "Batched-call contract", and 2.6.8, in
   `docs\superpowers\specs\2026-09-28-per-drive-watch-channels-design.md` in your worktree.
5. `WORKSPACE\orchestrator-rulings.md`: rows B4-Q1, B5-Q1, W40-R1 bind this task.
6. For lock order and recovery: the "Lock order" and "Decisions" sections of
   `WORKSPACE\task-B5-report.md` and `WORKSPACE\task-B6-report.md`.

Context the brief cannot know:
- Already done by B5 (ruling B4-Q1): the single-drive `RescanAsync` throws
  `InvalidOperationException` carrying `DriveStatus.MftProducerFailureMessage` (message
  "Drive X was not rescanned: <producer message>", producer exception as `InnerException`) when the
  producer returns no block, and throws `JournalCatchUpLostException` after a lost-catch-up stop.
  Do not reimplement them; your `SingleRescan_*` tests then pass on arrival: show their RED with
  an uncommitted scratch mutation that restores the old silent return. The brief's audit of
  existing tests that expected a failed rescan to complete normally was largely done by B5; re-run
  the audit (Grep) and fix any remaining case.
- NOTE FOR B7 from earlier tasks: port the 10 dropped `WaitForCatchUpAsync_AllDrives_*` cases
  (dropped by B2 because the all-drives overload did not exist yet; read them with
  `git show c1d43784:MFTLib.Tests/Index/<file>`; B2's commit message on the integration branch
  names them: `git log --all --grep "WaitForCatchUpAsync_AllDrives"`), and the aggregate
  all-drives catch-up case B3 dropped; keep "failed source start raises no WatchFaulted". Also the
  old all-drives `WaitForCatchUpAsync(token)` assertion C6 dropped from
  `WatchFailureObservationTests` (see C6's commit message). Each ported case keeps its name when
  the meaning is unchanged; list them in your report.
- Automatic recovery exists (B6): a `Drive` or `Apply` fault publishes `Recovering` and queues a
  recovery; tests that need a drive to stay faulted use the internal `HoldEveryRecovery()` seam
  (see B6's tests) or a `Channel` fault.
- Open owner spec gaps (do not decide them differently from the current code; name any choice in
  the report): what a stop does after a failed restart; whether a fresh start discards a faulted
  instance's outstanding fault.
- Handler reentrancy is task B8's (next); do not write a test that blocks inside a handler on a
  lifecycle call.
- W40-R1: every NEW test needs, in the report, the literal command (full `dotnet test` line with
  its literal `--filter`) and the real failing output, before implementation or against an
  uncommitted scratch mutation. A command template with a placeholder does not count.
- Test rules: fake producers and `TestGate` ordering, no clock, no real-time delay or polling
  sleep, every await in a test AND in its helpers bounded by a timeout, owned cache directory.
- Run the whole suite once AFTER your last edit, then aislop; report both.
- aislop gate: no finding other than the four baseline warnings in lane-common.md plus the ruled
  8-parameter `JournalBrokerHost` constructor warning. Keep CRLF line endings.
- Nothing you run may need elevation. Always pass `-NonInteractive` to `run-coverage.ps1`. Never
  end your turn to wait on a background command; poll inside the turn.

REPORT FILE: `WORKSPACE\task-B7-report.md` (the one file outside your worktree you may write).
Write the full report per lane-common.md, including the port list and a "Primary checkout" line
with the output of `git -C C:\Users\mtsch\MFTLib status --short`.

Commit on `task/265-B7` with the brief's commit message, ending with
`Co-Authored-By: Claude <noreply@anthropic.com>`.

Reply with ONLY (under 15 lines): Status (DONE | DONE_WITH_CONCERNS | BLOCKED | NEEDS_CONTEXT),
commits (short SHA and subject), one-line test summary, concerns, report path.
