# V1 Windows merged-head gate report

Worktree: C:\Users\mtsch\MFTLib-worktrees\impl-265, branch impl/265-per-drive-channels, HEAD 66bdb6d406057238a74e41c23da3617b1cc5f1c4 (verified, tree clean at start).
Lock id eb6a7f01-bc7e-4f07-bec6-1aae5980cde2 (acquired, released last).

| Step | Command | Exit | Result |
| --- | --- | --- | --- |
| 1 | .\init.ps1 -Build | 0 | PASS. Build succeeded, 0 warnings, 0 errors. Log line 46 has one non-fatal error from the optional settings-provisioning tool (could not replace a locked inference-manager.exe in the schoen-lab venv); init still reported Done. |
| 2 | MSBuild (amd64, via vswhere) MFTLibNative.vcxproj Debug x64 | 0 | PASS |
| 3 | .\scripts\run-coverage.ps1 -NonInteractive | 0 | PASS. Total 1972, Passed 1966, Skipped 6, Failed 0. Line coverage 99.3% (6915 of 6963 lines). |
| 4 | nscov.py per namespace | 0 | See below |
| 5 | pwsh scripts/test-coverage-status.ps1 | 0 | PASS. "Coverage status regression tests passed." The ::error lines are the script's own negative-case output; baseline 97.2 validated. |
| 6 | .\scripts\native-coverage.ps1 | 0 | PASS. MFTLibNative 97.8% line, 100% branch. No elevation problem. |
| 7 | aislop scan . / aislop ci . | scan not captured; ci 1 | Score 99/100, 0 errors, 5 warnings, 0 fixable. ci exits 1 because failBelow is 100. Exactly the expected five, nothing extra. |
| 8 | git status | - | Worktree empty; primary empty and on main. |

## Skipped tests (6, non-Windows)
EnsureCreated_OnUnixLeavesOwnerOnlyPermissions, EnsureCreated_OnUnixNarrowsAnAlreadyWideDirectory, EnsureCreated_OnUnixWidensAnUnderpermissionedDirectory, Open_OnLinuxWithNoOverride_ThrowsPlatformNotSupported, Find_OnLinux_LeavesBackslashesInsideFileNamesUntouched, Query_NonWindowsHost_ThrowsPlatformNotSupported.

## Per-namespace coverage (covered / total lines)
| Namespace | Covered | Total | Percent |
| --- | --- | --- | --- |
| MFTLib (flat) | 2774 | 2774 | 100.0 |
| MFTLib.Index | 3401 | 3410 | 99.74 |
| TestProgram | 58 | 58 | 100.0 |
| Program (TestProgram/Benchmark entry) | 4 | 4 | 100.0 |
| Benchmark | 547 | 554 | 98.74 |
| MFTLibTestExtensions | 131 | 163 | 80.37 |

Uncovered MFTLib: none.
Uncovered MFTLib.Index (9, exactly the expected non-Windows set): BlockFile.Flush.cs:71, 72, 93, 94; CacheDirectory.cs:335, 337, 340, 342, 345.
Also uncovered outside the requested gate namespaces:
- Benchmark BenchmarkRunner.cs:147, 155, 156, 157, 158, 159, 170.
- MFTLibTestExtensions (not a publisher-checked namespace): DelegatingStream.cs 8, 9, 10, 11, 15, 16, 19, 23, 28, 33, 35; InMemoryDuplexStream.cs 12, 16, 17, 18, 28, 29, 41, 43, 49, 50, 51; InMemoryBrokerPipes.cs 28, 45, 56, 57, 58; BrokerTestHarness.cs 23, 34, 72; HostPipeEnds.cs 33, 35.
Note: Benchmark 98.74% and MFTLibTestExtensions 80.37% were not stated in the expectations; the gate only requires nonzero coverage there. Whether the Benchmark gap predates this branch was not checked.

## aislop warnings
- MFTLib.Tests/NativeSeamIsolationFixtures.cs:73 dotnet/AsyncFixer01
- MFTLib.Tests/NativeSeamIsolationFixtures.cs:79 dotnet/AsyncFixer01
- MFTLib/Index/CachedBlockDeletionOutcome.cs:8 ai-slop/csharp-redundant-doc-comment
- MFTLib/Index/CachedBlockDeletionOutcome.cs:10 ai-slop/csharp-redundant-doc-comment
- MFTLib/Broker/Host/JournalBrokerHost.cs:46 complexity/too-many-params (8 params)

Logs: .superpowers\step1.log, step2.log, step3.log, step5.log, step6.log, step7.log, step7ci.log (git-ignored).
