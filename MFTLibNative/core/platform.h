// platform.h - minimal cross-platform abstractions used by the MFT parser.
// Win32 uses native APIs; POSIX uses open/pread + posix_memalign.
#pragma once

// ReSharper disable once CppUnusedIncludeDirective
// aislop-ignore-next-line CppUnusedIncludeDirective -- size_t below needs this on GCC/Clang
#include <cstddef>
#include <cstdint>
#include <new>

namespace mftlib::platform {
struct File;  // opaque handle

// Strong type for a byte offset within a file, so it cannot be transposed with
// the adjacent byte-count argument of pread_at/pwrite_at.
struct FileOffset {
    int64_t value;
};

// Open for reading. Returns nullptr on failure. Path is UTF-8.
File* open_read(const char* path_utf8);
// A second read handle on the file an open handle names, never on whatever its path names now. Null on failure.
File* reopen_read(const File* file);

// Open for writing (creates/truncates). Returns nullptr on failure. Path is UTF-8.
File* open_write(const char* path_utf8);

int64_t size_of(const File* file);

// Reads up to count bytes at offset. Returns bytes read (>=0) or -1 on error.
int64_t pread_at(const File* file, void* buffer, size_t count, FileOffset offset);

// Writes count bytes at offset. Returns bytes written or -1 on error.
int64_t pwrite_at(const File* file, const void* buffer, size_t count, FileOffset offset);

// Closes the file. Safe with nullptr.
void close_file(File* file);

// Allocate a large aligned buffer (page-aligned). Returns nullptr on failure.
void* big_allocate(size_t bytes);

// Free a buffer returned from big_allocate. Size must match.
void big_free(void* pointer, size_t bytes);

// A page-aligned byte buffer from big_allocate that frees itself. Growing it does not keep its contents.
class AlignedBuffer {
   public:
    AlignedBuffer() = default;
    AlignedBuffer(const AlignedBuffer&) = delete;
    AlignedBuffer& operator=(const AlignedBuffer&) = delete;
    AlignedBuffer(AlignedBuffer&&) = delete;
    AlignedBuffer& operator=(AlignedBuffer&&) = delete;
    ~AlignedBuffer() { big_free(data_, capacity_); }

    uint8_t* data() const { return data_; }

    // Makes room for at least bytes. False, with the buffer unchanged, when the allocation fails.
    bool TryReserve(const size_t bytes) {
        if (bytes <= capacity_) {
            return true;
        }
        void* grown = big_allocate(bytes);
        if (grown == nullptr) {
            return false;
        }
        big_free(data_, capacity_);
        data_ = static_cast<uint8_t*>(grown);
        capacity_ = bytes;
        return true;
    }

    // As TryReserve, but a failed allocation throws std::bad_alloc.
    void Reserve(const size_t bytes) {
        if (!TryReserve(bytes)) {
            throw std::bad_alloc{};
        }
    }

   private:
    uint8_t* data_ = nullptr;
    size_t capacity_ = 0;
};

// Returns the OS-level error code from the most recent failed call on this thread
// (GetLastError on Win32, errno on POSIX). Useful for enriching error messages.
uint32_t last_error();
}  // namespace mftlib::platform
