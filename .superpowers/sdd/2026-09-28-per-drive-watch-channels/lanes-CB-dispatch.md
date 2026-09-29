You are a test lane on the MFTLib "per-drive watch channels" plan (C# on Windows). Your task (CB)
raises line coverage of the flat `MFTLib` namespace (everything under `MFTLib/` except
`MFTLib/Index/`) toward 100 percent with non-elevated tests. The project standard is 100 percent
line coverage for managed code; the plan's tranche target is at least main's 99.32 percent. Test
code only, unless a line is dead code (then say so; see below).

YOUR WORKTREE: `C:\Users\mtsch\MFTLib-worktrees\265-CB` (branch `task/265-CB`).
Expected HEAD: `ca649bba0a69bf9dc83aeaa60ebb522485063fe6`. FIRST ACTION: `git -C C:\Users\mtsch\MFTLib-worktrees\265-CB rev-parse HEAD`
must equal that SHA; on mismatch STOP and report; never merge, rebase or reset to self-correct.

WORKSPACE = `C:\Users\mtsch\MFTLib-worktrees\impl-265\.superpowers\sdd\2026-09-28-per-drive-watch-channels`
(read-only for you, except your report file).

Read first: `WORKSPACE\lane-common.md`, `WORKSPACE\global-constraints.md` (in full),
`WORKSPACE\broker-common.md` (older than your base; read the code), `WORKSPACE\orchestrator-rulings.md`
(rows C2-Q1, C5-Q1, C5-Q2, W40-R1).

Method:
1. Run `pwsh -NoProfile -File C:\Users\mtsch\MFTLib-worktrees\265-CB\scripts\run-coverage.ps1 -NonInteractive`
   once (background, poll inside your turn), then
   `python C:\Users\mtsch\MFTLib-worktrees\impl-265\.superpowers\scratch\nscov.py C:\Users\mtsch\MFTLib-worktrees\265-CB\MFTLib.Tests\coverage.xml "MFTLib(other)"`
   (it prints per-namespace totals and the uncovered lines per file for the named namespace; it
   truncates each file's list at 14 lines, so read `coverage.xml` directly for any file with more).
2. For every uncovered line, classify it: (a) reachable by a non-elevated test through the public
   or internal surface, with in-process fakes (HostChannelHarness, BrokerTestHarness,
   ScriptedBroker, FakeTimeProvider, the native seams); (b) reachable only with elevation or a
   real NTFS volume (the admin-only `RequiresAdmin` tests cover these in the owner's elevated run);
   (c) unreachable (dead code, or a defensive branch no input can reach). Read the code; do not
   guess from the file name.
3. Write tests for every (a) line, in the existing test file of that area (or a new focused file
   named after the class). Each test must assert behavior, not merely execute the line.
4. For (c), do NOT delete code in this lane. List each with the reason; the controller decides.
5. Re-run the coverage and report the new totals and the remaining uncovered lines with their
   classification.

Test rules: FakeTimeProvider where time matters; no real-time delay or polling sleep; no
elapsed-time assertion; every await in a test and its helpers bounded; owned cache directory;
`[DoNotParallelize]` where process-wide seams are touched (the `NativeSeamIsolationTests` guard
enforces it); no real elevated process, nothing that triggers UAC: never run `run-coverage.ps1`
without `-NonInteractive`, never call the real launcher. Host faults reach tests only through
production surfaces (C2-Q1). These are coverage tests for existing behavior, so they need no RED
evidence; if one fails against the code, that is a suspected defect: leave it out of the commit and
report it with test name, assertion, observed value, and the spec line.

Verification: the targeted classes; ONE final `run-coverage.ps1 -NonInteractive` after your last
edit (0 failed; paste Total/Passed/Failed and the nscov.py totals); ONE `aislop scan .` (gate: the
four baseline warnings plus the ruled 8-parameter `JournalBrokerHost` constructor warning). Keep
CRLF line endings. Never end your turn to wait on a background command; poll inside the turn.

Commit on `task/265-CB`: "Broker and volume code reachable without elevation is covered by tests",
ending with `Co-Authored-By: Claude <noreply@anthropic.com>`.

REPORT FILE: `WORKSPACE\task-CB-report.md`: before and after totals, a table of every uncovered
line at the start (file:line, class a/b/c, the covering test or the reason), suspected defects,
commands and outputs, and the output of `git -C C:\Users\mtsch\MFTLib status --short`.

Reply with ONLY (under 12 lines): Status, commit, MFTLib(other) coverage before and after, counts of
a/b/c lines, suspected defects, report path.
