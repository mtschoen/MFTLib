# Variable MFT Record Size Support Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Make MFTLib parse NTFS volumes that use 4096-byte MFT records (Windows 10+ on 4Kn-formatted drives), not just the 1024-byte default it currently hardcodes.

**Architecture:** Replace `constexpr FILE_RECORD_SIZE = 1024` with a runtime parameter detected from the on-disk data. For file-based parses (`ParseMFTFromFile`/`Utf8`), peek at record 0's `BytesAllocated` field (offset 0x1C of the FILE header) — this tells us the on-disk record size, in bytes, used by the rest of the file. For volume-based parses (`ParseMFTRecords` HANDLE path on Windows), use `BytesPerFileRecordSegment` from `FSCTL_GET_NTFS_VOLUME_DATA` (the existing code already calls this ioctl). Thread the detected size through `ParseMFTImpl`, the chunk-reader contexts, and the per-record buffer math. Update `GenerateSyntheticMFTUtf8`/`GenerateSyntheticMFT` to accept a record size parameter (default 1024 for backward compat), so we can fixture-test both sizes.

**Tech Stack:** C++17, existing CMake build, existing smoke-test runner, the NTFS spec at offset 0x1C of the FILE record header.

**Why this matters:** Found during the dump-volume.sh test on a real 5 GB MFT — only 23% of records (1.18M of 5.08M) were parseable because the volume uses 4 KB records and our parser reads in 1 KB chunks. The first 1 KB of each 4 KB record happens to contain the FILE header, so we got every 4th record. Fix is required for any modern Windows NTFS volume that's been formatted with `format /A:4096` or that defaulted to 4 KB records on a 4Kn-sector drive.

