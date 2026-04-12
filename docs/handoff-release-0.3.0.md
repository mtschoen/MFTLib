# Handoff: MFTLib 0.3.0 Release

## Status

Code-complete. Version already set to 0.3.0 in `MFTLib/MFTLib.csproj`. All features implemented and tested. Waiting on real-world validation via file-wizard before publishing.

## Pre-release checklist

### 1. file-wizard end-to-end validation

Run file-wizard with the new MFTLib on a real machine:
- First run: full MFT scan, verify USN cursor is saved in cache
- Make some filesystem changes (create, delete, rename files)
- Second run: verify cache loads, USN delta is applied (not full rescan), changes appear in index
- Check the MAUI UI shows correct file listings after delta application

file-wizard review findings were all addressed (see `file-wizard/docs/reviews/2026-04-12-usn-integration-review.md` — can be deleted, findings were fixed). Key fixes:
- Bug 1: Per-drive journal fallback (don't nuke all drives)
- Bug 2: Always save cursor even on empty results

### 2. Update TEST-REPORT.md

Current report says 237 tests. Actual count is now **270 tests** (212 non-admin, 58 admin-required). Run the full coverage suite and update:

```powershell
.\scripts\run-coverage.ps1                  # managed coverage (non-admin)
.\scripts\run-coverage.ps1 -NonInteractive  # or this for CI
```

### 3. Push and dry run

```bash
git push --force-with-lease  # still have the earlier force-push history
```

```powershell
.\scripts\release.ps1  # dry run: build, test, pack NuGet
```

The release script runs coverage internally, so it validates the 100% managed gate.

### 4. Publish

```powershell
.\scripts\release.ps1 -Publish  # tag v0.3.0, push to NuGet, create GitHub release
```

This requires:
- `C:\Users\mtsch\nugetkey` file with the NuGet API key
- `CHANGELOG.md` in repo root (the release script passes it to `gh release create --notes-file`). **This file doesn't exist yet** — either create it or modify the release script to use inline notes.
- `gh` CLI authenticated for GitHub release creation

## What's in 0.3.0

### New features
- **USN journal batch read:** `MftVolume.QueryUsnJournal()` + `ReadUsnJournal(cursor)` for incremental change detection since last scan
- **USN journal live watch:** `MftVolume.WatchUsnJournal(cursor, cancellationToken)` — `IAsyncEnumerable` that yields batches as filesystem changes arrive, zero CPU when idle
- **FileAttributes from $STANDARD_INFORMATION:** Accurate cloud placeholder flags (`Offline`, `RecallOnDataAccess`, `RecallOnOpen`) that were invisible from `$FILE_NAME`

### Improvements
- Native C++ split into `core/`, `mft/`, `usn/` folder structure
- Benchmark error handling (per-iteration catch, no crash on failure)
- Native DLL copy fixed (was targeting `net8.0-windows` instead of `net8.0`)
- Test struct sizes use authoritative `MftResult` constants instead of magic numbers
- 48-bit segment index encoding documented on `RecordNumber`/`ParentRecordNumber`
- Comparison benchmarks (BenchCompare, BenchCompareLive) extracted to separate repo at `C:\Users\mtsch\source\repos\MFTBenchCompare`

### Coverage
- Managed: 100% line, 100% branch, 100% method
- Native: 98.8% line, 100% branch (12 lines of unreachable overlapped I/O code)

### Test count
- 270 total (212 non-admin, 58 admin-required)

## Unpushed commits

```
5e5cd3c Add native USN journal test hooks and coverage tests
279612d Restore 100% managed test coverage for all projects
d322d28 Split dllmain.cpp into core/, mft/, usn/ folders with logical file structure
a1a10bd Remove completed plan documents
f25b649 Bump version to 0.3.0 for USN journal release
9e52bb0 Document 48-bit segment index encoding on RecordNumber and ParentRecordNumber
b4a1d48 Add InternalsVisibleTo for FileWizardTests
253f6c7 Add unit tests for WatchUsnJournal IAsyncEnumerable API
b5728f2 Add live integration test for WatchUsnJournal
caf0d0e Add MftVolume.WatchUsnJournal IAsyncEnumerable API with CancelIoEx cancellation
63a77d4 Add native WatchUsnJournalBatch and CancelUsnJournalWatch exports
eaff627 Add live USN journal integration tests (admin-required)
60eeab4 Add USN journal unit tests with mocked native calls
061180e Wire USN journal P/Invoke and add MftVolume.QueryUsnJournal/ReadUsnJournal API
3bb7631 Add managed USN journal types: UsnReason, UsnJournalEntry, UsnJournalCursor, interop structs
a144b98 Add native USN journal structs, QueryUsnJournal and ReadUsnJournal exports
29941e9 Fix native DLL copy for Benchmark and add error handling to benchmark runner
688bfe5 Fix test struct sizes and update FileAttributes source comments
7022700 Read FileAttributes from $STANDARD_INFORMATION instead of $FILE_NAME
```

## Known issues

- `CHANGELOG.md` doesn't exist — release script will fail at `gh release create --notes-file`
- Native coverage has 12 unreachable lines (see `docs/handoff-native-coverage-100.md`)
- file-wizard is still using a local project reference to MFTLib, not the NuGet package — switch after publishing
