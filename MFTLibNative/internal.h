#pragma once

#include <algorithm>
#include <array>
#include <atomic>
#include <chrono>
#include <cassert>
#include <cstdio>
#include <cstdint>
// aislop-ignore-next-line CppUnusedIncludeDirective -- std::swprintf below needs this on GCC/Clang
#include <cwchar>

#ifndef _WIN32
    #include <cerrno>
#endif

#include "mft_api.h"

using SteadyClock = std::chrono::steady_clock;
using TimePoint = SteadyClock::time_point;

static inline double ElapsedMs(TimePoint start, TimePoint end) {
    return std::chrono::duration<double, std::milli>(end - start).count();
}

template <typename... Arguments>
void SetErrorMessageBuffer(wchar_t* buffer, size_t bufferLength, const wchar_t* format, Arguments... arguments) {
    if (buffer == nullptr || bufferLength == 0) {
        return;
    }
#ifdef _WIN32
    int written = _snwprintf_s(buffer, bufferLength, _TRUNCATE, format, arguments...);
#else
    int written = std::swprintf(buffer, bufferLength, format, arguments...);
    if (written < 0 || static_cast<size_t>(written) >= bufferLength) {
        buffer[bufferLength - 1] = L'\0';
    }
#endif
    assert(written >= 0 && "error message truncated");
}

#ifndef _WIN32
// A parse message buffer holds UTF-16 units and wchar_t is wider here, so the text is formatted
// as wchar_t and narrowed unit by unit. Every message is ASCII text and numbers.
template <typename... Arguments>
void SetErrorMessageBuffer(char16_t* buffer, size_t bufferLength, const wchar_t* format, Arguments... arguments) {
    if (buffer == nullptr || bufferLength == 0) {
        return;
    }
    // Native ABI message buffers are at most 256 units. Formatting failure text must not allocate.
    std::array<wchar_t, 256> wide{};
    const size_t length = (std::min)(bufferLength, wide.size());
    SetErrorMessageBuffer(wide.data(), length, format, arguments...);
    std::transform(wide.begin(), wide.begin() + length, buffer,
                   [](wchar_t unit) { return static_cast<char16_t>(unit); });
}
#endif

template <typename Unit, size_t N, typename... Arguments>
// NOLINTNEXTLINE(modernize-avoid-c-arrays): array-reference parameter deduces the fixed C-ABI buffer size
void SetErrorMessage(Unit (&buffer)[N], const wchar_t* format, Arguments... arguments) {
    SetErrorMessageBuffer(buffer, N, format, arguments...);
}

// Reads a 32-bit field that another thread (the managed caller) may write while it is read.
// An aligned 32-bit load is single-copy atomic on every supported target; the acquire
// fence keeps later reads from moving ahead of it.
inline int32_t LoadSharedInt32(const int32_t* field) {
#ifdef _WIN32
    int32_t value = *static_cast<const volatile int32_t*>(field);
    std::atomic_thread_fence(std::memory_order_acquire);
    return value;
#else
    return __atomic_load_n(field, __ATOMIC_ACQUIRE);
#endif
}

#ifndef _WIN32
// Retries an interrupted POSIX system call while it fails with EINTR.
template <typename Invocation>
inline auto RetryInterruptedSystemCall(Invocation invocation) {
    while (true) {
        const auto result = invocation();
        if (result >= 0 || errno != EINTR) {
            return result;
        }
    }
}
#endif

// The parse thread count for one chunk: every processor when control
// is null or its allowance is 0, otherwise the allowance clamped to [1, processors]. The
// SetMaxThreads test hook caps the result. Defined in core/test_hooks.cpp.
unsigned EffectiveThreadCount(const MftParseControl* control);
// Test hook recording (defined in core/test_hooks.cpp): what each chunk of the most recently
// finished parse used, read back through GetChunkThreadCounts. A parse collects its counts in its
// own instance, which only that parse touches; the destructor replaces the process-global
// observation under a mutex, so concurrent parses never interleave or clear each other's counts.
// Chunks past the capacity are not recorded.
class ParseThreadRecording {
   public:
    ParseThreadRecording() = default;
    ParseThreadRecording(const ParseThreadRecording&) = delete;
    ParseThreadRecording& operator=(const ParseThreadRecording&) = delete;
    ParseThreadRecording(ParseThreadRecording&&) = delete;
    ParseThreadRecording& operator=(ParseThreadRecording&&) = delete;
    ~ParseThreadRecording() noexcept;

