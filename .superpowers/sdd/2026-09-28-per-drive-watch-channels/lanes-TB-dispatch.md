You are a test-accounting lane on the MFTLib "per-drive watch channels" plan (C# on Windows). The
plan's tranche rule: every base-commit test method in a file that a task deleted either has a
counterpart at HEAD or is named in a porting commit message as dropped, with its reason. An audit
found methods that are neither. Your task (call it TB) closes that gap: port what still describes
the contract, and account for the rest in one commit message. Test code only; no production change.

YOUR WORKTREE: `C:\Users\mtsch\MFTLib-worktrees\265-TB` (branch `task/265-TB`).
Expected HEAD: `__HEAD__`. FIRST ACTION: `git -C C:\Users\mtsch\MFTLib-worktrees\265-TB rev-parse HEAD`
must equal that SHA; on mismatch STOP and report; never merge, rebase or reset to self-correct.

WORKSPACE = `C:\Users\mtsch\MFTLib-worktrees\impl-265\.superpowers\sdd\2026-09-28-per-drive-watch-channels`
(read-only for you, except your report file). If any file named below is missing, STOP and reply
BLOCKED naming it.

Read, in this order:
1. `WORKSPACE\lane-common.md`: lane rules (worktree, lock, build, verification, report contract).
2. `WORKSPACE\global-constraints.md`: binding constraints and test rules. In full.
3. `WORKSPACE\broker-common.md` and `WORKSPACE\index-port-common.md`: the port rules. Both are
   older than your base (every broker and index task through B9 is merged); read the code.
4. `WORKSPACE\tranche-B-audit.md`: the audit. Its two "Not accounted for" tables are your work
   list. Base commit for all reads: `3597586` (`git show 3597586:<path>`).
5. `WORKSPACE\orchestrator-rulings.md`: rows C2-Q1, W5-1, W40-R1.

Method, for every row of both "Not accounted for" tables:
- Exclusion: rows for `WaitForCatchUpAsync_AllDrives_*` and any all-drives catch-up aggregate case
  belong to task B7 (running in parallel, which adds the all-drives overloads). List them in your
  report as "B7" and do nothing else with them.
- Behavior column "No ...": verify by reading the base test and the current code (one focused
  check each) that the behavior no longer exists under the per-drive contract. If it does not,
  the method goes into the commit message's dropped list with a one-line reason. If you find that
  it does still exist, treat it as the next case.
- "Yes ...", "Changed", "Partly": search HEAD for a test that already pins the same behavior under
  another name (grep for the production symbol and the scenario). If one exists, list the mapping
  (base name -> HEAD name) as "covered by". If none exists, port the case onto the current contract
  (keep the base name when the meaning is unchanged), in the existing test file of that area, at
  full assertion strength. A ported case that fails against the code is information: do not weaken
  it and do not change production code; leave it out of the commit, and report it as a suspected
  defect with the test name, assertion, observed value and spec line.
- The ABI-version-1 and FileIndexRescanCleanup items outside deleted files: account for them the
  same way.
Build the lists mechanically and make the counts in your report add up to the audit's table rows.

Test rules: FakeTimeProvider where time matters, no real-time delay or polling sleep, no
elapsed-time assertion, every await in a test and its helpers bounded, owned cache directory,
`[DoNotParallelize]` where process-wide seams are touched. Host faults reach tests only through
production surfaces (C2-Q1). Ports need no RED; there are no new tests beyond ports.

Verification: targeted runs of every class you touched; ONE `run-coverage.ps1 -NonInteractive`
after your last edit (0 failed; paste Total/Passed/Failed); ONE `aislop scan .` after it (gate: the
four baseline warnings plus the ruled 8-parameter `JournalBrokerHost` constructor warning, nothing
else). Keep CRLF line endings. Nothing you run may need elevation. Never end your turn to wait on a
background command; poll inside the turn.

Commit ONCE on `task/265-TB`, subject "Account for every base test method the per-drive redesign
removed", body: a "Ported" list (base name -> file), a "Covered by" list (base name -> HEAD name),
a "Dropped" list (one line per method: name - reason), and a "Left to B7" list; end with
`Co-Authored-By: Claude <noreply@anthropic.com>`. If a file has no test change the commit may still
carry the lists: it must contain at least your ported tests.

REPORT FILE: `WORKSPACE\task-TB-report.md` (the one file outside your worktree you may write): the
four lists with counts, suspected defects, commands and outputs, files changed, and a "Primary
checkout" line with the output of `git -C C:\Users\mtsch\MFTLib status --short`.

Reply with ONLY (under 15 lines): Status, commit, counts (ported / covered by / dropped / left to
B7 / suspected defects), one-line test summary, report path.
