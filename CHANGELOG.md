# Changelog

## 0.2.0

### Breaking Changes

- Removed `MFTParse` class (use `MftVolume.ReadAllRecords()` or `MftVolume.FindByName()` instead)
- Removed `MftFileEntry` interop struct (internal; replaced by `MftRecord`)
- `FileUtilities` is now a `static class`

### Improvements

- Swappable native function indirection (`MFTLibNative`) for testability
- Swappable dependencies in `ElevationUtilities` for testability
- Extracted `MftVolume.ExtractDriveLetter()` helper
- Replaced magic numbers in `MftResult` with named constants (`NativeEntrySize`, `NativePathEntrySize`)
- Fixed path resolution when filter is null

### Tests

- Expanded test suite from 16 to 60+ tests
- Achieved 100% line, branch, method, and full-method coverage
- Added admin-elevated test suite (`MftVolumeAdminTests`)
- Added coverage for `ElevationUtilities`, `MftResult`, `MftRecord`, path resolution, native mock indirection
- Added combined coverage script for admin + non-admin tests
- Tagged interactive UAC tests with `TestCategory("Interactive")`

## 0.1.2

- Initial public release
- MFT parsing with native C++ core
- Path resolution via `MftPathUtilities`
- `MftVolume.ReadAllRecords()` and `FindByName()` APIs
- Filename filtering (exact match or contains, case-insensitive)
- Configurable buffer sizes
- Self-elevation via `ElevationUtilities`
