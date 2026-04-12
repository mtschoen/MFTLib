# Handoff: Native Coverage 100%

## Goal

Get MFTLibNative from 98.8% to 100% line coverage. Currently 100% branch.

## What's uncovered (12 lines)

All in `MFTLibNative/usn/usn_journal.cpp`, the `WatchUsnJournalBatch` function:

### 1. Overlapped I/O error paths (lines 222-229)

```cpp
if (error == ERROR_IO_PENDING) {
    ok = GetOverlappedResult(volumeHandle, &overlapped, &bytesReturned, TRUE);
    if (!ok) {
        error = GetLastError();
        if (error == ERROR_OPERATION_ABORTED) {  // CancelIoEx path
            CloseHandle(overlapped.hEvent);
            VirtualFree(readBuffer, 0, MEM_RELEASE);
            return result;
        }
    }
}
```

**Why unreachable:** The volume handle is opened via `Kernel32.CreateFile` without `FILE_FLAG_OVERLAPPED`. When you pass an `OVERLAPPED` struct to `DeviceIoControl` on a non-overlapped handle, Windows ignores it and completes synchronously. So `ERROR_IO_PENDING` is never returned, and the entire overlapped codepath is dead.

**Fix:** Open the volume handle with `FILE_FLAG_OVERLAPPED` when the caller intends to use `WatchUsnJournalBatch`. Options:
- Add a `FileUtilities.GetOverlappedVolumeHandle` that passes `FILE_FLAG_OVERLAPPED` to `CreateFile`
- Or add an `overlapped` bool parameter to `MftVolume.Open`
- Then the watch test `WatchUsnJournal_CancelDuringBlockingWait_ReturnsEmpty` (already exists in `NativeCoverageTests.cs`) would naturally hit the `ERROR_IO_PENDING` → `ERROR_OPERATION_ABORTED` path

**Risk:** Changing the handle to overlapped might affect `ReadUsnJournal` (non-blocking) behavior. Verify that `BytesToWaitFor = 0` still returns immediately with an overlapped handle. It should — `BytesToWaitFor = 0` means "don't wait."

### 2. ReadUsnJournal grow-failure error handler (lines 139-142)

```cpp
if (!grown) {
    swprintf_s(result->errorMessage, 256, L"Failed to grow entry array");
    VirtualFree(readBuffer, 0, MEM_RELEASE);
    result->nextUsn = nextUsn;
    return result;
}
```

**Why hard to hit:** The grow only triggers when >1024 entries are returned. The `ShouldFailAlloc` countdown would need to be set exactly right — skip the read buffer alloc, skip the initial entry alloc, let the first DeviceIoControl succeed and return >1024 entries, then fail on the grow. The test `ReadUsnJournal_AllocFailOnGrow_ReturnsError` exists but it's system-dependent (needs >1024 journal entries between USN 0 and current).

**Fix:** Either lower the initial capacity via a test hook (e.g. `SetUsnInitialCapacity(16)`) so the grow triggers with fewer entries, or accept this as an edge case that's covered by the identical pattern in the MFT parsing code.

### 3. test_hooks.cpp line 36

`ShouldFailUsnIo` returning `false`. This IS executed (every successful USN call), likely a coverage tool instrumentation artifact. Ignore.

## Files involved

- `MFTLibNative/usn/usn_journal.cpp` — the watch function
- `MFTLib/FileUtilities.cs` / `MFTLib/Kernel32.cs` — handle creation
- `MFTLib/MftVolume.cs` — may need overlapped handle variant
- `MFTLib.Tests/NativeCoverageTests.cs` — existing cancel test to update
- `scripts/native-coverage.ps1` — coverage runner
- `.claude/scripts/native-coverage-elevated.ps1` — elevated coverage helper (in .gitignore)

## Test infrastructure

Native coverage requires admin elevation and uses Microsoft.CodeCoverage.Console:
```powershell
.\scripts\native-coverage.ps1           # non-admin (misses USN paths)
.\.claude\scripts\native-coverage-elevated.ps1  # self-elevating (UAC prompt)
```

The elevated script launches a visible PowerShell window (for UAC), runs coverage, writes results to `native-coverage-elevated.log`, creates `.done` marker, then exits automatically.
