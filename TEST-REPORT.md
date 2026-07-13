MFTLib test report - 2026-07-13
===========================================

Status:   PASS
Mode:     maintain
Tests:    417 passed, 0 failed (383 non-admin + 34 elevated NTFS/USN)
Git:      feat/volume-broker at c4690c1 plus the coverage and correctness changes in this commit

Managed coverage (`scripts/run-coverage.ps1`, full attended run):
  Line:   1399/1399 (100%)
  Branch: 380/380 (100%)
  Method: 235/235 (100%)
  Modules: MFTLib 100%, TestProgram 100%, Benchmark 100%
  Exclusion annotations: 0

Native coverage (`scripts/native-coverage.ps1`):
  Line:   1275/1275 (100%)
  Branch: 100%
  Exclusion annotations: 0

Lint and quality:
  `aislop ci .` 0.13.1: score 100/100, 0 findings across 66 supported files
  Formatting: 0 findings
  Static analysis: 0 findings
  Code quality: 0 findings
  AI-slop: 0 findings
  Security: 0 findings
  Existing explicit suppressions: 10 C++ NOLINT lines, 3 C# warning/suppression lines
  Documented exceptions: 0

Release validation performed:
  - Full Release|x64 solution build
  - 383 non-admin tests with merged managed coverage
  - 34 elevated tests against real NTFS MFT and USN journal APIs
  - Native Debug|x64 instrumentation with 100% line and branch coverage
  - VolumeBroker named-pipe/MMF end-to-end tests, including broker death and live-watch teardown
  - Synthetic generator conversion and asynchronous write-failure regressions
  - Deep native path truncation, malformed attribute/extension, and USN short/zero-record cases
  - `aislop ci .` whole-repository quality gate

Remaining outward checks:
  - Gitea Windows and Linux PR jobs on the pushed branch
  - file-wizard broker smoke and MAUI single-UAC/live-watch validation against merged MFTLib main
  - git-wizard `--watch` smoke against merged MFTLib main
  - final package dry run before publishing 0.3.0

Commands:
  `.\scripts\run-coverage.ps1`
  `.\scripts\run-coverage.ps1 -NonInteractive`
  `.\scripts\native-coverage.ps1`
  `aislop ci .`
