You are the final-review fix lane on the MFTLib "per-drive watch channels" plan (C# and C++ on
Windows). The whole branch is implemented and reviewed; a final whole-branch review found a short
list of issues. You fix ALL of them in one pass. There is no second fix wave, so be thorough.

YOUR WORKTREE: `C:\Users\mtsch\MFTLib-worktrees\265-FF` (branch `task/265-FF`).
Expected HEAD: `38fbca32307ec11f0792c2665583542be04da1bd`. FIRST ACTION:
`git -C C:\Users\mtsch\MFTLib-worktrees\265-FF rev-parse HEAD` must equal that SHA; on mismatch STOP
and report; never merge, rebase or reset to self-correct.

WORKSPACE = `C:\Users\mtsch\MFTLib-worktrees\impl-265\.superpowers\sdd\2026-09-28-per-drive-watch-channels`
(read-only for you, except your report file).

Read first: `WORKSPACE\lane-common.md` (lane rules; the controller owns your worktree: do not
acquire or release any lock), `WORKSPACE\global-constraints.md` (in full), `WORKSPACE\final-review.md`
(the findings, each with file:line), `WORKSPACE\orchestrator-rulings.md` (rows W40-R1, W40-R1a,
CB-Q1, FR-Q1).

Fix list (the complete list; each production fix gets a failing-first regression test with the
literal command and real failing output in your report, per W40-R1/W40-R1a):
1. Important 1: `MFTLib/Broker/BrokerDiagnosticsWriter.cs:53-86`: the enqueue/drop accounting race
   can strand `FlushAsync` (a flush snapshots `_accepted` between the increment and the drop's
   decrement). Make accounting such that a flush waits only for records that will be processed
   (for example count a record as accepted only once it is actually queued, or count drops as
   processed), and a flush always completes. Deterministic test through the existing writer seams.
2. Important 2: `MFTLib/Broker/Client/BrokerProcess.Control.cs:120-123`: a wrong known control reply
   throws `BrokerChannelLostException` with a null drive (process-ending by contract,
   `BrokerChannelLostException.cs:7-9`) without ending the process. Build the reason, call
   `RequestEnd(reason)`, then throw; test that `HasEnded` becomes true, `Ended` fires once, and
   later requests fail.
3. Important 3: `MFTLibNative/mft_api.h:34, :73-76`: `MftParseControl` sits inside
   `#pragma pack(push, 1)` although its fields are read as shared 32-bit values
   (`MFTLibNative/internal.h:44-53`). Move it outside the packed region (natural alignment), keep
   the managed declaration's layout in step, and add size/offset/alignment assertions
   (`static_assert` in native; a managed layout test). Rebuild native with MSBuild (x64 Release)
   and run the native test program if the repository has one for this header; run the Linux-side
   compile only if you can without leaving the worktree (otherwise say it is owed).
4. Important 4 (except the plan/spec deletion, deferred to V1 by ruling FR-Q1): remove the
   `.editorconfig` sections for the deleted `JournalBrokerScanSession` files (`.editorconfig:168-174`);
   rename the test methods whose names carry deleted API names
   (`MFTLib.Tests/BrokerProcessTests.BlockSections.cs:11, :63, :101, :135` `ArmScanAndCatchUpAsync_*`;
   `MFTLib.Tests/VolumeQueryClientTests.cs:10, :32` `QueryVolumesAsync_*`) to names that state what
   they pin against the current API; reword `MFTLib.Tests/BenchmarkRunnerTests.cs:1131`. Then run one
   `git grep -F` per deleted identifier (the list is in `WORKSPACE\docs-common.md`) over the repository
   excluding `docs/superpowers/` and `CHANGELOG.md`, and report every result count (all must be 0).
5. Important 5: `MFTLib.Tests/BrokerProcessLaunchTests.cs:188-198` waits on a real 50 ms timer with
   no independent bound: route that timeout through an injected `TimeProvider` (FakeTimeProvider in
   the test) if the production code under test takes one, otherwise bound the wait with a hang
   guard and say why a clock cannot be injected; `MFTLib.Tests/Index/FileIndexWatchRescanTests.cs:330`
   awaits a locally cancelled rescan without the hang guard: bound it.
6. Minor 1: `MFTLib.Tests/BrokerProtocolTests.cs:52-59` and `MFTLib.Tests/BrokerProtocolTests.Scan.cs:207-212`:
   capture the exceptions and assert that the messages name the rejected frame kind and cause
   values (these survived an earlier mutation check).
7. Minor 2: `MFTLib/Mft/ParseThreadAllowance.cs:3-8` and the public parse APIs' docs
   (`MFTLib/Mft/MftVolume.cs:112-115` and `ReadRecordBatches`): state that one allowance attaches to one
   running parse at a time and document the `InvalidOperationException`.
8. Minor 3: rewrite the three stale comments as target-state explanations:
   `MFTLib.Tests/Index/FileIndexWatchRescanTests.cs:272-273`, `MFTLib.Tests/Index/FileIndexCatchUpLossTests.cs:176-178`,
   `MFTLib.Tests/JournalBrokerHostLivenessTests.StalledPipe.cs:78-79` (six 5-second visits).
Do not touch anything else. If a fix turns out to need a design decision the review does not make,
make the least surprising choice and name it in the report.

Verification after your last edit: native Release build (MSBuild x64) if you touched native code;
`dotnet build -c Release -p:Platform=x64`; targeted classes for every test you changed or added;
ONE `pwsh -NoProfile -File C:\Users\mtsch\MFTLib-worktrees\265-FF\scripts\run-coverage.ps1 -NonInteractive`
(background, poll inside your turn; paste Total/Passed/Failed); ONE `aislop scan` of the worktree
(gate: exactly the four baseline warnings plus the ruled 8-parameter `JournalBrokerHost` constructor
warning). Keep CRLF line endings. Nothing may need elevation. Never end your turn to wait.

Commit on `task/265-FF`: one commit per numbered item is preferred (target-state subjects, full
words, no em-dashes), each ending with `Co-Authored-By: Claude <noreply@anthropic.com>`.

REPORT FILE: `WORKSPACE\task-FF-report.md`: per item, what changed (file:line), the RED command and
output where applicable, the deleted-identifier grep counts, the whole-suite and aislop results, and
the output of `git -C C:\Users\mtsch\MFTLib status --short`.

Reply with ONLY (under 12 lines): Status, commits, one-line test summary, anything left undone,
report path.
