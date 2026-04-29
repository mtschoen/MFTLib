# MFTLib Linux Port MVP Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Make `MFTLibNative` compile and run on Linux, with a C++ smoke test that proves end-to-end synthetic MFT generation + parsing works on a non-Windows OS, while keeping the existing Windows MSBuild flow fully intact.

**Architecture:** Add a thin platform-abstraction layer (`core/platform.h`) that wraps file I/O, big-buffer allocation, and the `EXPORT` macro. Win32 and POSIX get separate implementation files chosen by CMake. The existing parsing logic in `mft/` is refactored to call platform abstractions instead of Win32 directly. Windows-only code (`usn/`, `dllmain.cpp`, the HANDLE-based `ParseMFTRecords` path that uses `DeviceIoControl`) is guarded with `#ifdef _WIN32` and excluded from the Linux build via CMake. A new `CMakeLists.txt` builds the native lib on Linux *alongside* (not replacing) the existing `.vcxproj` for Windows. A small `linux_smoke_test.cpp` exercises `GenerateSyntheticMFTUtf8` + `ParseMFTFromFileUtf8` (Linux-only UTF-8 entry points added to dodge the wchar_t-size mismatch — managed-side cross-platform interop is deferred to a follow-up plan).

**Tech Stack:** C++17, CMake 3.20+, POSIX file I/O (`open`/`pread`/`pwrite`/`close`), `std::atomic`, `std::thread` (already used), `posix_memalign` for big buffers. No new third-party deps.

**Out of scope (future plans):**
- Managed wrapper (`MFTLib.csproj`) cross-platform support — needs `wchar_t`→`char16_t` API surgery
- Live volume access on Linux via libntfs-3g or libtsk
- Linux ports of `TestProgram` and `Benchmark`
- USN journal Linux equivalent
- Perf comparison vs sleuthkit / ntfs-3g
- Cross-platform xUnit test runs

---

## Prerequisites

Before starting, confirm tooling on Linux:
- `cmake --version` ≥ 3.20
- `g++ --version` ≥ 9 (or `clang++` ≥ 10), C++17 support
- `make` or `ninja`

If missing on Arch: `sudo pacman -S cmake gcc ninja`. On Debian/Ubuntu: `sudo apt install cmake g++ ninja-build`.

Windows side stays on MSBuild — no toolchain changes needed there.

---

## File Structure

**New files:**
- `MFTLibNative/core/platform.h` — platform-abstraction interface (file I/O, big alloc, EXPORT macro)
- `MFTLibNative/core/platform_win32.cpp` — Win32 implementation
- `MFTLibNative/core/platform_posix.cpp` — POSIX implementation
- `MFTLibNative/CMakeLists.txt` — cross-platform build (Linux primarily, optionally Windows)
- `MFTLibNative/test/linux_smoke_test.cpp` — native end-to-end smoke test
- `MFTLibNative/test/CMakeLists.txt` — smoke test build
- `scripts/build-linux.sh` — convenience build script

**Modified files:**
- `MFTLibNative/internal.h` — make portable (drop `_snwprintf_s`, drop Windows-typed test hook signatures)
- `MFTLibNative/framework.h` — wrap Windows includes in `#ifdef _WIN32`; add POSIX equivalents below the guard
- `MFTLibNative/ntfs.h` — replace Windows typedefs (`ULONG`, `USHORT`, `UCHAR`, `LARGE_INTEGER`) with `stdint.h` types
- `MFTLibNative/core/ntfs_io.h` — type signatures use `int` fd or platform::File* instead of HANDLE for non-Windows variants
- `MFTLibNative/core/ntfs_io.cpp` — replace `ReadFile`/`SetFilePointer` with platform abstractions; HANDLE-based volume reads stay Windows-only
- `MFTLibNative/mft/mft_parse.cpp` — replace `VirtualAlloc`/`VirtualFree`, `InterlockedExchangeAdd64`, `CreateFileW`/`ReadFile`/`GetFileSizeEx` with platform calls and `std::atomic`; add `ParseMFTFromFileUtf8` Linux entry point
- `MFTLibNative/mft/mft_synthetic.cpp` — same pattern; add `GenerateSyntheticMFTUtf8` Linux entry point
- `MFTLibNative/usn/usn_journal.cpp` — wrap entire body in `#ifdef _WIN32`
- `MFTLibNative/dllmain.cpp` — wrap entire body in `#ifdef _WIN32`

---

## Phase 1 — Build infrastructure

### Task 1: Verify Linux toolchain

**Files:** none

- [ ] **Step 1: Check tools exist**

```bash
cmake --version
g++ --version
ninja --version
```

Expected: all three report a version. If any missing, install via the package manager and retry.

- [ ] **Step 2: Confirm git tree clean**

```bash
git -C /home/schoen/MFTLib status
```

Expected: `nothing to commit, working tree clean`. If dirty, stop and ask the user how to proceed.

- [ ] **Step 3: Create a feature branch**

```bash
git -C /home/schoen/MFTLib checkout -b linux-port-mvp
```

Expected: switched to new branch.

---

### Task 2: Add CMakeLists.txt skeleton

**Files:**
- Create: `MFTLibNative/CMakeLists.txt`

- [ ] **Step 1: Write the CMakeLists.txt**

