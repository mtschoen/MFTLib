MFTLib test report — 2026-05-26
===========================================

Status:   PASS
Tests:    287 total (229 non-admin + 58 admin), all passing
Git:      f9a7b15 + working tree (branch feat/0.3-elevation-provider)

Managed coverage: 100% line, 100% branch, 100% method
  MFTLib      — 100% line, 100% branch, 100% method (626/626 lines, 196/196 branches, 114/114 methods)
  TestProgram — 100% line, 100% branch, 100% method
  Benchmark   — 100% line, 100% branch, 100% method
  0 exclusion annotations

Native coverage: 100% line, 100% branch (unchanged — no native code touched this session)
  MFTLibNative — 100% line, 100% branch
  0 exclusion annotations

Changes covered this run:
  - public IElevationProvider + ElevationUtilities.DefaultProvider (delegation via Func seams)
  - UsnJournalEntry.Create factory (added 9ed4154, previously uncovered)
  - MftVolume.WatchUsnJournalWithCursor overload (added 01e8801, previously uncovered)

Managed coverage command:
  .\scripts\run-coverage.ps1                  # full run with admin tests
  .\scripts\run-coverage.ps1 -NonInteractive  # skip admin tests (CI / headless)

Native coverage command:
  .\scripts\native-coverage.ps1
