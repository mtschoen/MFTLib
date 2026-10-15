# MFTLib - Test Report

2026-10-14

| Field | Value |
| --- | --- |
| Status | Windows unelevated tests PASS; full aislop scan PASS |
| Mode | best-effort |
| Git | refactor/producer-kind-one-enum; base 184b1f0e822b13af12be8659472daa1c2b4c4d38 with working-tree changes |
| Tests | 3250 total: 3241 passed, 0 failed, 9 skipped |
| Coverage | Not collected in this lane; no coverage percentage or baseline comparison asserted |
| Lint | aislop 0.16.0: 100/100, 0 errors, 2 pre-existing fixture warnings, 0 fixable; formatting, code quality, AI slop and security: 0 findings |
| Build | Native and managed Release x64 passed; managed builds: 0 warnings, 0 errors; native restore NU1503 warning |
| Exclusions | RequiresAdmin excluded; 9 platform-specific cases skipped; no exclusions or suppressions added |
| Platform | Windows unelevated; elevated, Linux and native coverage not run |

## Lint

| Engine | Findings | Location |
| --- | --- | --- |
| Formatting | 0 | None |
| Lint | 2 pre-existing | MFTLib.Tests/CallerGateFixtureConstructors.cs:35 (jb/RedundantExplicitPositionalPropertyDeclaration); MFTLib.Tests/CallerGateFixtureRecord.cs:50 (jb/EmptyConstructor) |
| Code quality | 0 | None |
| AI slop | 0 | None |
| Security | 0 | None |

## Commands

```powershell
.\scripts\build-windows.ps1
dotnet test MFTLib.Tests/MFTLib.Tests.csproj -c Release -p:Platform=x64 --no-build --filter "TestCategory!=RequiresAdmin"
.\Benchmark\bin\x64\Release\net10.0\Benchmark.exe index --synthetic 16 --iterations 1
aislop scan .
aislop scan --staged
```