**Out of scope (future plans):**
- Detecting and handling MFT records that span MFT chunks (extension records via `$ATTRIBUTE_LIST`) — orthogonal to record size.
- Reading the boot sector to determine record size (we use record 0's own self-description, which works equivalently).
- ABI changes to the public `MftParseResult` struct — record size is internal to the parse, callers don't need it.

---

## File Structure

**Modified:**
- `MFTLibNative/ntfs.h` — keep `FILE_RECORD_SIZE = 1024` as the **default for synthetic generation only**; add a new `MAX_FILE_RECORD_SIZE = 4096` for static buffer sizing where allocation must be conservative. Document the change in a comment.
- `MFTLibNative/core/ntfs_io.h` — `ReadMFTRecord` already takes runtime `bytesPerCluster` — extend to take `recordSize` as well. `ApplyFixup` already takes `recordSize`; no signature change needed.
- `MFTLibNative/core/ntfs_io.cpp` — update `ReadMFTRecord` body to use the parameter instead of `FILE_RECORD_SIZE`.
- `MFTLibNative/mft/mft_parse.cpp` — biggest set of changes. Add `recordSize` to `FileReadContext`, `VolumeReadContext`, and as a parameter to `ParseMFTImpl`. Replace every `FILE_RECORD_SIZE` reference with `ctx.recordSize` (in chunk readers) or the threaded parameter (in `ParseMFTImpl` and its callees `ProcessRecordSlice`, `ProcessRecordBatch`). Add record-size detection helper `static uint32_t DetectRecordSizeFromHeader(const uint8_t* record1k)`. Use it in `ParseMFTFromFileImpl` after reading the first 1024 bytes. Use `BytesPerFileRecordSegment` from the existing `FSCTL_GET_NTFS_VOLUME_DATA` call in `ParseMFTRecords`.
- `MFTLibNative/mft/mft_synthetic.cpp` — add `recordSize` parameter to `GenerateSyntheticMFTImpl`. Bump `GenerateSyntheticMFT` (Windows) and `GenerateSyntheticMFTUtf8` (Linux) signatures with a default-style helper export to preserve callers, OR add a new entry point. Decision below: add a new entry point `GenerateSyntheticMFTSized` and have the existing entry points call it with `1024`.
- `MFTLibNative/test/linux_smoke_test.cpp` — add tests for the 4096-byte record path: `test_round_trip_4k_records`, `test_record_size_detection_4k`, `test_record_size_detection_invalid_magic`.
- `MFTLib/MFTLibNative.cs` — add a P/Invoke for `GenerateSyntheticMFTSized` so existing C# tests keep working unchanged.

**Created:**
- None. All changes go into existing files.

---

## Phase 1 — Synthetic generator parameterization

### Task 1: Add `GenerateSyntheticMFTSized` entry point with recordSize param

**Files:**
- Modify: `MFTLibNative/mft/mft_synthetic.cpp`

The existing impl hardcodes `FILE_RECORD_SIZE` everywhere. Add a record-size-aware impl, route the existing entry points through it.

- [ ] **Step 1: Read the existing file** to see all `FILE_RECORD_SIZE` and `ApplyUSAProtection(..., FILE_RECORD_SIZE, ...)` sites (already known: lines 52, 75, 163, 186, 221, 238, 265, 270 per `grep -n FILE_RECORD_SIZE`).

- [ ] **Step 2: Modify `GenerateSyntheticMFTImpl` signature**

```cpp
static bool GenerateSyntheticMFTImpl(const char* filePath, uint64_t recordCount,
                                     uint32_t bufferSizeRecords, uint32_t recordSize) {
    // Existing body, but every `FILE_RECORD_SIZE` replaced with `recordSize`.
    // The static buffer for a single record (line 52 area, "uint8_t record[...]")
    // becomes: std::vector<uint8_t> recordVec(recordSize); uint8_t* record = recordVec.data();
}
```

Replace each of the 8 `FILE_RECORD_SIZE` references in the file with `recordSize`. Where there's a stack `uint8_t record[FILE_RECORD_SIZE]`, change to a heap `std::vector<uint8_t>` so the size is dynamic.

- [ ] **Step 3: Add the new export**

```cpp
extern "C" {
#ifdef _WIN32
EXPORT bool GenerateSyntheticMFTSized(const wchar_t* filePath, uint64_t recordCount,
                                       uint32_t bufferSizeRecords, uint32_t recordSize) {
    int u8len = WideCharToMultiByte(CP_UTF8, 0, filePath, -1, nullptr, 0, nullptr, nullptr);
    if (u8len <= 0) return false;
    std::string utf8(static_cast<size_t>(u8len - 1), '\0');
    WideCharToMultiByte(CP_UTF8, 0, filePath, -1, utf8.data(), u8len, nullptr, nullptr);
    return GenerateSyntheticMFTImpl(utf8.c_str(), recordCount, bufferSizeRecords, recordSize);
}
#endif

#ifndef _WIN32
EXPORT bool GenerateSyntheticMFTSizedUtf8(const char* filePath, uint64_t recordCount,
                                           uint32_t bufferSizeRecords, uint32_t recordSize) {
    return GenerateSyntheticMFTImpl(filePath, recordCount, bufferSizeRecords, recordSize);
}
#endif
}
```

- [ ] **Step 4: Route the existing entry points through the new impl**

```cpp
#ifdef _WIN32
EXPORT bool GenerateSyntheticMFT(const wchar_t* filePath, uint64_t recordCount,
                                  uint32_t bufferSizeRecords) {
    int u8len = WideCharToMultiByte(CP_UTF8, 0, filePath, -1, nullptr, 0, nullptr, nullptr);
    if (u8len <= 0) return false;
    std::string utf8(static_cast<size_t>(u8len - 1), '\0');
    WideCharToMultiByte(CP_UTF8, 0, filePath, -1, utf8.data(), u8len, nullptr, nullptr);
    return GenerateSyntheticMFTImpl(utf8.c_str(), recordCount, bufferSizeRecords, FILE_RECORD_SIZE);
}
#endif

#ifndef _WIN32
EXPORT bool GenerateSyntheticMFTUtf8(const char* filePath, uint64_t recordCount,
                                      uint32_t bufferSizeRecords) {
    return GenerateSyntheticMFTImpl(filePath, recordCount, bufferSizeRecords, FILE_RECORD_SIZE);
}
#endif
```

- [ ] **Step 5: Build and verify**

Run: `scripts/build-linux.sh`
Expected: clean build, smoke test still passes (the existing 8 tests use the legacy 1024-byte entry point, which now delegates to the sized impl with `recordSize=1024`).

- [ ] **Step 6: Commit**

```bash
git add MFTLibNative/mft/mft_synthetic.cpp
git commit -m "feat(native): parameterize synthetic MFT generator by record size"
```

---

## Phase 2 — Record size detection helper

### Task 2: Add `DetectRecordSizeFromHeader` static helper

**Files:**
- Modify: `MFTLibNative/mft/mft_parse.cpp`

- [ ] **Step 1: Add the helper near the top of the file (above `ParseMFTFromFileImpl`)**

```cpp
// Reads the BytesAllocated field at offset 0x1C of an NTFS FILE record header.
// Returns the on-disk record size in bytes, or 0 if the magic is wrong / the
// reported size is implausible. Caller must have read at least 0x20 bytes
// from the start of record 0 into `record1k`.
static uint32_t DetectRecordSizeFromHeader(const uint8_t* record1k) {
    if (record1k[0] != 'F' || record1k[1] != 'I' ||
        record1k[2] != 'L' || record1k[3] != 'E') {
        return 0;
    }
    uint32_t bytesAllocated;
    std::memcpy(&bytesAllocated, record1k + 0x1C, sizeof(uint32_t));
    // Sanity: must be a power of 2 in [512, 65536].
    if (bytesAllocated < 512 || bytesAllocated > 65536) return 0;
    if ((bytesAllocated & (bytesAllocated - 1)) != 0) return 0;
    return bytesAllocated;
}
```

- [ ] **Step 2: Build to confirm it compiles** (no behavior change yet — helper is unused)

```bash
scripts/build-linux.sh --no-managed 2>&1 | tail -3
# (Add --no-managed flag if not already supported; skip if Phase 0 hasn't added it.)
```

If `--no-managed` isn't a flag, just run `cmake --build build/linux-coverage` directly.

Expected: clean compile.

- [ ] **Step 3: Commit**

```bash
git add MFTLibNative/mft/mft_parse.cpp
git commit -m "feat(native): add DetectRecordSizeFromHeader helper"
```

---

## Phase 3 — Thread record size through the parse pipeline

### Task 3: Add `recordSize` to `FileReadContext` and `VolumeReadContext`

**Files:**
- Modify: `MFTLibNative/mft/mft_parse.cpp`

- [ ] **Step 1: Update `FileReadContext` struct**

```cpp
struct FileReadContext {
    mftlib::platform::File* file;
    uint64_t recordsRemaining;
    uint32_t bufferSizeRecords;
    int64_t  fileOffset;
    uint32_t recordSize;   // NEW
};
```

- [ ] **Step 2: Update `FileReadChunk` to use `c->recordSize`**

Replace `FILE_RECORD_SIZE` with `c->recordSize` in the byte-count math:

```cpp
static uint64_t FileReadChunk(void* ctx, uint8_t* targetBuffer, double& ioMs) {
    auto* c = (FileReadContext*)ctx;
    if (c->recordsRemaining == 0) return 0;
    uint64_t filesToLoad = c->recordsRemaining < (uint64_t)c->bufferSizeRecords
                           ? c->recordsRemaining
                           : (uint64_t)c->bufferSizeRecords;
    size_t byteCount = (size_t)(filesToLoad * c->recordSize);
    auto ioStart = SteadyClock::now();
    if (ShouldFailRead()) return 0;
    int64_t bytesRead = mftlib::platform::pread_at(c->file, targetBuffer, byteCount, c->fileOffset);
    if (bytesRead <= 0) return 0;
    ioMs += ElapsedMs(ioStart, SteadyClock::now());
    uint64_t recordsRead = (uint64_t)bytesRead / c->recordSize;
    c->fileOffset += (int64_t)recordsRead * c->recordSize;
    c->recordsRemaining -= recordsRead;
    return recordsRead;
}
```

- [ ] **Step 3: Wrap the equivalent change in `#ifdef _WIN32` for `VolumeReadContext`**

```cpp
#ifdef _WIN32
struct VolumeReadContext {
    HANDLE volumeHandle;
    std::vector<DataRun>* mftRuns;
    uint32_t bytesPerCluster;
    uint64_t filesRemaining;
    uint32_t bufferSizeRecords;
    uint32_t runIndex;
    uint64_t positionInBlock;
    uint32_t recordSize;   // NEW
};
#endif
```

- [ ] **Step 4: Update `VolumeReadChunk` to use `c->recordSize`**

Replace `FILE_RECORD_SIZE` with `c->recordSize` in the divisions/multiplications at lines 521, 531, 535:

```cpp
#ifdef _WIN32
static uint64_t VolumeReadChunk(void* ctx, uint8_t* targetBuffer, double& ioMs) {
    // ... existing setup ...
    if (c->positionInBlock == 0) {
        c->filesRemaining = run.clusterCount * c->bytesPerCluster / c->recordSize;
    }
    // ... etc, replacing all FILE_RECORD_SIZE with c->recordSize ...
    if (!Read(c->volumeHandle, targetBuffer, /*offset=*/...,
              (DWORD)(filesToLoad * c->recordSize), &readBytes)) {
        return 0;
    }
    c->positionInBlock += filesToLoad * c->recordSize;
    // ...
}
#endif
```

- [ ] **Step 5: Build to confirm it compiles** (callers haven't been updated yet, so build will fail at construction sites — that's expected)

```bash
cmake --build build/linux-coverage 2>&1 | tail -10
```

Expected: errors at the `FileReadContext ctx = {...}` site about missing initializer for `recordSize`. We fix that in Task 4.

- [ ] **Step 6: Commit**

```bash
git add MFTLibNative/mft/mft_parse.cpp
git commit -m "refactor(native): add recordSize field to read contexts"
```

---

### Task 4: Thread `recordSize` through `ParseMFTImpl` and `ParseMFTFromFileImpl`

**Files:**
- Modify: `MFTLibNative/mft/mft_parse.cpp`

- [ ] **Step 1: Add `recordSize` parameter to `ParseMFTImpl`**

```cpp
static MftParseResult* ParseMFTImpl(
        ReadChunkFn readChunk, void* readContext,
        uint64_t totalRecords,
        const wchar_t* filter, uint32_t matchFlags,
        uint32_t bufferSizeRecords,
        uint32_t recordSize) {        // NEW
    // ... existing body ...
    const size_t bufSize = (size_t)bufferSizeRecords * recordSize;   // use parameter
    // ...
}
```

Inside `ParseMFTImpl`, replace every `FILE_RECORD_SIZE` reference with the new `recordSize` parameter. Specifically at lines 344, 407, 410, 448, 451 (per the grep earlier). Pass `recordSize` to `ProcessRecordSlice` and `ProcessRecordBatch` (next step).

- [ ] **Step 2: Add `recordSize` to `ProcessRecordSlice` and `ProcessRecordBatch`**

```cpp
static void ProcessRecordSlice(
        uint8_t* buffer, uint64_t startIdx, uint64_t endIdx, uint64_t recordBase,
        SliceResult* slice,
        const wchar_t* filter, uint16_t filterLen, uint32_t matchFlags,
        PathLookup* lookup,
        uint32_t recordSize) {        // NEW
    for (uint64_t i = startIdx; i < endIdx; i++) {
        auto* recPtr = buffer + recordSize * i;       // was: FILE_RECORD_SIZE * i
        // ...
        while ((uint8_t*)attr - (uint8_t*)rec < recordSize) {  // was: < FILE_RECORD_SIZE
            // ...
        }
    }
}
```

Same change applies to `ProcessRecordBatch` at the `recPtr = buffer + FILE_RECORD_SIZE * i` site.

The `ApplyFixup(recPtr, FILE_RECORD_SIZE)` calls (lines 410, 451) become `ApplyFixup(recPtr, recordSize)`. `ApplyFixup` already takes recordSize as a parameter — no further change needed there.

- [ ] **Step 3: Update `ParseMFTFromFileImpl` to detect record size**

```cpp
static MftParseResult* ParseMFTFromFileImpl(const char* path_utf8,
                                            const wchar_t* filter,
                                            uint32_t matchFlags,
                                            uint32_t bufferSizeRecords) {
#ifndef _WIN32
    if (filter != nullptr) {
        auto* result = (MftParseResult*)calloc(1, sizeof(MftParseResult));
        if (result) SetErrorMessage(result->errorMessage, L"Filter not supported on Linux yet");
        return result;
    }
#endif

    auto* file = mftlib::platform::open_read(path_utf8);
    if (!file) {
        auto* result = (MftParseResult*)calloc(1, sizeof(MftParseResult));
        if (result) SetErrorMessage(result->errorMessage, L"Failed to open file. Error: %u",
                                    mftlib::platform::last_error());
        return result;
    }

    int64_t fileSize = mftlib::platform::size_of(file);
    if (fileSize < 0) {
        mftlib::platform::close_file(file);
        auto* result = (MftParseResult*)calloc(1, sizeof(MftParseResult));
        if (result) SetErrorMessage(result->errorMessage, L"Failed to get file size");
        return result;
    }

    // Peek at record 0 to determine the on-disk record size.
    uint8_t header[1024];
    int64_t headerRead = mftlib::platform::pread_at(file, header, 1024, 0);
    if (headerRead < 1024) {
        mftlib::platform::close_file(file);
        auto* result = (MftParseResult*)calloc(1, sizeof(MftParseResult));
        if (result) SetErrorMessage(result->errorMessage, L"MFT file too small to read record 0");
        return result;
    }
    uint32_t recordSize = DetectRecordSizeFromHeader(header);
    if (recordSize == 0) {
        mftlib::platform::close_file(file);
        auto* result = (MftParseResult*)calloc(1, sizeof(MftParseResult));
        if (result) SetErrorMessage(result->errorMessage,
            L"Could not detect MFT record size: invalid FILE magic or BytesAllocated");
        return result;
    }

    uint64_t totalRecords = (uint64_t)fileSize / recordSize;
    FileReadContext ctx = { file, totalRecords, bufferSizeRecords, 0, recordSize };
    auto* result = ParseMFTImpl(FileReadChunk, &ctx, totalRecords, filter,
                                 matchFlags, bufferSizeRecords, recordSize);
    mftlib::platform::close_file(file);
    return result;
}
```

- [ ] **Step 4: Update Windows `ParseMFTRecords` to pass recordSize**

Find the existing `ParseMFTRecords` body (Windows-only, after the `#ifdef _WIN32`). It already calls `DeviceIoControl(FSCTL_GET_NTFS_VOLUME_DATA, ...)` to populate `NTFS_VOLUME_DATA_BUFFER ntfsData`. That struct has a `BytesPerFileRecordSegment` field (`LARGE_INTEGER`).

Add the field to the context construction:

```cpp
#ifdef _WIN32
EXPORT MftParseResult* ParseMFTRecords(HANDLE volumeHandle, const wchar_t* filter,
                                        uint32_t matchFlags, uint32_t bufferSizeRecords) {
    // ... existing FSCTL_GET_NTFS_VOLUME_DATA call populating `ntfsData` ...
    uint32_t recordSize = (uint32_t)ntfsData.BytesPerFileRecordSegment.QuadPart;

    // ... existing code that reads boot record, MFT runs, etc.
    //     Anywhere it reads a single record (line 633's record0 read,
    //     line 684's extRecord), use `recordSize` instead of FILE_RECORD_SIZE
    //     for the static buffer and the read length. The buffer becomes:
    //         std::vector<uint8_t> record0(recordSize);
    //         std::vector<uint8_t> extRecord(recordSize);

    VolumeReadContext ctx;
    // ... existing field assignments ...
    ctx.recordSize = recordSize;

    return ParseMFTImpl(VolumeReadChunk, &ctx, totalRecords, filter,
                         matchFlags, bufferSizeRecords, recordSize);
}
#endif
```

The static-buffer change at lines 632 and 684 (`uint8_t record0[FILE_RECORD_SIZE]` and `uint8_t extRecord[FILE_RECORD_SIZE]`) needs to become a heap allocation since the size is now runtime:

```cpp
std::vector<uint8_t> record0Buf(recordSize);
uint8_t* record0 = record0Buf.data();
// ...
ApplyFixup(record0, recordSize);
```

Same pattern for `extRecord`.

- [ ] **Step 5: Update `ReadMFTRecord` in ntfs_io.cpp**

```cpp
// In core/ntfs_io.h:
#ifdef _WIN32
bool ReadMFTRecord(HANDLE volumeHandle, std::vector<DataRun>& mftRuns,
                    uint32_t bytesPerCluster, uint32_t recordSize,    // NEW param
                    uint64_t recordNumber, uint8_t* buffer);
#endif
```

```cpp
// In core/ntfs_io.cpp:
#ifdef _WIN32
bool ReadMFTRecord(HANDLE volumeHandle, std::vector<DataRun>& mftRuns,
                    uint32_t bytesPerCluster, uint32_t recordSize,
                    uint64_t recordNumber, uint8_t* buffer) {
    uint64_t byteOffset = recordNumber * recordSize;   // was: FILE_RECORD_SIZE
    // ...
    if (!Read(volumeHandle, buffer, diskOffset, recordSize, &bytesRead)
        || bytesRead != recordSize)
        return false;
    return ApplyFixup(buffer, recordSize);
}
#endif
```

Update callers of `ReadMFTRecord` to pass the recordSize argument.

- [ ] **Step 6: Build clean**

```bash
scripts/build-linux.sh
```

Expected: clean build, all 8 smoke tests still pass (they use 1024-byte synthetic MFTs).

- [ ] **Step 7: Commit**

```bash
git add MFTLibNative/mft/mft_parse.cpp MFTLibNative/core/ntfs_io.h MFTLibNative/core/ntfs_io.cpp
git commit -m "feat(native): thread record size through parse pipeline"
```

---

## Phase 4 — Tests for 4 KB records

### Task 5: Add 4KB-record smoke tests

**Files:**
- Modify: `MFTLibNative/test/linux_smoke_test.cpp`

- [ ] **Step 1: Declare the new sized entry points at the top of the file**

```cpp
extern "C" bool GenerateSyntheticMFTSizedUtf8(const char* filePath,
                                               uint64_t recordCount,
                                               uint32_t bufferSizeRecords,
                                               uint32_t recordSize);
```

- [ ] **Step 2: Add `test_round_trip_4k_records`**

```cpp
bool test_round_trip_4k_records() {
    const char* path = "/tmp/mftlib_synthetic_4k.mft";
    constexpr uint32_t recordSize = 4096;
    constexpr uint64_t recordCount = 256;

    if (!GenerateSyntheticMFTSizedUtf8(path, recordCount, kDefaultBufferRecords, recordSize)) {
        std::fprintf(stderr, "  setup FAIL: GenerateSyntheticMFTSizedUtf8 returned false\n");
        return false;
    }

    MftParseResult* r = ParseMFTFromFileUtf8(path, nullptr, 0, kDefaultBufferRecords);
    bool ok = r && r->totalRecords == recordCount && r->usedRecords > 0
              && r->errorMessage[0] == L'\0';
    if (ok) {
        std::printf("  total=%llu used=%llu (recordSize=%u)\n",
                    (unsigned long long)r->totalRecords,
                    (unsigned long long)r->usedRecords, recordSize);
    } else if (r) {
        std::fprintf(stderr, "  FAIL: total=%llu used=%llu err[0]=%d\n",
                     (unsigned long long)r->totalRecords,
                     (unsigned long long)r->usedRecords, (int)r->errorMessage[0]);
    }
    if (r) FreeMftResult(r);
    std::remove(path);
    return ok;
}
```

- [ ] **Step 3: Add `test_record_size_detection_invalid_magic`**

```cpp
bool test_record_size_detection_invalid_magic() {
    // Write a file whose first 1024 bytes are zeros (no FILE magic).
    const char* path = "/tmp/mftlib_bad_magic.mft";
    FILE* f = std::fopen(path, "wb");
    if (!f) return false;
    std::vector<uint8_t> zeros(1024, 0);
    std::fwrite(zeros.data(), 1, zeros.size(), f);
    std::fclose(f);

    MftParseResult* r = ParseMFTFromFileUtf8(path, nullptr, 0, kDefaultBufferRecords);
    bool ok = r && r->errorMessage[0] != L'\0' && r->usedRecords == 0;
    if (!ok && r) {
        std::fprintf(stderr, "  FAIL: expected errorMessage on bad magic; used=%llu err[0]=%d\n",
                     (unsigned long long)r->usedRecords, (int)r->errorMessage[0]);
    }
    if (r) FreeMftResult(r);
    std::remove(path);
    return ok;
}
```

- [ ] **Step 4: Register them in the `tests` array in `main()`**

```cpp
const TestCase tests[] = {
    {"round_trip",                test_round_trip},
    {"round_trip_4k_records",     test_round_trip_4k_records},          // NEW
    {"parse_missing_file",        test_parse_missing_file},
    {"parse_empty_file",          test_parse_empty_file},
    {"parse_filter_returns_error", test_parse_filter_returns_error},
    {"alloc_failure_path",        test_alloc_failure_path},
    {"read_failure_path",         test_read_failure_path},
    {"generate_unwritable_path",  test_generate_unwritable_path},
    {"max_threads_clamping",      test_max_threads_clamping},
    {"record_size_invalid_magic", test_record_size_detection_invalid_magic},  // NEW
};
```

Add `#include <vector>` near the top if not already pulled in by other includes.

- [ ] **Step 5: Build and run**

```bash
scripts/build-linux.sh 2>&1 | tail -20
```

Expected: 10/10 pass, including the two new tests. The 4 KB round-trip should report `recordSize=4096` and a non-zero `usedRecords`.

- [ ] **Step 6: Commit**

```bash
git add MFTLibNative/test/linux_smoke_test.cpp
git commit -m "test(native): add 4KB-record round-trip and bad-magic tests"
```

---

## Phase 5 — Verify on the real volume

### Task 6: Re-run dump-volume.sh and verify the fix

**Files:** none

- [ ] **Step 1: Force a fresh extraction (cached dump might be partially mis-aligned in our minds, but the file itself is correct — the `--force` extraction was good)**

Actually no, the dump file is fine. Just re-run the parser:

```bash
LD_LIBRARY_PATH=build/linux-coverage build/linux-coverage/tools/mft-cli dump /tmp/sda1.mft 2>&1 | head -10
```

Expected: `usedRecords` should now be much higher than 1.18M — likely the full 5M minus deleted records and reserved slots.

- [ ] **Step 2: Search README again, confirm hit count rises**

```bash
LD_LIBRARY_PATH=build/linux-coverage build/linux-coverage/tools/mft-cli search /tmp/sda1.mft README 2>&1 | tail -5
```

Expected: hit count rises from the previous 1476 — most of the missing 75% of records will be regular files, so we should see ~3-4× more matches if README files are uniformly distributed.

- [ ] **Step 3: No commit needed for verification — just record the new numbers in the PR description.**

---

## Phase 6 — Update C# bindings and Windows side

### Task 7: Add `GenerateSyntheticMFTSized` P/Invoke binding

**Files:**
- Modify: `MFTLib/MFTLibNative.cs`

- [ ] **Step 1: Add the new DllImport (paired with the existing `GenerateSyntheticMFT`)**

```csharp
[DllImport(LibraryName, EntryPoint = "GenerateSyntheticMFTSized",
           CallingConvention = CallingConvention.Cdecl, CharSet = CharSet.Unicode)]
[return: MarshalAs(UnmanagedType.U1)]
static extern bool NativeGenerateSyntheticMFTSized(string filePath, ulong recordCount,
                                                    uint bufferSizeRecords, uint recordSize);

public static Func<string, ulong, uint, uint, bool> GenerateSyntheticMFTSized =
    NativeGenerateSyntheticMFTSized;
```

(Match the surrounding code's style — the existing file uses a `Func<>` delegate pattern that lets tests swap in mocks.)

- [ ] **Step 2: Build managed lib**

```bash
dotnet build MFTLib/MFTLib.csproj 2>&1 | tail -5
```

Expected: clean build, only the existing SourceLink warning.

- [ ] **Step 3: Commit**

```bash
git add MFTLib/MFTLibNative.cs
git commit -m "feat(managed): add GenerateSyntheticMFTSized P/Invoke binding"
```

---

### Task 8: Update Windows native callers' static buffers (if not done in Task 4)

**Files:**
- Modify: `MFTLibNative/mft/mft_parse.cpp` (Windows-only sections)

This task may be a no-op if Task 4's Step 4 already covered it. If the static-buffer change at lines 632 and 684 wasn't applied during Task 4 because we were focused on Linux, do it now.

- [ ] **Step 1: Search for any remaining `FILE_RECORD_SIZE` in mft_parse.cpp**

```bash
grep -n "FILE_RECORD_SIZE" MFTLibNative/mft/mft_parse.cpp
```

Expected: zero matches. If any remain, they must be in `#ifdef _WIN32` blocks (we don't build Windows here, so the Linux compile won't catch them).

- [ ] **Step 2: Replace remaining usages**

For each remaining hit, decide:
- If it's a stack array `uint8_t buf[FILE_RECORD_SIZE]` → replace with `std::vector<uint8_t>` and pass `.data()` to consumers.
- If it's a numeric multiplier in I/O math → replace with the in-scope `recordSize` variable.
- If it's a hardcoded buffer size for a fixed-purpose 1KB read (rare) → leave as `1024` literally with a comment, since some NTFS structures are always 1KB regardless of MFT record size.

- [ ] **Step 3: Commit**

```bash
git add MFTLibNative/mft/mft_parse.cpp
git commit -m "refactor(native): finish removing FILE_RECORD_SIZE hardcodes from Windows path"
```

(Skip if no changes were needed.)

---

## Phase 7 — Documentation and PR notes

### Task 9: Update the existing Linux port plan with cross-reference

**Files:**
- Modify: `docs/superpowers/plans/2026-04-24-linux-port-mvp.md`

- [ ] **Step 1: Add a "Follow-up" section near the bottom**

Append before the existing self-review section:

```markdown
## Follow-up: variable MFT record size

The MVP ships with `FILE_RECORD_SIZE` hardcoded to 1024. This breaks on
modern NTFS volumes formatted with 4 KB records — surfaced when running
dump-volume.sh against a real /dev/sda1 that returned only 23% of records.

See `docs/superpowers/plans/2026-04-25-variable-mft-record-size.md` for
the fix plan.
```

- [ ] **Step 2: Commit**

```bash
git add docs/superpowers/plans/2026-04-24-linux-port-mvp.md
git commit -m "docs: cross-reference variable-record-size plan"
```

---

## Self-review notes

**Spec coverage:**
- ✅ Detect record size from file content: Task 2 (helper) + Task 4 Step 3 (use it)
- ✅ Detect record size on Windows volume path: Task 4 Step 4 (use `BytesPerFileRecordSegment`)
- ✅ Thread through pipeline: Task 3 (read contexts) + Task 4 (ParseMFTImpl, ProcessRecordSlice/Batch)
- ✅ Synthetic generator: Task 1
- ✅ Tests: Task 5 (4KB round-trip, bad magic)
- ✅ Real-volume verification: Task 6
- ✅ C# bindings: Task 7
- ✅ Windows-side cleanup: Task 8

**Known gaps / risks:**
- **Windows static buffers**: Tasks 4 and 8 both touch the Windows-only paths but the implementer can't compile-test on Linux. The Windows agent will need to verify after merge. Mitigation: keep the changes mechanical (literal substitution `FILE_RECORD_SIZE` → `recordSize`) and use `std::vector<uint8_t>` for stack arrays — both are safe transformations.
- **Performance**: replacing `FILE_RECORD_SIZE` (a `constexpr`) with a runtime variable might prevent some compiler optimizations (constant-folded multiplications). For modern compilers this is negligible, but worth a quick perf sanity check after merge — the existing benchmark on Windows should not regress more than a few percent.
- **The synthetic generator's record-size parameter is exposed via a *new* entry point** (`GenerateSyntheticMFTSized`/`Utf8`) rather than changing the existing one. This preserves all existing C# tests on Windows. The parallel-API approach was chosen over an ABI-breaking signature change.
- **`ApplyFixup` already takes `recordSize`** — verified via `grep`. No fixup logic changes needed beyond passing the right value.
- **`MAX_FILE_RECORD_SIZE = 4096`** mentioned in the file structure — actually not needed in the implementation. Removed.

**Rollback:**
- The change is contained to `mft_parse.cpp`, `mft_synthetic.cpp`, `ntfs_io.{h,cpp}`, and one new entry-point binding in `MFTLibNative.cs`. If the perf regression is real and significant, the parameter can be re-replaced with a compile-time constant under a feature flag while we look for a different fix.

---

## Execution

Plan complete and saved to `docs/superpowers/plans/2026-04-25-variable-mft-record-size.md`. Two execution options:

**1. Subagent-Driven (recommended)** — Dispatch a fresh subagent per task, review between tasks, fast iteration.

**2. Inline Execution** — Execute tasks in this session using executing-plans, batch execution with checkpoints.

Which approach?
