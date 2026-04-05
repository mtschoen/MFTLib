MFTLib test report — 2026-04-05
═══════════════════════════════════════════

Status:   PASS
Tests:    218 total (185 non-admin + 33 admin), all passing
Git:      db030e8 (main, with uncommitted changes)

Managed coverage: 100% line, 100% branch, 100% method
  MFTLib      — 100% line, 100% branch, 100% method
  TestProgram — 100% line, 100% branch, 100% method
  Benchmark   — 100% line, 100% branch, 100% method
  0 exclusion annotations

Native coverage: 100% line, 100% branch
  MFTLibNative — 100% line, 100% branch
  0 exclusion annotations

Managed coverage command:
  .\scripts\run-coverage.ps1                  # full run with admin tests
  .\scripts\run-coverage.ps1 -NonInteractive  # skip admin tests (CI / headless)

Native coverage command:
  .\.claude\scripts\native-coverage.ps1
