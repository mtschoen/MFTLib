# MFTLib - Test Report

2026-09-08T20:16:10-07:00

| Field | Value |
| --- | --- |
| Status | PASS for Task 20 Windows coverage and zero findings in branch-touched files; whole-repository CI remains below 100 |
| Mode | best-effort, scoped by the Task 20 controller rulings |
| Tests | 1062 total: 1059 passed, 0 failed, 3 skipped |
| Git | feat/index-mft-producer, 32d2478 plus Task 20 ownership and style fixes |
| Coverage | MFTLib: 96.92% line, 92.63% branch, 99.3% method. All managed modules: 4232/4354 lines, 122 uncovered; 1370/1471 branches; 738/743 methods. No exclusions added. |
| Lint | aislop 0.16.0: 99/100, 0 errors, 2 inherited warnings, 7 inherited informational findings, 0 formatting findings; 0 findings in files touched since 1c58b2d |
| Baseline | Restored throwaway worktree at 1c58b2d: 88/100, 0 errors, 32 warnings, 11 informational findings. Initial feature tip: 89/100, 34 warnings, 7 informational findings. |
| Analyzer execution | Roslynator solution loading failed; fallback ran for all 5 restored managed projects. TestExtensions reported 0 diagnostics without producing a report. |
| Native binary | Rebuilt Release/x64 with MSBuild; test-output DLL LastWriteTime 2026-09-08T20:09:04.3966167-07:00 |
| Linux | Coverage and native smoke test not run locally; controller verification pending |

## Commands

```powershell
dotnet test MFTLib.Tests/MFTLib.Tests.csproj -c Release -p:Platform=x64 --filter "TestCategory!=RequiresAdmin"
pwsh -NoProfile -File scripts/run-coverage.ps1 -NonInteractive
aislop scan . --json
aislop ci .
aislop scan --staged --json
```

## Remaining gate limitations

`aislop scan` and `aislop ci` return 1 at 99/100. The controller explicitly leaves findings in untouched files out of scope: AsyncFixer02 in `MFTLib.Tests/Index/BlockValidationMatrixTests.cs:158`, IDISP001 in `MFTLib.Tests/MftVolumeAdminTests.cs:401`, and seven informational punctuation findings in `scripts/dump-volume.sh` and `scripts/run-coverage.ps1`. No rule configuration or suppressions changed. Format, code-quality, and security engines reported zero findings; lint reported two and ai-slop reported seven.

The coverage script prints the inherited NU1503 warning when `dotnet restore` skips the native vcxproj. The native MSBuild rebuild and all five managed builds succeeded without compiler warnings. Expected native failure-path diagnostics appear in the test log. No UAC prompt occurred. The Windows script does not print native coverage percentages; these remain unmeasured locally. The three skipped tests exercise Unix cache-directory permissions.

The progress-test fix checks native totals of 300 in the final transfer frame despite only three emitted rows. It no longer assumes that a coalesced intermediate parsing report reaches the pipe. Existing real named-section, broker, block-adoption, and enumeration-lifetime tests exercised the ownership cleanup changes.
