#pragma once

#include <algorithm>
#include <atomic>
#include <chrono>
#include <cassert>
#include <cstdio>
#include <cstdint>
#include <vector>

#include "mft_api.h"

using SteadyClock = std::chrono::steady_clock;
using TimePoint = SteadyClock::time_point;

static inline double ElapsedMs(TimePoint start, TimePoint end) {
    return std::chrono::duration<double, std::milli>(end - start).count();
}

template <typename... Args>
void SetErrorMessageBuffer(wchar_t* buffer, size_t bufferLength, const wchar_t* format, Args... arguments) {
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
template <typename... Args>
void SetErrorMessageBuffer(char16_t* buffer, size_t bufferLength, const wchar_t* format, Args... arguments) {
    if (buffer == nullptr || bufferLength == 0) {
        return;
    }
    std::vector<wchar_t> wide(bufferLength);
    SetErrorMessageBuffer(wide.data(), bufferLength, format, arguments...);
    std::transform(wide.begin(), wide.end(), buffer, [](wchar_t unit) { return static_cast<char16_t>(unit); });
}
#endif

template <typename Unit, size_t N, typename... Args>
// NOLINTNEXTLINE(modernize-avoid-c-arrays): array-reference parameter deduces the fixed C-ABI buffer size
void SetErrorMessage(Unit (&buffer)[N], const wchar_t* format, Args... arguments) {
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

struct MftParseControl;

// The parse thread count for one chunk: every processor when control
// is null or its allowance is 0, otherwise the allowance clamped to [1, processors]. The
// SetMaxThreads test hook caps the result. Defined in core/test_hooks.cpp.
unsigned EffectiveThreadCount(const MftParseControl* control);
// Test hook recording (defined in core/test_hooks.cpp): what each chunk of the most recent
// parse used, read back through GetChunkThreadCounts.
void ResetRecordedParseThreadCounts();
void RecordChunkThreadCount(unsigned threadCount);

// Test hook declarations (defined in core/test_hooks.cpp)
bool ShouldFailAlloc();
bool ShouldFailRead();
// The Nth parse cancellation check after SetCancelCheckCountdown(N), and every check after it,
// reports cancelled, so a test can cancel inside a chunk's workers.
bool ShouldForceCancel();
bool ShouldFailFileSize();
// Force the platform positioned read/write to take their failure branch, so
// pread_at/pwrite_at error handling is coverable without a real I/O failure.
bool ShouldFailPlatformRead();
bool ShouldFailPlatformWrite();
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
#endif
