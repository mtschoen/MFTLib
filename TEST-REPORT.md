MFTLib test report - 2026-06-21
===========================================

Status:   PASS
Mode:     close-the-gap (activated the C# aislop lint gate and drove it to 0 findings)
Tests:    264 total, all passing
Git:      c556d40 + working tree (branch feat/aislop-csharp-gate-v0.12.3)

Managed coverage: 100% line, 100% branch, 100% method, 100% full-method
  MFTLib      - 100% line, 100% branch, 100% method
  TestProgram - 100% line, 100% branch, 100% method
  Benchmark   - 100% line, 100% branch, 100% method
  Totals: 633/633 lines, 196/196 branches, 116/116 methods
  0 exclusion annotations

Native coverage: 100% line, 100% branch (unchanged - no native code touched this session)
  MFTLibNative - 100% line, 100% branch
  0 exclusion annotations

Lint: aislop v0.12.3 (C# fork): 0 findings, score 100/100, failBelow 100
  Engines: format, lint (roslynator + jb inspectcode), code-quality, ai-slop, security
  jb inspectcode scoped to the 4 C# projects via lint.csharp.jbProjects; the C++
    MFTLibNative tree keeps its own clang-tidy/cppcheck gate.
  1 per-case suppression: CA1711 on the public [Flags] enum MatchFlags (renaming
    the shipped public type would break consumers; the Flags suffix is conventional).
  CA1707 (underscores in identifiers) disabled for MFTLib.Tests via .editorconfig
    (test methods follow the Method_Scenario_Expected convention).
  0 documented exceptions

Changes this run:
  - Activated the aislop C# gate: .aislop/config.yml (failBelow 100, jbProjects
    scoping), MFTLib.Tests/.editorconfig (CA1707), .gitea/workflows/aislop.yml
    (Windows CI gate, pinned fork v0.12.3).
  - Cleared 56 findings across 18 files: StringComparison/IFormatProvider overloads,
    AccessTo(Disposed|Modified)Closure restructures, MemberHidesStatic (extracted
    DefaultElevationProvider), null-forgiving (extracted SystemInfo.WmiString with
    unit tests to keep the defensive branch covered), the MftRecord interop ctor
    grouped into a NativeStrings struct, and assorted CA rules.

Managed coverage command:
  .\scripts\run-coverage.ps1                  # full run with admin tests (UAC prompt)
  .\scripts\run-coverage.ps1 -NonInteractive  # skip admin tests (CI / headless)

Native coverage command:
  .\scripts\native-coverage.ps1

aislop gate:
  aislop ci .                                 # local (uses the pinned global binary)
  .gitea/workflows/aislop.yml                 # CI (Windows runner, fork v0.12.3)
