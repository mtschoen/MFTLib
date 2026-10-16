#include "platform.h"
#include "../internal.h"

#include <fcntl.h>
#include <sys/stat.h>
#include <sys/types.h>
#include <unistd.h>
#include <cerrno>
#include <cstdlib>
#include <cstring>
#include <memory>

namespace mftlib::platform {

struct File {
    int fileDescriptor;
};

File* open_read(const char* path_utf8) {
    if (path_utf8 == nullptr) {
        return nullptr;
    }
    int fileDescriptor = ::open(path_utf8, O_RDONLY | O_CLOEXEC);
    if (fileDescriptor < 0) {
        return nullptr;
    }
    posix_fadvise(fileDescriptor, 0, 0, POSIX_FADV_SEQUENTIAL);
    auto* file = new (std::nothrow) File{fileDescriptor};
    if (file == nullptr) {
        ::close(fileDescriptor);
    }
    return file;
}

File* open_write(const char* path_utf8) {
    if (path_utf8 == nullptr) {
        return nullptr;
    }
    int fileDescriptor = ::open(path_utf8, O_WRONLY | O_CREAT | O_TRUNC | O_CLOEXEC, 0644);
    if (fileDescriptor < 0) {
        return nullptr;
    }
    auto* file = new (std::nothrow) File{fileDescriptor};
    if (file == nullptr) {
        ::close(fileDescriptor);
    }
    return file;
}

int64_t size_of(const File* file) {
    if (file == nullptr) {
        return -1;
    }
    struct stat fileStat{};
    if (::fstat(file->fileDescriptor, &fileStat) != 0) {
        return -1;
    }
    return static_cast<int64_t>(fileStat.st_size);
}

int64_t pread_at(const File* file, void* buffer, size_t count, FileOffset offset) {
    if (file == nullptr) {
        return -1;
    }
    if (ShouldFailPlatformRead()) {
        errno = EACCES;
        return -1;
    }
    ssize_t totalBytes = 0;
    auto* bytePointer = static_cast<char*>(buffer);
    int64_t currentOffset = offset.value;
    while (count > 0) {
        ssize_t bytesRead = RetryInterruptedSystemCall([&]() -> ssize_t {
            int64_t injected = 0;
            if (TryInjectInterruptedIo(injected)) {
                return static_cast<ssize_t>(injected);
            }
            return ::pread(file->fileDescriptor, bytePointer, count, currentOffset);
        });
        if (bytesRead < 0) {
            return -1;
        }
        if (bytesRead == 0) {
            break;  // EOF
        }
        totalBytes += bytesRead;
        bytePointer += bytesRead;
        currentOffset += bytesRead;
        count -= static_cast<size_t>(bytesRead);
    }
    return totalBytes;
}

int64_t pwrite_at(const File* file, const void* buffer, size_t count, FileOffset offset) {
    if (file == nullptr) {
        return -1;
    }
    if (ShouldFailPlatformWrite()) {
        return -1;
    }
    ssize_t totalBytes = 0;
    const auto* bytePointer = static_cast<const char*>(buffer);
    int64_t currentOffset = offset.value;
    while (count > 0) {
        ssize_t bytesWritten = RetryInterruptedSystemCall([&]() -> ssize_t {
            int64_t injected = 0;
            if (TryInjectInterruptedIo(injected)) {
                return static_cast<ssize_t>(injected);
            }
            return ::pwrite(file->fileDescriptor, bytePointer, count, currentOffset);
        });
        if (bytesWritten < 0) {
            return -1;
        }
        if (bytesWritten == 0) {
            break;
        }
        totalBytes += bytesWritten;
        bytePointer += bytesWritten;
        currentOffset += bytesWritten;
        count -= static_cast<size_t>(bytesWritten);
    }
    return totalBytes;
}

void close_file(File* file) {
    std::unique_ptr<File> owned(file);
    if (!owned) {
        return;
    }
    if (owned->fileDescriptor >= 0) {
        ::close(owned->fileDescriptor);
    }
}

void* big_allocate(size_t bytes) {
    void* allocatedPointer = nullptr;
    if (posix_memalign(&allocatedPointer, 4096, bytes) != 0) {
        return nullptr;
    }
    return allocatedPointer;
}

void big_free(void* pointer, size_t /*bytes*/) { std::free(pointer); }

uint32_t last_error() { return static_cast<uint32_t>(errno); }

}  // namespace mftlib::platform
