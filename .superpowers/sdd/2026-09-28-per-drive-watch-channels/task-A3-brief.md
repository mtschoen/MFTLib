### Task A3: Delete the scan session layer

**Files:**
- Delete: `MFTLib/Broker/Client/JournalBrokerScanSession.cs`, `.Rescan.cs`, `.Start.cs`, `.Watch.cs`, `MFTLib/Broker/Client/JournalBrokerSessionState.cs`, `MFTLibTestExtensions/ScanSessionTestHarness.cs`, `MFTLib.Tests/JournalBrokerScanSessionTests.cs` and its eight partials (`.BlockLifetime`, `.Connection`, `.CursorReplacement`, `.Rescan`, `.Start`, `.WarmStart`, `.Watch`, `.WatchTransitions`), `MFTLib.Tests/VolumeQueryScanSessionTests.cs`
- Modify: `MFTLib/Broker/Client/BrokerScanResult.cs` (drop the `JournalBrokerScanSession.LatestScan` cref sentence), `MFTLibTestExtensions/MFTLibTestExtensions.csproj` (comment lines 5 to 9 name the session; describe the assembly as the consumer test harness), `MFTLib.Tests/MftProducerEndToEndTests.cs` (delete the cases that construct a `JournalBrokerScanSession`; keep the rest; list the deleted method names in the commit message for C4)

- [ ] **Step 1:** Grep each of `JournalBrokerScanSession`, `JournalBrokerSessionState`, `ScanSessionTestHarness` separately across `MFTLib`, `MFTLibTestExtensions`, `MFTLib.Tests`, `TestProgram`, `Benchmark`; the only hits must be the files above.
- [ ] **Step 2:** Delete and edit. No new tests: this task removes a feature with no production caller (spec section 4).
- [ ] **Step 3: Verify** build, whole suite, `aislop scan .`. Docs that describe the session are rewritten in D2.
- [ ] **Step 4: Commit:** "Delete JournalBrokerScanSession and ScanSessionTestHarness".

**Gate:** green. **Depends on:** none.

