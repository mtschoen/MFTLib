### Finding Verdicts

1. ADDRESSED. `ScanDriveAsync` now creates a per-scan `lifetimeReleased` flag (`MFTLib/Broker/Client/BrokerProcess.Scan.cs:49`), calls the same release-once path immediately after `ScanReady` (`MFTLib/Broker/Client/BrokerProcess.Scan.cs:58-62`), and calls it again from the failure path before disposing the block in a `finally` (`MFTLib/Broker/Client/BrokerProcess.Scan.cs:70-81`). `ReleaseOnce` returns when ownership has already been released and, critically, sets the flag before invoking `Dispose` (`MFTLib/Broker/Client/BrokerProcess.Scan.cs:85-95`). Therefore a throwing `Dispose` at `ScanReady` is not retried by the catch, while a failure before `ScanReady` releases the lifetime there; the `finally` invokes block disposal in either case.

   The new regression test installs a lifetime whose `Dispose` increments its count and throws (`MFTLib.Tests/BrokerProcessTests.BlockSections.cs:192-215`), then asserts the original exception, exactly one lifetime-dispose attempt, and a disposed block (`MFTLib.Tests/BrokerProcessTests.BlockSections.cs:176-189`). A fresh focused run covering that test and the ported disconnection, repeated-ready, and error-after-ready cases passed: `dotnet test MFTLib.Tests\MFTLib.Tests.csproj -c Release -p:Platform=x64 --no-build --filter "FullyQualifiedName~Produce_DisconnectionDisposesBlockBeforeReturning|FullyQualifiedName~Produce_RejectsBrokenExchangeAndDisposesBlock|FullyQualifiedName~ScanDrive_LifetimeDisposeThrowingAfterScanReady_IsNotRetriedAndBlockIsReleased"` reported 7 passed, 0 failed, 0 skipped.

2. Important - NOT ADDRESSED. There is no W40-R1 RED evidence for `ScanDrive_LifetimeDisposeThrowingAfterScanReady_IsNotRetriedAndBlockIsReleased`. The fix-round report gives the implementation claim and green runs only (`.superpowers/sdd/2026-09-28-per-drive-watch-channels/task-C4-report.md:119-123`); it does not provide an exact pre-fix or scratch-mutation command and failing output as required by W40-R1 (`.superpowers/sdd/2026-09-28-per-drive-watch-channels/orchestrator-rulings.md:30`). Code inspection shows why the pre-fix implementation would have failed the new assertions: its first throwing dispose would enter the catch, the catch would retry the lifetime dispose, and the second throw would prevent block disposal. That reasoning is not the recorded failing-run evidence the ruling requires.

### New Breakage in the Fix Diff

None.

### Out-of-Scope Observations

None.

### Verdict

Findings remain open

- Important: W40-R1 RED evidence is missing for the new throwing-Dispose regression test.
