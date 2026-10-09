# MFTLib - Test Report

2026-10-09

| Field | Value |
| --- | --- |
| Status | PASS for nullable scan retention slice; platform CI pending |
| Mode | best-effort; changed production files fully line-covered |
| Git | feature/l3-scan-retention, tested working tree based on [journal-surface cleanup commit af4f106](https://gitea.fleet.sticktoitive.net/schoen/MFTLib/commit/af4f10609ea8e2a352f8278ffde3dff3b32b6eed) |
| Tests | Whole suite: 2771 total, 2744 passed, 0 failed, 27 skipped |
| Targeted tests | 512 passed, 0 failed, 0 skipped |
| Contract tests | 44 passed (surface, caller, instruction, seam isolation, namespace boundary) |
| Follow-up protocol tests | 81 passed after adding two malformed journal-count cases; whole suite not repeated |
| Coverage | Combined whole-suite and protocol follow-up: 8914/8986 lines (99.199%), 72 uncovered; 3546/3670 branches (96.621%) |
| Changed production files | No uncovered executable lines in combined reports |
| Test extensions | Whole-suite line, branch and method coverage: 100% |
| Lint | aislop 0.16.0: score 100/100, 0 errors, 2 pre-existing warnings, 0 fixable; exit 0 |
| Build | Native and managed Release x64 passed; managed projects: 0 warnings, 0 errors; native restore NU1503 warning |
| Platform | Windows unelevated; Linux, Linux package and elevated live-volume CI pending; native instrumentation not run |

## Lint

| Engine | Findings | Location |
| --- | --- | --- |
| format | 0 | None |
| lint | 2 | Unchanged CallerGateFixtureConstructors.cs:35 (RedundantExplicitPositionalPropertyDeclaration), CallerGateFixtureRecord.cs:50 (EmptyConstructor) |
| code-quality | 0 | None |
| ai-slop | 0 | None |
| security | 0 | None |

## Coverage

| Assembly | Whole-suite line coverage | Branch coverage | Method coverage |
| --- | --- | --- | --- |
| MFTLib | 99.71% | 97.83% | 99.92% |
| MFTLibTestExtensions | 100% | 100% | 100% |
| SampleProgram.Direct | 97.73% | 96.19% | 97.77% |
| SampleProgram.Watch | 89.77% | 79.83% | 92.85% |
| Benchmark | 98.82% | 96.36% | 100% |

## Commands

```powershell
pwsh -NoProfile -File scripts/build-windows.ps1
dotnet test MFTLib.Tests/MFTLib.Tests.csproj -c Release -p:Platform=x64 --no-build --filter "FullyQualifiedName~PublicSurfaceTests|FullyQualifiedName~PublicMemberCallerTests|FullyQualifiedName~AgentInstructionsTests|FullyQualifiedName~NativeSeamIsolationTests|FullyQualifiedName~NamespaceBoundaryTests"
dotnet test MFTLib.Tests/MFTLib.Tests.csproj -c Release -p:Platform=x64 --no-build --filter "FullyQualifiedName~MftBlockRowWriter|FullyQualifiedName~MftProducerEndToEndTests|FullyQualifiedName~LocalMftBlockProducerTests|FullyQualifiedName~FreedRowsFixtureParityTests|FullyQualifiedName~JournalMutatorFreedRowTests|FullyQualifiedName~BrokerProtocolTests|FullyQualifiedName~BrokerFrameLengthTests|FullyQualifiedName~BrokerFrameWriteLimitTests|FullyQualifiedName~BrokerMftBlockProducerProtocolTests|FullyQualifiedName~BrokerProcessTests|FullyQualifiedName~BrokerSessionTests|FullyQualifiedName~JournalBrokerHostTests|FullyQualifiedName~JournalBrokerHostBlockScanTests|FullyQualifiedName~JournalBrokerHostChannelTests|FullyQualifiedName~WatchArgumentsTests|FullyQualifiedName~PublicMemberCallerTests|FullyQualifiedName~DefaultElevatedEntryRunnerTests"
$env:VSTEST_TESTHOST_SHUTDOWN_TIMEOUT = '60000'
dotnet test MFTLib.Tests/MFTLib.Tests.csproj -c Release -p:Platform=x64 --no-build -p:CollectCoverage=true -p:CoverletOutputFormat=cobertura -p:CoverletOutput=../.lane/coverage
dotnet test MFTLib.Tests/MFTLib.Tests.csproj -c Release -p:Platform=x64 --no-build --filter "FullyQualifiedName~BrokerProtocolTests" -p:CollectCoverage=true -p:CoverletOutputFormat=cobertura -p:CoverletOutput=../.lane/count-coverage
reportgenerator "-reports:.lane/coverage.cobertura.xml;.lane/count-coverage.cobertura.xml" "-targetdir:.lane/coverage-combined" "-reporttypes:MarkdownSummary;TextSummary;JsonSummary;Cobertura"
aislop ci .
```
