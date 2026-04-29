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

// Returns the OS-level error code from the most recent failed call on this thread
// (GetLastError on Win32, errno on POSIX). Useful for enriching error messages.
uint32_t last_error();

}  // namespace mftlib::platform
