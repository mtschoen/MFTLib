# MFTLib - Test Report

2026-09-16

| Field | Value |
| --- | --- |
| Status | PASS: requested Windows build, test/coverage command, and aislop CI gate |
| Mode | best-effort: existing global coverage gaps retained; no tests or gates weakened |
| Git | agent/45-index-no-way-to-await-the-initia, CI fixes based on 2d14a78e9614456cac82524f42492563c85d1c68 |
| Tests | 1457 total: 1451 passed, 0 failed, 6 platform skips; admin tests excluded by the existing NonInteractive command |
| Coverage | 5931/6056 lines (97.93%), 125 uncovered; 2007/2126 branches (94.36%); 1018/1022 methods (99.62%) |
| Lint | aislop 0.16.0: score 100/100, 0 errors, 0 unsuppressed warnings; 4 findings covered by existing aislop-ignore directives, 0 new suppressions; all 5 enabled engines ran |
| Native | Release x64 build passed using vswhere-resolved amd64 MSBuild and PlatformToolset=v143 |
| Platform | Windows; Linux and elevated live-volume tests were not run |

## Commands

```powershell
$installation = & "${env:ProgramFiles(x86)}\Microsoft Visual Studio\Installer\vswhere.exe" -products '*' -requires Microsoft.Component.MSBuild -property installationPath -latest
$nativeBuild = Join-Path $installation 'MSBuild/Current/Bin/amd64/MSBuild.exe'
& $nativeBuild MFTLibNative/MFTLibNative.vcxproj -t:Build -p:Configuration=Release -p:Platform=x64 -p:PlatformToolset=v143 "-p:SolutionDir=$pwd\" -v:q -nologo
pwsh -File scripts/run-coverage.ps1 -NonInteractive
aislop ci .
aislop scan .
git diff --check
```
