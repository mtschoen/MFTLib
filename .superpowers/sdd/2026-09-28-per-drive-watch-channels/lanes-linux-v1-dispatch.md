You are a verification lane on the MFTLib "per-drive watch channels" plan. Your job: run the
Linux build and coverage on one pushed integration commit on host `llamabox`, and report. You
change no code and commit nothing.

Target commit: `38fbca32307ec11f0792c2665583542be04da1bd` on branch `impl/265-per-drive-channels` of the Gitea remote
(`gitea@gitea.fleet.sticktoitive.net:schoen/MFTLib.git`).

Steps (run over `ssh llamabox '...'` from the Bash tool; keep each ssh command single-purpose):
1. A scratch clone from an earlier run exists at `~/scratch/mftlib-265-w3` on llamabox. Reuse it:
   `cd ~/scratch/mftlib-265-w3 && git fetch origin impl/265-per-drive-channels && git checkout --detach 38fbca32307ec11f0792c2665583542be04da1bd && git clean -ffxd && git status --short`.
   (That clone is disposable scratch owned by this plan; `git clean` there is intended. Never run
   git clean anywhere else.) If the clone is missing, clone the remote into that path first.
   Confirm `git rev-parse HEAD` equals `38fbca32307ec11f0792c2665583542be04da1bd`; on mismatch STOP and report.
2. `./init.sh --build`, output to `~/scratch/mftlib-265-w3-build.log` on llamabox. It can take
   several minutes: run it with `nohup ... &` and poll the log for completion inside your turn
   (never end your turn to wait; the ssh session may drop).
3. `scripts/coverage-linux.sh`, output to `~/scratch/mftlib-265-w3-coverage.log`, same way.
4. Collect: exit codes of both; native test totals; managed totals (total, passed, failed,
   skipped); line coverage; every failed test name with its message; the names of the new test
   classes of this plan that ran (grep the log or the TRX for `BlockFileRangedFlushTests`,
   `JournalBrokerHostLivenessTests`, `BrokerProcessLivenessTests`, `FileIndexCatchUpLossTests`,
   `FileIndexConcurrentRescanTests`, `FileIndexDisposalOrderTests`, `FileIndexCallbackReentrancyTests`, `FileIndexWatchRecoveryTests`, `FileIndexConcurrentOpenTests`, `FileIndexBatchedOperationTests`, `BrokerCrossDriveLivenessTests`, `BrokerIndexWatchSourceTests`) and whether they passed or
   were excluded. W40's `msync` path (BlockFile.Flush on Linux) has never run on Linux before:
   state explicitly whether `BlockFileRangedFlushTests` ran on Linux and its result.
5. If anything fails, do not fix it. Capture the failure and, where the log shows it, the likely
   cause.

Nothing you run may need elevation or sudo. Do not touch any other directory on llamabox.

REPORT FILE: `C:\Users\mtsch\MFTLib-worktrees\impl-265\.superpowers\sdd\2026-09-28-per-drive-watch-channels\linux-v1-report.md`
(the only file you may write on this machine). Include every command run and the relevant output
lines, plain ASCII punctuation only.

Reply with ONLY (under 10 lines): Status (GREEN | RED | BLOCKED), managed and native totals, line
coverage, failed tests if any, whether BlockFileRangedFlushTests ran, report path.
