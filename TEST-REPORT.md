MFTLib test report — 2026-04-05
═══════════════════════════════════════════

Status:   PASS
Tests:    157 passed, 30 skipped (admin-only), 187 total
Git:      4f8833d (main, with uncommitted changes)
Coverage: 100% line, 100% branch, 100% method
          0 lines uncovered
          0 exclusion annotations
          0 coverlet exclude filters

Per-module breakdown:
  MFTLib      — 100% line, 100% branch, 100% method
  TestProgram — 100% line, 100% branch, 100% method
  Benchmark   — 100% line, 100% branch, 100% method

Coverage command:
  MSBuild.exe MFTLib.sln -p:Configuration=Release -p:Platform=x64 -v:quiet
  dotnet test MFTLib.Tests/MFTLib.Tests.csproj -p:Platform=x64 -p:CollectCoverage=true -p:CoverletOutputFormat=cobertura -p:CoverletOutput=./coverage.xml '-p:Include="[MFTLib]*,[TestProgram]*,[Benchmark]*"' --no-build -c Release