```cmake
cmake_minimum_required(VERSION 3.20)
project(MFTLibNative CXX)

set(CMAKE_CXX_STANDARD 17)
set(CMAKE_CXX_STANDARD_REQUIRED ON)
set(CMAKE_CXX_EXTENSIONS OFF)
set(CMAKE_POSITION_INDEPENDENT_CODE ON)
set(CMAKE_CXX_VISIBILITY_PRESET hidden)

set(SOURCES
    core/test_hooks.cpp
    core/ntfs_io.cpp
    mft/mft_parse.cpp
    mft/mft_synthetic.cpp
)

if(WIN32)
    list(APPEND SOURCES
        dllmain.cpp
        usn/usn_journal.cpp
        core/platform_win32.cpp
    )
else()
    list(APPEND SOURCES
        core/platform_posix.cpp
    )
endif()

add_library(MFTLibNative SHARED ${SOURCES})

target_include_directories(MFTLibNative PRIVATE
    ${CMAKE_CURRENT_SOURCE_DIR}
    ${CMAKE_CURRENT_SOURCE_DIR}/core
)

if(UNIX)
    target_compile_options(MFTLibNative PRIVATE
        -Wall -Wextra -Wno-unused-parameter
    )
    target_link_libraries(MFTLibNative PRIVATE pthread)
endif()

# Smoke test (Linux only for MVP)
if(UNIX AND BUILD_TESTING)
    add_subdirectory(test)
endif()
```

