# Task W4fix report: BrokerMftBlockProducerTests failure after merging C4 and C5

Worktree `C:\Users\mtsch\MFTLib-worktrees\265-W4fix`, branch `task/265-W4fix`, base `23709f6` (confirmed).
Commit: `1705380` "Scan catch-up fails loudly when a read returns entries without advancing its cursor".

## Cause

The failing assertion was not the section lifetime. `MFTLib.Tests/BrokerMftBlockProducerTests.cs:45` is
`Assert.AreEqual(1, completed.CatchUpEntries.Count);`, which observed 2. The lifetime assertions (lines 36 and 48) hold,
and the section lifetime is released exactly once.

The test's fake catch-up source (`BrokerMftBlockProducerTests.cs:13-14` at `23709f6`) was
`(_, _, _) => ([entry], AdvancedCursor)`: it returned the same entry and the same cursor on every call. C4 ported it
against the old single-call source. C5 made catch-up a loop of bounded reads (`JournalBrokerHost.Scan.cs`, `CatchUp`,
which reads until a call returns its cursor unchanged). Call 1, from the armed cursor, returned the entry and advanced
to `AdvancedCursor`. Call 2, from `AdvancedCursor`, returned the same entry with the cursor unchanged. The loop appended
that chunk before checking for the tip (`JournalBrokerHost.Scan.cs:260-261` at `23709f6`), so the terminal
`JournalBatch` carried the entry twice.

Which side is wrong: the fake. A real bounded read at the tip returns no entries. The native loop breaks when the
buffer's next USN equals the start (`MFTLibNative/usn/usn_journal.cpp:279-281`) before copying any record. The host was
still too trusting, though: a source that broke that rule silently duplicated entries.
`MftProducerEndToEndTests.cs:34` had the same fake. It passed only because applying the same journal entries twice to
the index is idempotent.

## Fix

- Production (`MFTLib/Broker/Host/JournalBrokerHost.Scan.cs`, `CatchUp`): a call that returns its cursor unchanged ends
  catch-up only if it returned no entries. Entries without an advance throw `InvalidOperationException` ("catch-up read
  returned N entries without advancing its cursor J:U"). That goes through the existing failed-catch-up path, the
  journal check, to `Error`/`CatchUpLost`, instead of shipping duplicates. `UsnJournalCatchUpSource`'s doc now states
  that a call at the tip returns no entries.
- Tests: `BrokerMftBlockProducerTests.Produce_AdoptsClientBlockAndReleasesOnlySectionLifetime` and
  `MftProducerEndToEndTests.OpenAsync_AdoptsBrokerBlockAndAppliesCatchUpAtRecordNumbers` now use
  `CatchUpSources.ToTip(AdvancedCursor, ...)`, a fake that honors the bounded contract. No assertion changed.
- New regression test `JournalBrokerHostChannelTests.ScanChannel_CatchUpReturnsEntriesWithoutAdvancing_WritesErrorAndNoDuplicateBatch`.

## Evidence

RED, the reported test at `23709f6`, after `init.ps1 -Build`:
```
dotnet test MFTLib.Tests/MFTLib.Tests.csproj -c Release -p:Platform=x64 --no-build --filter "FullyQualifiedName~BrokerMftBlockProducerTests"
  Failed Produce_AdoptsClientBlockAndReleasesOnlySectionLifetime [97 ms]
  Error Message:
   Assert.AreEqual failed. Expected:<1>. Actual:<2>.
     at MFTLib.Tests.BrokerMftBlockProducerTests.Produce_AdoptsClientBlockAndReleasesOnlySectionLifetime() in ...\MFTLib.Tests\BrokerMftBlockProducerTests.cs:line 45
Failed!  - Failed:     1, Passed:    12, Skipped:     0, Total:    13
```

RED, the new guard test, before the production change:
```
dotnet test MFTLib.Tests/MFTLib.Tests.csproj -c Release -p:Platform=x64 --no-build --filter "FullyQualifiedName~JournalBrokerHostChannelTests.ScanChannel_CatchUpReturnsEntriesWithoutAdvancing_WritesErrorAndNoDuplicateBatch"
  Failed ScanChannel_CatchUpReturnsEntriesWithoutAdvancing_WritesErrorAndNoDuplicateBatch [44 ms]
  Error Message:
   Assert.AreEqual failed. Expected:<Error>. Actual:<JournalBatch>.
Failed!  - Failed:     1, Passed:     0, Skipped:     0, Total:     1
```

GREEN:
```
dotnet test ... --no-build --filter "FullyQualifiedName~BrokerMftBlockProducerTests|FullyQualifiedName~JournalBrokerHostChannelTests.ScanChannel_CatchUpReturnsEntriesWithoutAdvancing_WritesErrorAndNoDuplicateBatch"
Passed!  - Failed:     0, Passed:    14, Skipped:     0, Total:    14
```

Broker classes (`JournalBrokerHost`, `BrokerProcess`, `BrokerProtocol`, `BrokerMftBlockProducer`, `MftProducerEndToEnd`,
`GrowUsnJournalHost`, `VolumeQueryHost`, `BrokerFrameLength`, `BrokerIndexWatch`, `NativeSeamIsolation` name filters):
`Passed!  - Failed: 0, Passed: 330, Skipped: 0, Total: 330`.

Whole suite, `.\scripts\run-coverage.ps1 -NonInteractive`: `Total tests: 1716`, `Passed: 1710`, `Skipped: 6`, Failed 0
(no Failed line), exit 0.

aislop `scan . -d`: `99 / 100 Healthy 0 errors · 5 warnings`, the gate unchanged: `NativeSeamIsolationFixtures.cs:73`, `:79`,
`CachedBlockDeletionOutcome.cs:8`, `:10`, and the ruled 8-parameter `JournalBrokerHost` constructor.

## Primary checkout

`git -C C:\Users\mtsch\MFTLib status --short`: (empty output)
