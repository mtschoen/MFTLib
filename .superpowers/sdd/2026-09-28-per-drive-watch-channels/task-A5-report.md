# Task A5 report: DONE_WITH_CONCERNS

Commit: "Delete the broker watch source" (branch task/265-A5, base e65902cf).

## Implemented
- Deleted BrokerIndexWatchSource.cs/.PerDrive.cs/.AbandonedStart.cs and the 11 test files in the brief.
- Removed BrokerMftBlockProducer.CreateWatchSource (_connectAsync still used by ProduceAsync).
- Additions to the brief's file list, per team-lead ruling (dangling cref would break the gate): reworded XML comments in
  MFTLib/Broker/Client/LiveWatchItem.cs and MFTLib/Broker/Client/JournalBrokerClient.LiveWatch.cs. Comment text only.

## Evidence (no new tests, so no RED/GREEN)
- Grep before: 19 files (64 hits). After (excluding docs/.superpowers): only IIndexWatchSource.cs, FileIndex.Watch.cs
  (B1 rewrites), plus docs/AGENTS.md/README/CHANGELOG (D1-D3).
- Build: `dotnet build MFTLib.Tests -c Release -p:Platform=x64 --no-incremental`: 0 warnings, 0 errors. `init.ps1 -Build` also succeeded.
  (Solution-level `dotnet build` cannot load the vcxproj; init.ps1 -Build is the working path.)
- Whole suite `run-coverage.ps1 -NonInteractive`: 1761 total, 1755 passed, 6 skipped, 0 failed; line coverage 98.2%.
- `aislop scan .`: 98/100, 0 errors, 9 warnings, exit 1 (gate needs 100). Breakdown:
  - 3 InvalidXmlDocComment: FileIndex.Watch.cs:41,62 and IIndexWatchSource.cs:42, the expected crefs B1 rewrites.
  - 2 test-support items orphaned by this deletion (caused by A5): `GateFrameWriteStream.ForwardedFrameKinds`
    (only read by deleted BrokerWatchStartSendCancellationTests) and `InProcessBlockBrokerHarness` `WrapClientTransport`
    setter (only set by the same file). C6 ports that test and needs both; left in place, not in A5's file list.
  - 4 pre-existing, unrelated: NativeSeamIsolationFixtures.cs:73,79 (AsyncFixer01), CachedBlockDeletionOutcome.cs:8,10 (redundant doc).
    Not verified against base; the files are untouched by A5.

## Concerns
- aislop gate is not 100 at this commit for the reasons above; decide whether the two orphaned test-support members are
  deleted here (C6 restores from c1d43784) or left for C6.
- Commit was first made with only the staged deletions and amended to include the three edited files.

## Fix section (team-lead rulings)
New commit 405ead9 "Remove references and test support orphaned by the broker watch source deletion" (first commit is now 8906157).
- Reworded the 3 dangling crefs (FileIndex.Watch.cs x2, IIndexWatchSource.cs), comment text only.
- Removed `GateFrameWriteStream.ForwardedFrameKinds` (plus its Enqueue and the now-unused `using System.Collections.Concurrent`)
  and `InProcessBlockBrokerHarness.Options.WrapClientTransport` (harness passes clientTransport directly). No further orphans.
- Line endings: GateFrameWriteStream.cs had been converted to LF by my edit; restored CRLF (diff is pure deletions).
- Build `dotnet build MFTLib.Tests -c Release -p:Platform=x64 --no-incremental`: 0 warnings, 0 errors.
- Whole suite (run-coverage.ps1 -NonInteractive, before the CRLF restore, a whitespace-only change): 1761 total, 1755 passed, 6 skipped, 0 failed; line coverage 98.2%.
- `aislop scan .` after final commit: 99/100, 0 errors, 4 warnings, 0 fixable. Remaining (untouched, per ruling 3):
  - dotnet/AsyncFixer01 (Unnecessary async/await): MFTLib.Tests/NativeSeamIsolationFixtures.cs:73:5 and :79:5
  - ai-slop/csharp-redundant-doc-comment: MFTLib/Index/CachedBlockDeletionOutcome.cs:8:1 and :10:1