- [ ] **Step 2: Verify CMake configures (will fail at compile, that's expected)**

```bash
cd /home/schoen/MFTLib && cmake -S MFTLibNative -B build/linux -G Ninja
```

Expected: `-- Configuring done` and `-- Generating done`. (Build itself will fail later — we haven't ported the code yet.)

- [ ] **Step 3: Commit**

```bash
git -C /home/schoen/MFTLib add MFTLibNative/CMakeLists.txt
git -C /home/schoen/MFTLib commit -m "build: add CMakeLists.txt skeleton for cross-platform native build"
```

---

## Phase 2 — Platform abstraction layer

### Task 3: Define platform.h interface

**Files:**
- Create: `MFTLibNative/core/platform.h`

- [ ] **Step 1: Write the interface**

```cpp
// platform.h — minimal cross-platform abstractions used by the MFT parser.
// Win32 uses native APIs; POSIX uses open/pread + posix_memalign.
#pragma once

#include <cstddef>
#include <cstdint>

#ifdef _WIN32
    #define MFT_EXPORT extern "C" __declspec(dllexport)
#else
    #define MFT_EXPORT extern "C" __attribute__((visibility("default")))
#endif

namespace mftlib::platform {

struct File;  // opaque handle

// Open for reading. Returns nullptr on failure. Path is UTF-8.
File* open_read(const char* path_utf8);

// Open for writing (creates/truncates). Returns nullptr on failure. Path is UTF-8.
File* open_write(const char* path_utf8);

// Returns -1 on failure.
int64_t size_of(File* f);

// Reads up to count bytes at offset. Returns bytes read (>=0) or -1 on error.
int64_t pread_at(File* f, void* buf, size_t count, int64_t offset);

// Writes count bytes at offset. Returns bytes written or -1 on error.
int64_t pwrite_at(File* f, const void* buf, size_t count, int64_t offset);

// Closes the file. Safe with nullptr.
void close_file(File* f);

// Allocate a large aligned buffer (page-aligned). Returns nullptr on failure.
void* big_alloc(size_t bytes);

// Free a buffer returned from big_alloc. Size must match.
void big_free(void* ptr, size_t bytes);

}  // namespace mftlib::platform
```

- [ ] **Step 2: Commit**

```bash
git -C /home/schoen/MFTLib add MFTLibNative/core/platform.h
git -C /home/schoen/MFTLib commit -m "feat(native): add platform abstraction header"
```

---

### Task 4: Implement Win32 platform backend

**Files:**
- Create: `MFTLibNative/core/platform_win32.cpp`

- [ ] **Step 1: Write the Win32 implementation**

```cpp
#include "platform.h"

#define WIN32_LEAN_AND_MEAN
#include <windows.h>
#include <fileapi.h>
#include <handleapi.h>
#include <stringapiset.h>

#include <string>

namespace mftlib::platform {

struct File {
    HANDLE h;
};

static std::wstring utf8_to_wide(const char* s) {
    if (!s) return {};
    int wlen = MultiByteToWideChar(CP_UTF8, 0, s, -1, nullptr, 0);
    if (wlen <= 0) return {};
    std::wstring w(static_cast<size_t>(wlen - 1), L'\0');
    MultiByteToWideChar(CP_UTF8, 0, s, -1, w.data(), wlen);
    return w;
}

File* open_read(const char* path_utf8) {
    auto wide = utf8_to_wide(path_utf8);
    HANDLE h = CreateFileW(wide.c_str(), GENERIC_READ,
                           FILE_SHARE_READ | FILE_SHARE_WRITE | FILE_SHARE_DELETE,
                           nullptr, OPEN_EXISTING,
                           FILE_FLAG_SEQUENTIAL_SCAN, nullptr);
    if (h == INVALID_HANDLE_VALUE) return nullptr;
    return new File{h};
}

File* open_write(const char* path_utf8) {
    auto wide = utf8_to_wide(path_utf8);
    HANDLE h = CreateFileW(wide.c_str(), GENERIC_WRITE,
                           FILE_SHARE_READ, nullptr, CREATE_ALWAYS,
                           FILE_ATTRIBUTE_NORMAL, nullptr);
    if (h == INVALID_HANDLE_VALUE) return nullptr;
    return new File{h};
}

int64_t size_of(File* f) {
    if (!f) return -1;
    LARGE_INTEGER li{};
    if (!GetFileSizeEx(f->h, &li)) return -1;
    return li.QuadPart;
}

int64_t pread_at(File* f, void* buf, size_t count, int64_t offset) {
    if (!f) return -1;
    OVERLAPPED ov{};
    ov.Offset = static_cast<DWORD>(offset & 0xFFFFFFFF);
    ov.OffsetHigh = static_cast<DWORD>((offset >> 32) & 0xFFFFFFFF);
    DWORD bytesRead = 0;
    if (!ReadFile(f->h, buf, static_cast<DWORD>(count), &bytesRead, &ov)) {
        DWORD err = GetLastError();
        if (err != ERROR_HANDLE_EOF) return -1;
    }
    return bytesRead;
}

int64_t pwrite_at(File* f, const void* buf, size_t count, int64_t offset) {
    if (!f) return -1;
    OVERLAPPED ov{};
    ov.Offset = static_cast<DWORD>(offset & 0xFFFFFFFF);
    ov.OffsetHigh = static_cast<DWORD>((offset >> 32) & 0xFFFFFFFF);
    DWORD bytesWritten = 0;
    if (!WriteFile(f->h, buf, static_cast<DWORD>(count), &bytesWritten, &ov)) {
        return -1;
    }
    return bytesWritten;
}

void close_file(File* f) {
    if (!f) return;
    if (f->h != INVALID_HANDLE_VALUE) CloseHandle(f->h);
    delete f;
}

void* big_alloc(size_t bytes) {
    return VirtualAlloc(nullptr, bytes, MEM_COMMIT | MEM_RESERVE, PAGE_READWRITE);
}

void big_free(void* ptr, size_t /*bytes*/) {
    if (ptr) VirtualFree(ptr, 0, MEM_RELEASE);
}

}  // namespace mftlib::platform
```

- [ ] **Step 2: Verify Windows build still succeeds (manual on Windows host)**

This task introduces a new file but doesn't yet wire it into the existing `.vcxproj`. Mark this step as informational; the wiring happens in Task 11.

- [ ] **Step 3: Commit**

```bash
git -C /home/schoen/MFTLib add MFTLibNative/core/platform_win32.cpp
git -C /home/schoen/MFTLib commit -m "feat(native): add Win32 platform backend"
```

---

### Task 5: Implement POSIX platform backend

**Files:**
- Create: `MFTLibNative/core/platform_posix.cpp`

- [ ] **Step 1: Write the POSIX implementation**

```cpp
#include "platform.h"

#include <fcntl.h>
#include <sys/stat.h>
#include <sys/types.h>
#include <unistd.h>
#include <cstdlib>
#include <cstring>

namespace mftlib::platform {

struct File {
    int fd;
};

File* open_read(const char* path_utf8) {
    if (!path_utf8) return nullptr;
    int fd = ::open(path_utf8, O_RDONLY | O_CLOEXEC);
    if (fd < 0) return nullptr;
    posix_fadvise(fd, 0, 0, POSIX_FADV_SEQUENTIAL);
    return new File{fd};
}

File* open_write(const char* path_utf8) {
    if (!path_utf8) return nullptr;
    int fd = ::open(path_utf8, O_WRONLY | O_CREAT | O_TRUNC | O_CLOEXEC, 0644);
    if (fd < 0) return nullptr;
    return new File{fd};
}

int64_t size_of(File* f) {
    if (!f) return -1;
    struct stat st{};
    if (::fstat(f->fd, &st) != 0) return -1;
    return static_cast<int64_t>(st.st_size);
}

int64_t pread_at(File* f, void* buf, size_t count, int64_t offset) {
    if (!f) return -1;
    ssize_t total = 0;
    auto* p = static_cast<char*>(buf);
    while (count > 0) {
        ssize_t n = ::pread(f->fd, p, count, offset);
        if (n < 0) return -1;
        if (n == 0) break;  // EOF
        total += n; p += n; offset += n; count -= static_cast<size_t>(n);
    }
    return total;
}

int64_t pwrite_at(File* f, const void* buf, size_t count, int64_t offset) {
    if (!f) return -1;
    ssize_t total = 0;
    const auto* p = static_cast<const char*>(buf);
    while (count > 0) {
        ssize_t n = ::pwrite(f->fd, p, count, offset);
        if (n < 0) return -1;
        if (n == 0) break;
        total += n; p += n; offset += n; count -= static_cast<size_t>(n);
    }
    return total;
}

void close_file(File* f) {
    if (!f) return;
    if (f->fd >= 0) ::close(f->fd);
    delete f;
}

void* big_alloc(size_t bytes) {
    void* p = nullptr;
    if (posix_memalign(&p, 4096, bytes) != 0) return nullptr;
    return p;
}

void big_free(void* ptr, size_t /*bytes*/) {
    std::free(ptr);
}

}  // namespace mftlib::platform
```

- [ ] **Step 2: Quick syntax check via CMake (build will still fail later in this task chain)**

```bash
cd /home/schoen/MFTLib && cmake --build build/linux --target MFTLibNative 2>&1 | head -40
```

Expected: errors are about *other* files (mft_parse.cpp, ntfs_io.cpp, etc.), not `platform_posix.cpp`. If the POSIX file itself has errors, fix them before continuing.

- [ ] **Step 3: Commit**

```bash
git -C /home/schoen/MFTLib add MFTLibNative/core/platform_posix.cpp
git -C /home/schoen/MFTLib commit -m "feat(native): add POSIX platform backend"
```

---

## Phase 3 — Make headers portable

### Task 6: Make `framework.h` portable

**Files:**
- Modify: `MFTLibNative/framework.h`

- [ ] **Step 1: Replace contents**

```cpp
#pragma once

#ifdef _WIN32
    #define WIN32_LEAN_AND_MEAN
    #include <windows.h>
    #include <ioapiset.h>
    #include <fileapi.h>
    #include <handleapi.h>
    #include <winioctl.h>
    #include <minwindef.h>
#endif

#include <cstdio>
#include <cstdint>
#include <memory>
#include <vector>
```

- [ ] **Step 2: Commit**

```bash
git -C /home/schoen/MFTLib add MFTLibNative/framework.h
git -C /home/schoen/MFTLib commit -m "refactor(native): guard Windows headers in framework.h"
```

---

### Task 7: Replace Windows typedefs in `ntfs.h`

The file currently uses `ULONG`, `USHORT`, `UCHAR`, `LARGE_INTEGER` — these come from `<windows.h>`. Replace with `stdint.h` types so the header compiles under both toolchains.

**Files:**
- Modify: `MFTLibNative/ntfs.h`

- [ ] **Step 1: Add a portable types prelude at the top**

Insert immediately after `#pragma once`:

```cpp
#pragma once

#include <cstdint>

#ifndef _WIN32
    using ULONG  = uint32_t;
    using USHORT = uint16_t;
    using UCHAR  = uint8_t;
    using LONGLONG = int64_t;
    using ULONGLONG = uint64_t;
    union LARGE_INTEGER {
        struct { uint32_t LowPart; int32_t HighPart; };
        int64_t QuadPart;
    };
    using PVOID = void*;
    using BOOL = int;
    using DWORD = uint32_t;
    using HANDLE = void*;
    using PDWORD = DWORD*;
#endif
```

(Keep the rest of the file unchanged.)

- [ ] **Step 2: Try to compile — `ntfs.h` must parse under both toolchains**

```bash
cd /home/schoen/MFTLib && cmake --build build/linux --target MFTLibNative 2>&1 | grep -E "(ntfs.h|error)" | head -30
```

Expected: no errors mentioning `ntfs.h` directly. (Errors elsewhere are still expected.)

- [ ] **Step 3: Commit**

```bash
git -C /home/schoen/MFTLib add MFTLibNative/ntfs.h
git -C /home/schoen/MFTLib commit -m "refactor(native): provide portable typedefs in ntfs.h for non-Windows builds"
```

---

### Task 8: Make `internal.h` portable

`internal.h` uses `_snwprintf_s` (Windows) inside `SetErrorMessage` and declares a test hook `ShouldFailUsnIo(DWORD&)` that uses `DWORD`. Replace `_snwprintf_s` with a portable equivalent and guard the USN-related test hook.

**Files:**
- Modify: `MFTLibNative/internal.h`

- [ ] **Step 1: Replace contents**

```cpp
#pragma once

#include <chrono>
#include <cassert>
#include <cstdarg>
#include <cstdio>
#include <cwchar>
#include <cstdint>

#include "core/platform.h"

#ifdef _WIN32
    #define EXPORT __declspec(dllexport)
#else
    #define EXPORT __attribute__((visibility("default")))
#endif

using SteadyClock = std::chrono::steady_clock;
using TimePoint = SteadyClock::time_point;

static inline double ElapsedMs(TimePoint start, TimePoint end) {
    return std::chrono::duration<double, std::milli>(end - start).count();
}

namespace mftlib::detail {

template <size_t N>
inline int safe_vswprintf(wchar_t (&buffer)[N], const wchar_t* format, va_list args) {
#ifdef _WIN32
    return _vsnwprintf_s(buffer, N, _TRUNCATE, format, args);
#else
    int written = std::vswprintf(buffer, N, format, args);
    if (written < 0 || static_cast<size_t>(written) >= N) {
        buffer[N - 1] = L'\0';
        return -1;
    }
    return written;
#endif
}

}  // namespace mftlib::detail

template <size_t N, typename... Args>
inline void SetErrorMessage(wchar_t (&buffer)[N], const wchar_t* format, Args... arguments) {
    va_list dummy;
    (void)dummy;  // placeholder; use the wrapper below
    int written = std::swprintf(buffer, N, format, arguments...);
    if (written < 0) {
        buffer[N - 1] = L'\0';
    }
    assert(written >= 0 && "error message truncated");
}

unsigned EffectiveThreadCount();
bool ShouldFailAlloc();
bool ShouldFailRead();

#ifdef _WIN32
bool ShouldFailUsnIo(DWORD& outError);
#endif
```

(Note: `swprintf` returns -1 on truncation per C++17, which is what we want here. The `_vsnwprintf_s` Windows variant is gone — `swprintf` works on both platforms.)

- [ ] **Step 2: Verify compile of internal.h consumers picks up the change cleanly on Windows**

(Cannot verify on Linux yet because mft_parse.cpp still uses `VirtualAlloc` etc. — this step is just a textual check for now. The user will validate on Windows during the merge.)

- [ ] **Step 3: Commit**

```bash
git -C /home/schoen/MFTLib add MFTLibNative/internal.h
git -C /home/schoen/MFTLib commit -m "refactor(native): make SetErrorMessage and EXPORT portable in internal.h"
```

---

## Phase 4 — Refactor parsing code to use platform abstractions

### Task 9: Refactor `mft_parse.cpp` — atomics

The file uses `InterlockedExchangeAdd64()`. Replace with `std::atomic<int64_t>::fetch_add`.

**Files:**
- Modify: `MFTLibNative/mft/mft_parse.cpp`

- [ ] **Step 1: Find the InterlockedExchangeAdd64 site**

```bash
grep -n "InterlockedExchangeAdd64\|InterlockedExchange" /home/schoen/MFTLib/MFTLibNative/mft/mft_parse.cpp
```

- [ ] **Step 2: Replace each occurrence**

For each match, replace the surrounding `int64_t` field with `std::atomic<int64_t>` and rewrite the call site:

```cpp
// Before:
int64_t namePoolOffset = 0;
// ...
int64_t old = InterlockedExchangeAdd64(&namePoolOffset, length);

// After:
std::atomic<int64_t> namePoolOffset{0};
// ...
int64_t old = namePoolOffset.fetch_add(length, std::memory_order_relaxed);
```

Add `#include <atomic>` near the top of the file.

- [ ] **Step 3: Commit**

```bash
git -C /home/schoen/MFTLib add MFTLibNative/mft/mft_parse.cpp
git -C /home/schoen/MFTLib commit -m "refactor(native): replace InterlockedExchangeAdd64 with std::atomic in mft_parse"
```

---

### Task 10: Refactor `mft_parse.cpp` — buffer allocation

Replace `VirtualAlloc` / `VirtualFree` with `mftlib::platform::big_alloc` / `big_free`.

**Files:**
- Modify: `MFTLibNative/mft/mft_parse.cpp`

- [ ] **Step 1: Locate sites**

```bash
grep -n "VirtualAlloc\|VirtualFree" /home/schoen/MFTLib/MFTLibNative/mft/mft_parse.cpp
```

- [ ] **Step 2: Replace each occurrence**

```cpp
// Before:
auto* buf = static_cast<uint8_t*>(VirtualAlloc(nullptr, BUFFER_SIZE,
    MEM_COMMIT | MEM_RESERVE, PAGE_READWRITE));
// ...
VirtualFree(buf, 0, MEM_RELEASE);

// After:
auto* buf = static_cast<uint8_t*>(mftlib::platform::big_alloc(BUFFER_SIZE));
// ...
mftlib::platform::big_free(buf, BUFFER_SIZE);
```

Make sure `BUFFER_SIZE` (or whatever name is used) is in scope at the `big_free` call so we can pass the size — POSIX `free` ignores it, Win32 `VirtualFree` ignores it too, but the API requires it.

- [ ] **Step 3: Commit**

```bash
git -C /home/schoen/MFTLib add MFTLibNative/mft/mft_parse.cpp
git -C /home/schoen/MFTLib commit -m "refactor(native): use platform::big_alloc/free in mft_parse"
```

---

### Task 11: Refactor `mft_parse.cpp` — file I/O for `ParseMFTFromFile`

The HANDLE-based `ParseMFTRecords` function uses raw volume reads (`SetFilePointer`/`ReadFile` against a HANDLE passed in by the caller). That stays Windows-only — it's the live-volume path.

The path-based `ParseMFTFromFile` opens the file itself and reads it. Refactor that one to use `mftlib::platform`.

**Files:**
- Modify: `MFTLibNative/mft/mft_parse.cpp`

- [ ] **Step 1: Locate `ParseMFTFromFile`**

```bash
grep -n "ParseMFTFromFile\|ParseMFTRecords" /home/schoen/MFTLib/MFTLibNative/mft/mft_parse.cpp
```

- [ ] **Step 2: Wrap the existing wchar_t Windows entry point**

The existing signature uses `const wchar_t*`. Keep that on Windows, but extract the body into a static internal helper that takes a UTF-8 path and `mftlib::platform::File*`. Then add a new `EXPORT` `ParseMFTFromFileUtf8` that calls the helper directly.

```cpp
// At top of file:
#include "../core/platform.h"

// New internal helper (replaces the existing CreateFileW path):
static MftParseResult* ParseMFTFromFileImpl(const char* path_utf8,
                                            const wchar_t* filter,
                                            uint32_t matchFlags,
                                            uint32_t bufferSizeRecords) {
    auto* result = new MftParseResult{};
    auto* file = mftlib::platform::open_read(path_utf8);
    if (!file) {
        SetErrorMessage(result->errorMessage, L"Failed to open MFT file");
        return result;
    }
    int64_t fileSize = mftlib::platform::size_of(file);
    if (fileSize < 0) {
        mftlib::platform::close_file(file);
        SetErrorMessage(result->errorMessage, L"Failed to query MFT file size");
        return result;
    }

    // ... rest of the existing logic, but use platform::pread_at instead of ReadFile,
    // and platform::big_alloc/big_free instead of VirtualAlloc/VirtualFree.

    mftlib::platform::close_file(file);
    return result;
}

// Existing Windows entry point — wraps the helper:
#ifdef _WIN32
EXPORT MftParseResult* ParseMFTFromFile(const wchar_t* filePath,
                                        const wchar_t* filter,
                                        uint32_t matchFlags,
                                        uint32_t bufferSizeRecords) {
    int u8len = WideCharToMultiByte(CP_UTF8, 0, filePath, -1, nullptr, 0, nullptr, nullptr);
    std::string utf8(static_cast<size_t>(u8len > 0 ? u8len - 1 : 0), '\0');
    if (u8len > 0) {
        WideCharToMultiByte(CP_UTF8, 0, filePath, -1, utf8.data(), u8len, nullptr, nullptr);
    }
    return ParseMFTFromFileImpl(utf8.c_str(), filter, matchFlags, bufferSizeRecords);
}
#endif

// New Linux UTF-8 entry point:
#ifndef _WIN32
EXPORT MftParseResult* ParseMFTFromFileUtf8(const char* filePath,
                                            const wchar_t* filter,
                                            uint32_t matchFlags,
                                            uint32_t bufferSizeRecords) {
    return ParseMFTFromFileImpl(filePath, filter, matchFlags, bufferSizeRecords);
}
#endif
```

(The `filter` parameter is still `wchar_t*`. On Linux, the smoke test will pass `nullptr` for filter; full filter support across platforms is deferred.)

- [ ] **Step 3: Identify the HANDLE-based path and guard it Windows-only**

```cpp
#ifdef _WIN32
EXPORT MftParseResult* ParseMFTRecords(HANDLE volumeHandle, const wchar_t* filter,
                                       uint32_t matchFlags, uint32_t bufferSizeRecords) {
    // ... existing implementation ...
}
#endif
```

- [ ] **Step 4: Commit**

```bash
git -C /home/schoen/MFTLib add MFTLibNative/mft/mft_parse.cpp
git -C /home/schoen/MFTLib commit -m "refactor(native): split ParseMFTFromFile into platform-agnostic core"
```

---

### Task 12: Refactor `mft_synthetic.cpp` similarly

Apply the same pattern: replace `VirtualAlloc`/`VirtualFree` with `platform::big_alloc/big_free`, replace `CreateFileW`/`WriteFile` with `platform::open_write`/`pwrite_at`, replace any `InterlockedExchange*` with `std::atomic`, and add a `GenerateSyntheticMFTUtf8` Linux entry point alongside the Windows one.

**Files:**
- Modify: `MFTLibNative/mft/mft_synthetic.cpp`

- [ ] **Step 1: Locate sites**

```bash
grep -n "VirtualAlloc\|VirtualFree\|CreateFileW\|WriteFile\|InterlockedExchange" /home/schoen/MFTLib/MFTLibNative/mft/mft_synthetic.cpp
```

- [ ] **Step 2: Apply replacements identical in spirit to Tasks 9-11**

Lift the body into a static helper `GenerateSyntheticMFTImpl(const char* path_utf8, ...)` that uses platform calls, then provide:

```cpp
#ifdef _WIN32
EXPORT bool GenerateSyntheticMFT(const wchar_t* outputPath, /* ...other params... */) {
    // convert wide→UTF-8, call impl
}
#endif

#ifndef _WIN32
EXPORT bool GenerateSyntheticMFTUtf8(const char* outputPath, /* ...other params... */) {
    return GenerateSyntheticMFTImpl(outputPath, /* ...other params... */);
}
#endif
```

- [ ] **Step 3: Commit**

```bash
git -C /home/schoen/MFTLib add MFTLibNative/mft/mft_synthetic.cpp
git -C /home/schoen/MFTLib commit -m "refactor(native): use platform abstractions in mft_synthetic"
```

---

### Task 13: Refactor `core/ntfs_io.cpp`

This file holds `Read()` (HANDLE-based volume read), `ApplyFixup`, `ParseDataRuns`, `ReadNonResidentData`, `ReadMFTRecord`, `FindAttribute`. The fixup/parse helpers are pure logic and portable. The volume-read functions (`Read`, `ReadNonResidentData`, `ReadMFTRecord`) use `HANDLE` and `SetFilePointer`/`ReadFile` — those are Windows-only and are only used by the HANDLE-based `ParseMFTRecords` path, which we're already gating.

**Files:**
- Modify: `MFTLibNative/core/ntfs_io.h`
- Modify: `MFTLibNative/core/ntfs_io.cpp`

- [ ] **Step 1: Guard HANDLE-using declarations in the header**

```cpp
// ntfs_io.h
#pragma once
#include <cstdint>
#include <vector>
#include "../ntfs.h"
#include "../framework.h"  // for HANDLE, BOOL, etc. on Windows; portable typedefs on Linux

struct DataRun {
    int64_t clusterOffset;
    uint64_t clusterCount;
};

// Portable helpers:
bool ApplyFixup(uint8_t* record, uint32_t recordSize);
std::vector<DataRun> ParseDataRuns(PATTRIBUTE_RECORD_HEADER attr);
PATTRIBUTE_RECORD_HEADER FindAttribute(uint8_t* record, ATTRIBUTE_TYPE_CODE type);

// Windows-only helpers (HANDLE-based volume reads):
#ifdef _WIN32
BOOL Read(HANDLE handle, void* buffer, uint64_t from, DWORD count, PDWORD bytesRead);
uint8_t* ReadNonResidentData(HANDLE volumeHandle, PATTRIBUTE_RECORD_HEADER attr,
                              uint32_t bytesPerCluster, uint64_t* outSize);
bool ReadMFTRecord(HANDLE volumeHandle, std::vector<DataRun>& mftRuns,
                    uint32_t bytesPerCluster, uint64_t recordNumber, uint8_t* buffer);
#endif
```

- [ ] **Step 2: Wrap the corresponding bodies in `ntfs_io.cpp` with `#ifdef _WIN32`**

Find the bodies of `Read`, `ReadNonResidentData`, `ReadMFTRecord` and wrap each with:

```cpp
#ifdef _WIN32
// ... existing body ...
#endif
```

The portable ones (`ApplyFixup`, `ParseDataRuns`, `FindAttribute`) stay unguarded.

- [ ] **Step 3: Commit**

```bash
git -C /home/schoen/MFTLib add MFTLibNative/core/ntfs_io.h MFTLibNative/core/ntfs_io.cpp
git -C /home/schoen/MFTLib commit -m "refactor(native): guard HANDLE-using volume readers Windows-only"
```

---

### Task 14: Guard `usn/usn_journal.cpp` and `dllmain.cpp`

**Files:**
- Modify: `MFTLibNative/usn/usn_journal.cpp`
- Modify: `MFTLibNative/dllmain.cpp`

- [ ] **Step 1: Wrap entire body of usn_journal.cpp in `#ifdef _WIN32` / `#endif`**

```cpp
#ifdef _WIN32
// ... entire existing file content ...
#endif
```

- [ ] **Step 2: Same for dllmain.cpp**

```cpp
#ifdef _WIN32
// ... entire existing file content ...
#endif
```

- [ ] **Step 3: Verify CMake excludes them on Linux** — already done in Task 2's CMakeLists.txt (the `if(WIN32)` block lists them only for Windows). Double-check that the `add_library` source list on Linux does NOT include them.

```bash
cd /home/schoen/MFTLib && cmake --build build/linux --target MFTLibNative 2>&1 | grep -E "usn_journal|dllmain"
```

Expected: no output (these files are not compiled on Linux).

- [ ] **Step 4: Commit**

```bash
git -C /home/schoen/MFTLib add MFTLibNative/usn/usn_journal.cpp MFTLibNative/dllmain.cpp
git -C /home/schoen/MFTLib commit -m "refactor(native): guard Windows-only translation units with _WIN32"
```

---

## Phase 5 — Linux build green

### Task 15: Build the native lib on Linux

**Files:** none (build only)

- [ ] **Step 1: Reconfigure and build**

```bash
cd /home/schoen/MFTLib && cmake --build build/linux --target MFTLibNative 2>&1 | tee /tmp/mftlib_linux_build.log | tail -50
```

Expected: builds clean to `build/linux/libMFTLibNative.so`. If errors remain, fix them in-line and re-run. Common likely issues:
- Stray references to `wchar_t` printf-family functions: replace with `std::swprintf` on Linux
- Stray Windows-typed parameters: convert via the `ntfs.h` typedefs or guard with `#ifdef`
- Missing `#include <atomic>`, `<cstring>`, etc.

- [ ] **Step 2: Confirm the .so exists and has expected symbols**

```bash
ls -la /home/schoen/MFTLib/build/linux/libMFTLibNative.so
nm -D /home/schoen/MFTLib/build/linux/libMFTLibNative.so | grep -E "ParseMFTFromFileUtf8|GenerateSyntheticMFTUtf8|FreeMftResult"
```

Expected: all three exported symbols listed.

- [ ] **Step 3: Commit any final fixes from Step 1**

```bash
git -C /home/schoen/MFTLib add -A
git -C /home/schoen/MFTLib commit -m "fix(native): clean up Linux build errors"
# (only if there were fixes; skip if Step 1 was already clean)
```

---

## Phase 6 — Native smoke test

### Task 16: Write the smoke test

**Files:**
- Create: `MFTLibNative/test/CMakeLists.txt`
- Create: `MFTLibNative/test/linux_smoke_test.cpp`

- [ ] **Step 1: Write the test**

```cpp
// linux_smoke_test.cpp — proves end-to-end synthetic-generate + parse round-trip on POSIX.
#include <cstdio>
#include <cstdlib>
#include <cstring>
#include <string>

// Mirror the Linux UTF-8 entry-point declarations
struct MftParseResult;

extern "C" bool GenerateSyntheticMFTUtf8(const char* outputPath,
                                          uint32_t recordCount,
                                          uint32_t seed);
extern "C" MftParseResult* ParseMFTFromFileUtf8(const char* filePath,
                                                 const wchar_t* filter,
                                                 uint32_t matchFlags,
                                                 uint32_t bufferSizeRecords);
extern "C" void FreeMftResult(MftParseResult* result);

// Mirror the layout we care about (just for offsetof reads — no struct definition needed
// if we treat the result as opaque and rely on a published accessor)
// For MVP, parse mft_api.h's MftParseResult struct by including it directly:
#include "mft_api.h"

int main(int argc, char** argv) {
    const char* mftPath = "/tmp/synthetic.mft";
    constexpr uint32_t recordCount = 1024;
    constexpr uint32_t seed = 42;

    std::printf("[smoke] generating synthetic MFT (%u records) at %s\n", recordCount, mftPath);
    if (!GenerateSyntheticMFTUtf8(mftPath, recordCount, seed)) {
        std::fprintf(stderr, "[smoke] FAIL: GenerateSyntheticMFTUtf8 returned false\n");
        return 1;
    }

    std::printf("[smoke] parsing %s\n", mftPath);
    MftParseResult* result = ParseMFTFromFileUtf8(mftPath, nullptr, /*matchFlags=*/0, /*bufferSizeRecords=*/4096);
    if (!result) {
        std::fprintf(stderr, "[smoke] FAIL: ParseMFTFromFileUtf8 returned null\n");
        return 1;
    }

    std::printf("[smoke] totalRecords=%llu usedRecords=%llu ioMs=%.2f parseMs=%.2f\n",
                (unsigned long long)result->totalRecords,
                (unsigned long long)result->usedRecords,
                result->ioTimeMs, result->parseTimeMs);

    bool ok = (result->usedRecords > 0 && result->errorMessage[0] == L'\0');
    FreeMftResult(result);

    std::remove(mftPath);

    if (!ok) {
        std::fprintf(stderr, "[smoke] FAIL: usedRecords=0 or errorMessage non-empty\n");
        return 1;
    }
    std::printf("[smoke] PASS\n");
    return 0;
}
```

- [ ] **Step 2: Write the test CMakeLists**

```cmake
add_executable(linux_smoke_test linux_smoke_test.cpp)
target_include_directories(linux_smoke_test PRIVATE
    ${CMAKE_SOURCE_DIR}
    ${CMAKE_SOURCE_DIR}/core
)
target_link_libraries(linux_smoke_test PRIVATE MFTLibNative)
set_target_properties(linux_smoke_test PROPERTIES
    BUILD_RPATH "$ORIGIN/.."
)
```

- [ ] **Step 3: Reconfigure with BUILD_TESTING and build**

```bash
cd /home/schoen/MFTLib && cmake -S MFTLibNative -B build/linux -G Ninja -DBUILD_TESTING=ON
cmake --build build/linux --target linux_smoke_test
```

Expected: `build/linux/test/linux_smoke_test` exists.

- [ ] **Step 4: Run the smoke test**

```bash
LD_LIBRARY_PATH=/home/schoen/MFTLib/build/linux /home/schoen/MFTLib/build/linux/test/linux_smoke_test
echo "exit=$?"
```

Expected output:
```
[smoke] generating synthetic MFT (1024 records) at /tmp/synthetic.mft
[smoke] parsing /tmp/synthetic.mft
[smoke] totalRecords=1024 usedRecords=<>0 ioMs=... parseMs=...
[smoke] PASS
exit=0
```

If `usedRecords=0` or the error message is set, dig into the parse path — likely a remaining Windows assumption that didn't get caught at compile time.

- [ ] **Step 5: Commit**

```bash
git -C /home/schoen/MFTLib add MFTLibNative/test/
git -C /home/schoen/MFTLib commit -m "test(native): add Linux end-to-end smoke test"
```

---

## Phase 7 — Convenience build script and Windows regression check

### Task 17: Add `scripts/build-linux.sh`

**Files:**
- Create: `scripts/build-linux.sh`

- [ ] **Step 1: Write the script**

```bash
#!/usr/bin/env bash
set -euo pipefail

ROOT="$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)"
BUILD_DIR="$ROOT/build/linux"

mkdir -p "$BUILD_DIR"
cmake -S "$ROOT/MFTLibNative" -B "$BUILD_DIR" -G Ninja -DBUILD_TESTING=ON -DCMAKE_BUILD_TYPE=Release "$@"
cmake --build "$BUILD_DIR"

echo
echo "Running smoke test..."
LD_LIBRARY_PATH="$BUILD_DIR" "$BUILD_DIR/test/linux_smoke_test"
```

- [ ] **Step 2: Make executable and verify**

```bash
chmod +x /home/schoen/MFTLib/scripts/build-linux.sh
/home/schoen/MFTLib/scripts/build-linux.sh
```

Expected: clean Release build, smoke test passes.

- [ ] **Step 3: Commit**

```bash
git -C /home/schoen/MFTLib add scripts/build-linux.sh
git -C /home/schoen/MFTLib commit -m "chore: add build-linux.sh convenience script"
```

---

### Task 18: Verify Windows MSBuild flow still works

This is a manual verification step on the user's Windows machine — the Linux agent can't run MSBuild. The plan ends with a hand-off note.

**Files:** none

- [ ] **Step 1: User runs on Windows**

```powershell
MSBuild.exe MFTLib.sln -p:Configuration=Release -p:Platform=x64
```

Expected: solution builds clean. The new `core/platform.h` and `core/platform_win32.cpp` files are picked up by the existing `.vcxproj` ONLY if added to it. **This task includes adding them.**

- [ ] **Step 2: Add new files to MFTLibNative.vcxproj**

The `.vcxproj` lists source files explicitly. After porting, ensure these are added under `<ItemGroup>`:

```xml
<ClCompile Include="core\platform_win32.cpp" />
<ClInclude Include="core\platform.h" />
```

(Add via Visual Studio's Solution Explorer "Add Existing Item" or by hand-editing the .vcxproj. Easier on the user's Windows side.)

- [ ] **Step 3: User runs the existing test suite on Windows**

```powershell
.\scripts\run-coverage.ps1 -NonInteractive
```

Expected: all tests that ran before still run. (Some count may change because we added files but not new tests.)

- [ ] **Step 4: Commit**

```bash
git -C /home/schoen/MFTLib add MFTLibNative/MFTLibNative.vcxproj
git -C /home/schoen/MFTLib commit -m "build: register platform shim sources in MSBuild project"
```

- [ ] **Step 5: Push branch and open PR (or merge directly per user preference)**

```bash
git -C /home/schoen/MFTLib push -u origin linux-port-mvp
```

---

## Self-review notes

**Spec coverage:**
- ✅ Native lib builds on Linux: Tasks 2, 6-15
- ✅ Cross-platform parsing logic preserved: Tasks 7-13 (refactor only, no behavior change)
- ✅ Windows-only code properly guarded: Tasks 11, 13, 14
- ✅ End-to-end proof: Task 16 smoke test
- ✅ Windows regression check: Task 18

**Known gaps (deliberately deferred):**
- Managed wrapper Linux loading — needs `wchar_t`→`char16_t` API surgery; separate plan.
- `filter` parameter on `ParseMFTFromFileUtf8` is still `wchar_t*`; smoke test passes `nullptr`. Full filter support cross-platform comes with the managed-side port.
- USN journal Linux equivalent — not planned (Linux has fanotify/inotify, but that's a different programming model).

**Risks:**
- `mft_synthetic.cpp` and `mft_parse.cpp` may have more Windows assumptions than the explore agent surfaced (e.g., assumptions about page size, alignment, threading primitives). Build errors at Task 15 are the canary. Fix in place; if a fix turns out to be substantial (>30 min of work), pause and re-plan.
- The C# tests may break on Windows after the wchar_t/char16_t changes — but we deliberately avoided those in this MVP. The wcsxxx → swprintf change in `internal.h` is the only Windows-side risk; verify in Task 18.
- `swprintf` truncation behavior differs between glibc and MSVC. Glibc returns negative on truncation; MSVC's variant has a different contract. The `internal.h` SetErrorMessage now uses standard `std::swprintf` everywhere; verify this matches the existing semantics on Windows (it should — both return -1 on failure).

---

## Execution

Plan complete and saved to `docs/superpowers/plans/2026-04-24-linux-port-mvp.md`. Two execution options:

**1. Subagent-Driven (recommended)** — Dispatch a fresh subagent per task, review between tasks, fast iteration.

**2. Inline Execution** — Execute tasks in this session using executing-plans, batch execution with checkpoints.

Which approach?
