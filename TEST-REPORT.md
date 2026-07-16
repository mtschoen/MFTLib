MFTLib test report - 2026-07-16
===========================================

Status:   PASS (Linux CI-equivalent verification)
Tests:    271 managed passed, 34 platform tests skipped; 8 native smoke tests passed
Git:      feat/scan-to-watch-session-api at 54486b3 plus the changes in this commit

Managed coverage (`bash scripts/coverage-linux.sh`):
  Line:   1736/1782 (97.41%)
  Branch: 382/391 (97.69%)
  Method: 97.76%
  Touched broker client/session classes: 100% line and branch coverage
  Exclusion annotations added by this change: 0

Native coverage (`bash scripts/coverage-linux.sh`):
  Line:   547/868 (63.0%)
  Branch: 242/641 (37.8%)
  Functions: 56/84 (66.7%)
  Exclusion annotations added by this change: 0

The Linux script intentionally omits Windows-only named-pipe, named-MMF, raw-volume,
and elevated tests. Those platform exclusions account for the managed gap; the native
Linux smoke suite is a portability gate rather than the repository's full Windows
native coverage suite. The last attended Windows report (2026-07-13) recorded 100%
managed and native coverage. This worktree cannot run MSBuild/UAC verification, so the
Windows CI jobs remain the authoritative current check for those paths.

Quality:
  - `git diff --check`: clean
  - Focused concurrency/cancellation regressions: 20 repeated passes
  - `aislop scan .` on Linux: 0 lint/security errors; the repository-wide CRLF
    `.editorconfig` rule flags all 79 tracked C# files because this Linux checkout stores
    them with LF. The same finding is present on untouched files and is resolved by the
    Windows CI checkout used by the authoritative aislop gate.

Commands:
  `bash scripts/coverage-linux.sh`
  `aislop scan .`
  `aislop ci .`
