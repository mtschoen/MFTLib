# MFTLib - Test Report

2026-09-09

| Field | Value |
| --- | --- |
| Status | PASS for Task 7b2 fix round 1 Windows tests and no-new-findings gate |
| Mode | Best effort |
| Git | f27d5a9 plus fix round 1, feat/index-watch-bridge, verified before commit |
| Tests | Full non-admin suite: 1167 total, 1164 passed, 0 failed, 3 skipped. Test duration 13 s; command wall time 16.4360983 s; exit 0. Focused suite: 71 passed, 0 failed, 0 skipped; test duration 473 ms; command wall time 2.6048414 s. |
| Timeout | Full suite wrapped in a 900-second process timeout; it completed normally. |
| Coverage | 4580/4687 lines (97.71%), 1481/1552 branches (95.42%). Every changed executable production line was covered. Previous baseline: 97.67% lines, 95.42% branches. |
| Coverage limits | Overall coverage is below 100%. The report covers managed assemblies; native coverage was not rerun. |
| Lint | aislop 0.16.0: 99/100, 0 errors, 2 inherited warnings, 7 inherited informational findings. Zero findings in changed files. Exit 1 reflects the inherited findings. |
| Native binary | Not rebuilt. Test-bin Release/x64 DLL SHA-256: 550D7539B83D54A1DB5B6B124962DC53847F3B61A89242986DA54B269999FA74. |
| Platform | Windows verified; Linux verification belongs to the controller. Three existing Unix permission tests skipped. |

## Regression evidence

The invalid-drive regression failed with Assert.IsTrue because the existing epoch's batch was dropped. The generation-validation regression failed with KeyNotFoundException for drive C after compensation removed an earlier arm. Both passed after the arm compensation fix.

DisposeAsync_CalledTwice_DoesNotThrow failed with ObjectDisposedException ("The CancellationTokenSource has been disposed.") when the disposal nulling was temporarily removed. It passed after restoring the existing nulling. Each regression passed individually and in both broader suites.

## Remaining diagnostics

- AsyncFixer02 in MFTLib.Tests/Index/BlockValidationMatrixTests.cs:170.
- IDISP001 in MFTLib.Tests/MftVolumeAdminTests.cs:401.
- Seven inherited punctuation findings in scripts/dump-volume.sh and scripts/run-coverage.ps1.
- Inherited NU1902 warning for Microsoft.Build.Tasks.Git 8.0.0. No package changes made.

Roslynator could not load the mixed solution and fell back to five managed projects. TestExtensions reported zero diagnostics without writing an analyzer report.

## Commands

```powershell
dotnet test MFTLib.Tests/MFTLib.Tests.csproj -c Release -p:Platform=x64 --filter "TestCategory!=RequiresAdmin" -p:CollectCoverage=true -p:CoverletOutputFormat=cobertura -p:CoverletOutput=../coverage/mftlib-tests-coverage.xml
aislop scan . --json
```

These figures come from the plan 2b verification run. The per-task working directory that held its full output, coverage XML and scan JSON was git-ignored scratch and no longer exists: the plan completed in #137 and its document was reaped in #148. Re-run the commands above to regenerate the artifacts rather than looking for the originals.