    void Record(unsigned threadCount) {
        if (length_ < counts_.size()) {
            counts_[length_++] = threadCount;
        }
    }

   private:
    std::array<unsigned, 1024> counts_ = {};
    unsigned length_ = 0;
};

// Test hook declarations (defined in core/test_hooks.cpp)
extern "C" {
EXPORT void SetAllocFailCountdown(int countdown);
EXPORT void SetReadFailCountdown(int countdown);
EXPORT void SetMaxThreads(unsigned maximumThreads);
EXPORT void ResetTestState();
}
bool ShouldFailAllocation();
// Negative SetAllocFailCountdown modes inject exceptions without adding ABI exports.
enum class ExceptionTestPoint : int8_t {
    ParseAllocation = -1,
    ThreadStart = -2,
    UsnAllocation = -3,
    WorkerFailure = -4,
    ReadWorker = -5,
    UsnProcessing = -6
};
void ThrowInjectedException(ExceptionTestPoint point);
bool ShouldFailRead();
// The Nth parse cancellation check after SetCancelCheckCountdown(N), and every check after it,
// reports cancelled, so a test can cancel inside a chunk's workers.
bool ShouldForceCancel();
bool ShouldFailFileSize();
// Force the platform positioned read/write to take their failure branch, so
// pread_at/pwrite_at error handling is coverable without a real I/O failure.
bool ShouldFailPlatformRead();
bool ShouldFailPlatformWrite();
#ifndef _WIN32
// What SetInterruptedIo makes pread_at/pwrite_at's system call return, so RetryInterruptedSystemCall's
// EINTR handling is coverable without signalling a real call.
enum class InterruptedIoMode : int {
    Off = 0,
    // Fail with EINTR as many times as the interruption count, then run the real call.
    InterruptBeforeRealResult = 1,
    // Return 0 with errno left at EINTR: the stale errno must not be taken for an interruption.
    ZeroWithStaleInterrupt = 2,
    // Fail with EIO on every call: a genuine error is returned, never retried.
    GenuineError = 3
};
// Replaces the next system call's result when armed and returns true; sets errno as the real call would.
bool TryInjectInterruptedIo(int64_t& result);
#endif
uint32_t VolumeRecordSizeOverride();

#ifdef _WIN32
bool ShouldFailUsnIo(DWORD& outError);
// Test seam: inject a synthetic IOCTL success response (dequeues one buffer
// queued via SetUsnIoSuccess), so USN journal success + record-parsing paths
// can be covered without a real elevated volume handle.
bool UsnIoInjectSuccess(void* outBuffer, unsigned long outBufferSize, unsigned long* bytesReturned);
// Test seam: force the overlapped wait to report ERROR_OPERATION_ABORTED,
// exercising the watch cancel path without a real pending IOCTL.
bool UsnIoShouldAbortOverlapped();
bool TryUsnWatchPipeRead(HANDLE handle, void* buffer, DWORD size, DWORD* bytesReturned, OVERLAPPED* overlapped,
                         BOOL& success);
// Test seam: reports a CancelUsnJournalWatch on the test watch pipe and whether it found a request to cancel.
void NoteUsnWatchCancel(HANDLE handle, BOOL foundRequest);
// Test seam: reports a FreeUsnJournalResult while the test watch pipe is armed.
void NoteUsnResultFree();
constexpr int UsnCallCancel = 1;
constexpr int UsnCallFree = 2;
#endif
