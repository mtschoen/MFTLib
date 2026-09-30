# Task: draft the pull request body for impl/265-per-drive-channels -> main

You are a documentation lane inside the git worktree C:/Users/mtsch/MFTLib-worktrees/impl-265
(branch impl/265-per-drive-channels, HEAD 66bdb6d, base main 3597586). You WRITE EXACTLY ONE FILE:
`.superpowers/sdd/2026-09-28-per-drive-watch-channels/v1-pr-body-draft.md`. Do not edit any other
file, do not run git add/commit/push, do not acquire or release any lock, do not run builds or tests.
Use absolute paths. If any referenced file is missing, STOP and write the single word BLOCKED plus
the missing path into the output file.

## Inputs (read all of them; all paths relative to the worktree root)

- `.superpowers/sdd/2026-09-28-per-drive-watch-channels/lanes/problem-statement.md`: the house rule
  for the `## Problem` section a pull request must carry. Follow it exactly.
- `.superpowers/sdd/2026-09-28-per-drive-watch-channels/lanes/spec-2026-09-28-per-drive-watch-channels-design.md` (a copy taken from git; the tracked file was retired by the head commit): the specification. Read
  sections 1 and 2 fully (architecture, per-drive channels, state machine), skim the rest.
- `.superpowers/sdd/2026-09-28-per-drive-watch-channels/lanes/plan-2026-09-28-per-drive-watch-channels.md` (a copy taken from git): the plan. Read the wave table,
  the dependency table, the tasks V1, F1, G1, G2, and "Release bookkeeping". The F1/G1/G2 tables
  are the per-consumer migration work.
- `AGENTS.md` at the worktree root: section "No backward compatibility" (a breaking change is never
  described as a cost, risk or trade-off; migration is listed as plain work by file).
- `.superpowers/sdd/2026-09-28-per-drive-watch-channels/progress.md`: read the last two
  "## RESUME POINT" sections and the "## SESSION 5" section (gate numbers, Linux-under-load result,
  follow-ups owed).
- `.superpowers/sdd/2026-09-28-per-drive-watch-channels/final-rereview.md` and `task-FX4-report.md`
  in the same folder (the whole-branch final review verdicts and the deleted-identifier grep).
- `.superpowers/sdd/2026-09-28-per-drive-watch-channels/lanes/v1-diffstat.txt` and
  `lanes/v1-commits.txt`: the branch's diff stat and commit list.
- `CHANGELOG.md` at the worktree root: the 0.3.0 entry the branch added.

## Output: a complete pull request body, Markdown, in this order

1. Title line first, as `Title: <title>`: one sentence stating the resulting behavior (each watched
   drive gets its own broker channel, scans and watches are per drive), no ticket numbers in it.
2. `## Problem` per problem-statement.md: what was observed (one broker pipe, demux and watch
   generation shared by every drive; a fault on one drive ended every drive's watch; a timed-out
   stop let a late EndWatchAck end the next watch), not the fix. Link
   `Closes #252` and `Refs #265` on their own line right after this section, with the full URLs
   https://gitea.fleet.sticktoitive.net/schoen/MFTLib/issues/252 and .../issues/265 spelled out.
3. `## What changes`: the per-drive architecture in prose, from the specification: control pipe plus
   one pipe per drive, per-drive state machine (states and transitions by name), heartbeats and
   liveness, ParseThreadAllocator, concurrent open with SettledCount, CatchUpLost and the
   three-rescans rule, batched StartWatchingAsync/StopWatchingAsync/RescanAsync returning per-drive
   results, BrokerProcess replacing JournalBrokerClient, BrokerTestHarness. Name the public types
   and members a consumer meets. Keep it factual and under 60 lines.
4. `## Waves`: the wave list from the plan, one line per wave with its task ids and one-phrase
   purposes.
5. `## Consumer migration`: per AGENTS.md "No backward compatibility": one subsection per consumer
   (file-wizard, git-wizard), each a table `File | Change` reproduced from the plan's F1, G1 and G2
   tables (condense wording, keep every file). State that the pin-bump pull requests carry this
   work. Never call a breaking change a risk, cost or trade-off.
6. `## Gates`: a table with one row per gate and a `<<FILL>>` placeholder for every number the
   orchestrator will fill from the merged-head run: Windows whole suite (total/passed/skipped/
   failed), line coverage MFTLib and MFTLib.Index (with the nine non-Windows uncovered lines listed
   by file:line and the reason: Linux-only code paths in BlockFile.Flush.cs and CacheDirectory.cs),
   test-coverage-status.ps1, native builds Release and Debug, native coverage, aislop
   (score and the five accepted warnings by file:line), Linux coverage-linux.sh under load (from the
   ledger: 5 of 5 whole runs at 66bdb6d, 1621 passed, 0 failed, 84 skipped, 1705 total, under 32 CPU
   hogs), the elevated (admin) run, and the deleted-identifier grep (list the 24 identifiers from
   final-rereview.md Important 4 and state zero hits outside docs/superpowers and CHANGELOG.md).
7. `## Follow-ups filed`: a bullet list with `<<ISSUE>>` placeholders for: (a) failed scan followed
   by disposal after the restart decision ends with AggregateException; (b) BrokerDiagnostics
   ReplaceWriterForTest can lose one line; (c) launch-timeout test waits on a real 50 ms timer;
   (d) spec gaps left open (stop after a failed restart; whether a fresh start discards a faulted
   instance's outstanding fault; ruling C5-Q1); (e) deferred minors; (f) MFTLibTestExtensions
   coverage 80.37 percent out of scope. One line each stating the observed problem.
8. `## Still owed`: the attended measurements M1 to M3 from the plan (name each), the consumer
   pin-bump pull requests F1, G1+G2, and issue 264 (TestExtensions package) as its own item.
9. Last line exactly: `🤖 Generated with [Claude Code](https://claude.com/claude-code)`

## Style

Plain ASCII apart from that last line: no em-dashes, no en-dashes, no smart quotes. Full words, no
abbreviations. Every issue or pull request number appears with its full URL the first time. Do not
describe anything as "old" vs "new" API; describe the current contract. No first person.

## Fallback (added after a first attempt lost its output)

A previous run of this task composed the body and then had its file write refused by a project
lock hook. The lock is now released. Write the file as the FIRST action once the body is composed.
If the write is refused again for any reason, print the ENTIRE body to standard output between two
lines reading exactly `BEGIN-BODY` and `END-BODY` so nothing is lost. The follow-up issues are now
filed; use these instead of `<<ISSUE>>` placeholders, each with its full URL
https://gitea.fleet.sticktoitive.net/schoen/MFTLib/issues/<n>: 293 (failed rescan then disposal
throws AggregateException), 294 (BrokerDiagnostics.Log can drop a line on writer replacement),
295 (launch-timeout test waits on a real 50 ms timer), 296 (spec gaps left open), 297 (deferred
minors), 298 (MFTLibTestExtensions coverage outside the gate). The branch head is now 7184c25,
which retires the executed plan and specification (both deleted); the deleted-identifier grep
and every gate number refer to 66bdb6d, the code head, and the PR body says so in one sentence.
