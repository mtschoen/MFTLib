# Task FX6: admin failure of LiveWatch_CreateModifyDeleteCycle_ReportsOneChangePerRealTransition

## Verdict

Pre-existing on main, and deterministic rather than flaky. Not a branch defect, not environmental.
Nothing was committed. The failure is a contract conflict between this live test and the unit
tests that sit beside it on main, so it needs an owner decision rather than a fix in the branch.

## Run table (elevated, 5 runs each, instrumented test binary, same machine, same volume C:)

| Run | main 3597586 | branch 7184c25 (code 66bdb6d) |
| --- | --- | --- |
| 1 | FAIL, Modified expected 1 actual 2 | FAIL, Modified expected 1 actual 2 |
| 2 | FAIL, same | FAIL, same |
| 3 | FAIL, same | FAIL, same |
| 4 | FAIL, same | FAIL, same |
| 5 | FAIL, same | FAIL, same |

Main run 1 took 15 s: the delete arrived as RenameOldName with no FileDelete|Close inside the
watch timeout (676 unrelated batches), so the loop ended on its timeout token. It still failed on
the Modified assert first. The other nine runs finished in about 35 ms each.

Launch: one elevated pwsh per worktree via Start-Process -Verb RunAs -Wait, each running
dotnet test --no-build --filter FullyQualifiedName~JournalCloseCoalescingLiveTests five times.
The first branch prompt was cancelled; the relaunch was accepted.

## Raw records (instrumented dump, every run on both heads has this exact shape)

Branch run 1 (file mftlib-close-test-1ccfef08...tmp, record 11836475, seq 336, one batch):

| USN | Reason | Close | ApplyOne result |
| --- | --- | --- | --- |
| 611679259512 | FileCreate | no | Created |
| 611679259680 | DataExtend, FileCreate | no | **Modified** (classification = DataExtend) |
| 611679259848 | DataExtend, FileCreate, Close | yes | none (all bits already reported) |
| 611679260016 | DataExtend | no | Modified (new cycle after the close) |
| 611679260184 | DataExtend, Close | yes | none |
| 611679260352 | FileDelete, Close | yes | Deleted |

Main run 2 (record 11832285, seq 296) is identical in reasons, order and replay result, as are
main runs 3 to 5 and branch runs 2 to 5. There is no passing run to contrast on either head: 0 of 10
passed. The record set is the same on main and branch, so the branch's delivery is not implicated.

## Cause

WriteAllTextAsync opens, creates and writes, and NTFS writes a record at each new reason bit:
FileCreate, then FileCreate|DataExtend, then the close. In `MFTLib/Index/JournalMutator.cs` on main,
the first record marks only FileCreate reported (line 133, `cycles.MarkReported`). The second record
then computes `classification = meaningful & ~reported` (line 78) = DataExtend, and falls to
`else if (classification != UsnReason.None)` (line 107), `ApplyModification`, which raises
Modified. The append cycle raises the second, expected Modified. Total: two.

That behavior is the specified contract on main. `MFTLib.Tests/Index/JournalMutatorCloseCoalescingTests.cs`
has `CreateWriteCloseCycle_InOneBatch_ReportsOneCreatedAndOneModified` (from line 92) asserting
Created then Modified for exactly this three-record cycle, and
`CumulativeReasons_AcrossBatches_...` asserting "Intermediate cumulative record must be classified
as Modified, not a duplicate Created." The live test, added by PR 168 (e45c97b, 2026-09-15), instead
assumes the create cycle raises no Modified. Both came from the same PR; the non-admin tests pass and
the admin one has failed since it was written, because the elevated suite was not run.

## Branch diff check

The test calls `MftVolume.WatchUsnJournal` directly. On the branch that method only moves its
handle and cancellation event into `UsnWatchSession`, calling the same native
`_watchUsnJournalBatchCancelable` with the same arguments. The native watch export is unchanged;
the only `usn_journal.cpp` change is the buffer-read bound on `ReadUsnJournal`, which the watch
path does not call. The `JournalMutator` change (removed guard) is unreachable for this input,
since `ApplyModification` is only entered with a nonzero classification.

## Decision needed (not taken)

Either the live test is wrong (a write before the create-close is part of creation and the unit
tests are right, so the assert should expect two Modified or count only post-create cycles), or the
mutator contract should fold a DataExtend inside the create cycle into the Created change (then the
two unit tests above change with it). Both live on main; neither belongs in the per-drive branch.

## Committed

Nothing. The scratch instrumentation was reverted in the branch worktree (265-FX6), whose
`git status --short` is now empty. It remains uncommitted only in the detached main-baseline
worktree.
