// These hooks ship in the Release DLL on purpose: the shipped binary is the tested binary, and
// their cost was measured as not significant. Rationale and numbers: docs/architecture.md,
// "Native test hooks ship in the release DLL".
#include "pch.h"

#include <algorithm>
#include <array>
#include <atomic>
#include <cerrno>
#include <mutex>
#include <thread>
#include <new>
#include <system_error>
#include <stdexcept>

#include "../internal.h"
#include "../mft_api.h"
#include "exception_barrier.h"

namespace {
unsigned g_maximumThreads = 0;
int g_allocationFailureCountdown = 0;
std::atomic<int> g_exceptionMode{0};
std::atomic<int> g_threadStartCountdown{0};
int g_readFailCountdown = 0;
int g_failFileSize = 0;
int g_failPlatformReadCountdown = 0;
int g_failPlatformWrite = 0;
#ifndef _WIN32
// Interrupted positioned I/O: the mode (see InterruptedIoMode in internal.h), the interruptions still to
// inject, and how many times pread_at/pwrite_at consulted the hook while a mode was armed.
std::atomic<int> g_interruptedIoMode{0};
std::atomic<int> g_interruptedIoRemaining{0};
std::atomic<int> g_interruptedIoCalls{0};
#endif
uint32_t g_volumeRecordSizeOverride = 0;
// Process-global observation of the most recently finished parse, published by
// ParseThreadRecording's destructor. Publication, reset and the getter lock the mutex.
// The mutex protects access, not test ownership: tests reading these counts must not run
// concurrently with other parses or tests touching the hooks.
std::mutex g_parseThreadCountsMutex;
std::array<unsigned, 1024> g_chunkThreadCounts = {};
unsigned g_chunkThreadCountLength = 0;

void ClearRecordedParseThreadCounts() {
    std::scoped_lock lock(g_parseThreadCountsMutex);
    g_chunkThreadCountLength = 0;
}
// While armed, each parse cancellation check decrements the countdown, and the check that takes
// it from 1, and every check after it, reports cancelled. Atomic because parse workers check
// concurrently; unarmed, a check costs one relaxed load.
std::atomic<bool> g_cancelCheckCountdownArmed{false};
std::atomic<int> g_cancelCheckCountdown{0};
#ifdef _WIN32
DWORD g_usnIoFailError = 0;
int g_usnIoFailCountdown = 0;
// Ring queue of synthetic IOCTL success responses (buffers owned by the caller).
std::array<const uint8_t*, 8> g_usnIoData = {};
std::array<uint32_t, 8> g_usnIoSize = {};
int g_usnIoHead = 0;
int g_usnIoCount = 0;
int g_usnOverlappedAbort = 0;
// Armed watch pipes (one per drive in multi-drive tests), each with its own cancel-attempted event.
struct UsnWatchPipeSlot {
    HANDLE handle;
    HANDLE cancelAttempted;
};
std::array<UsnWatchPipeSlot, 4> g_usnWatchPipes = {};
size_t g_usnWatchPipeCount = 0;
HANDLE g_usnBeforeIssue = nullptr;
HANDLE g_usnContinueIssue = nullptr;
HANDLE g_usnIssued = nullptr;
BOOL g_usnCancelFoundRequest = FALSE;
std::atomic<int> g_usnGateReadNumber{0};
// Ordered log of watch-pipe cancels (UsnCallCancel) and result frees (UsnCallFree) while the pipe is armed.
std::array<std::atomic<int>, 16> g_usnCallLog = {};
std::atomic<int> g_usnCallCount{0};

UsnWatchPipeSlot* FindUsnWatchPipe(HANDLE handle) {
    const auto end = g_usnWatchPipes.begin() + static_cast<std::ptrdiff_t>(g_usnWatchPipeCount);
    const auto found = std::find_if(g_usnWatchPipes.begin(), end, [handle](const UsnWatchPipeSlot& slot) { return slot.handle == handle; });
    return found == end ? nullptr : &*found;
}

void LogUsnCall(int kind) {
    const auto index = static_cast<size_t>(g_usnCallCount.fetch_add(1));
    if (index < g_usnCallLog.size()) {
        g_usnCallLog[index] = kind;
    }
}
#endif
}  // namespace

unsigned EffectiveThreadCount(const MftParseControl* control) {
    const unsigned processorCount = std::max<unsigned int>(std::thread::hardware_concurrency(), 1);
    unsigned threadCount = processorCount;
    if (control != nullptr) {
        const int32_t allowance = LoadSharedInt32(&control->parseThreadAllowance);
        if (allowance != 0) {
            threadCount = static_cast<unsigned>(std::clamp<int64_t>(allowance, 1, processorCount));
        }
    }
    if (g_maximumThreads > 0 && g_maximumThreads < threadCount) {
        threadCount = g_maximumThreads;
    }
    return threadCount;
}

// Runs during unwinding, so a failed lock leaves the previous observation instead of terminating.
ParseThreadRecording::~ParseThreadRecording() noexcept {
    try {
        std::scoped_lock lock(g_parseThreadCountsMutex);
        std::copy_n(counts_.begin(), length_, g_chunkThreadCounts.begin());
        g_chunkThreadCountLength = length_;
    } catch (const std::system_error&) {
        return;
    }
}

bool ShouldFailAllocation() {
    if (g_allocationFailureCountdown <= 0) {
        return false;
    }
    return --g_allocationFailureCountdown == 0;
}

void ThrowInjectedException(ExceptionTestPoint point) {
    int expected = static_cast<int>(point);
    if (g_exceptionMode.load() != expected) {
        return;
    }
    if (point == ExceptionTestPoint::ThreadStart && g_threadStartCountdown.fetch_sub(1) > 1) {
        return;
    }
    if (!g_exceptionMode.compare_exchange_strong(expected, 0)) {
        return;
    }
    if (point == ExceptionTestPoint::ThreadStart) {
        throw std::system_error(std::make_error_code(std::errc::resource_unavailable_try_again));
    }
    if (point == ExceptionTestPoint::WorkerFailure || point == ExceptionTestPoint::ReadWorker) {
        throw std::runtime_error("Injected native worker failure");
    }
    throw std::bad_alloc{};
}

bool ShouldForceCancel() {
    return g_cancelCheckCountdownArmed.load(std::memory_order_relaxed) &&
           g_cancelCheckCountdown.fetch_sub(1, std::memory_order_relaxed) <= 1;
}

bool ShouldFailRead() {
    if (g_readFailCountdown <= 0) {
        return false;
    }
    return --g_readFailCountdown == 0;
}

bool ShouldFailFileSize() { return g_failFileSize != 0; }

bool ShouldFailPlatformRead() {
    if (g_failPlatformReadCountdown <= 0) {
        return false;
    }
    return --g_failPlatformReadCountdown == 0;
}

bool ShouldFailPlatformWrite() { return g_failPlatformWrite != 0; }
uint32_t VolumeRecordSizeOverride() { return g_volumeRecordSizeOverride; }

#ifndef _WIN32
bool TryInjectInterruptedIo(int64_t& result) {
    const auto mode = static_cast<InterruptedIoMode>(g_interruptedIoMode.load());
    if (mode == InterruptedIoMode::Off) {
        return false;
    }
    g_interruptedIoCalls++;
    switch (mode) {
        case InterruptedIoMode::InterruptBeforeRealResult:
            if (g_interruptedIoRemaining.fetch_sub(1) <= 0) {
                return false;
            }
            errno = EINTR;
            result = -1;
            return true;
        case InterruptedIoMode::ZeroWithStaleInterrupt:
            errno = EINTR;
            result = 0;
            return true;
        case InterruptedIoMode::GenuineError:
            errno = EIO;
            result = -1;
            return true;
        case InterruptedIoMode::Off:
            break;
    }
    return false;
}
#endif

#ifdef _WIN32
// Cross-component test seam used by USN code and exported test hooks; keep external.
// ReSharper disable once CppClangTidyMiscUseInternalLinkage
bool ShouldFailUsnIo(DWORD& outError) {
    if (g_usnIoFailCountdown <= 0) {
        return false;
    }
    if (--g_usnIoFailCountdown == 0) {
        outError = g_usnIoFailError;
        return true;
    }
    return false;
}

bool UsnIoInjectSuccess(void* outBuffer, unsigned long outBufferSize, unsigned long* bytesReturned) {
    if (g_usnIoCount <= 0) {
        return false;
    }
    const uint8_t* data = g_usnIoData[g_usnIoHead];
    uint32_t size = g_usnIoSize[g_usnIoHead];
    g_usnIoHead = (g_usnIoHead + 1) % 8;
    g_usnIoCount--;
    unsigned long copyLength = size < outBufferSize ? size : outBufferSize;
    if ((data != nullptr) && (copyLength != 0U)) {
        memcpy(outBuffer, data, copyLength);
    }
    if (bytesReturned != nullptr) {
        *bytesReturned = copyLength;
    }
    return true;
}

bool UsnIoShouldAbortOverlapped() {
    if (g_usnOverlappedAbort == 0) {
        return false;
    }
    g_usnOverlappedAbort = 0;
    return true;
}

bool TryUsnWatchPipeRead(HANDLE handle, void* buffer, DWORD size, DWORD* bytesReturned, OVERLAPPED* overlapped,
                         BOOL& success) {
    if (FindUsnWatchPipe(handle) == nullptr || overlapped == nullptr) {
        return false;
    }
    const bool gate = --g_usnGateReadNumber == 0;
    if (gate) {
        SetEvent(g_usnBeforeIssue);
        if (WaitForSingleObject(g_usnContinueIssue, INFINITE) != WAIT_OBJECT_0) {
            success = FALSE;
            return true;
        }
    }
    success = ReadFile(handle, buffer, size, bytesReturned, overlapped);
    const DWORD error = success != FALSE ? ERROR_SUCCESS : GetLastError();
    if (gate) {
        SetEvent(g_usnIssued);
    }
    SetLastError(error);
    return true;
}

void NoteUsnWatchCancel(HANDLE handle, BOOL foundRequest) {
    const UsnWatchPipeSlot* const slot = FindUsnWatchPipe(handle);
    if (slot == nullptr) {
        return;
    }
    g_usnCancelFoundRequest = foundRequest;
    LogUsnCall(UsnCallCancel);
    SetEvent(slot->cancelAttempted);
}

void NoteUsnResultFree() {
    if (g_usnWatchPipeCount != 0) {
        LogUsnCall(UsnCallFree);
    }
}
#endif

extern "C" {
EXPORT void SetMaxThreads(unsigned maximumThreads) { g_maximumThreads = maximumThreads; }
EXPORT void SetAllocFailCountdown(int countdown) {
    g_allocationFailureCountdown = (std::max)(countdown, 0);
    g_exceptionMode = countdown < 0 ? countdown : 0;
    // The I/O thread and first parse worker start before the next start fails.
    g_threadStartCountdown = 3;
}
EXPORT void SetReadFailCountdown(int countdown) { g_readFailCountdown = countdown; }
EXPORT void SetFailFileSize(int fail) { g_failFileSize = fail; }
EXPORT void SetFailPlatformRead(int countdown) { g_failPlatformReadCountdown = countdown; }
EXPORT void SetFailPlatformWrite(int fail) { g_failPlatformWrite = fail; }
#ifndef _WIN32
// Arms the interrupted positioned I/O hook. mode is an InterruptedIoMode value; interruptionCount is how many
// EINTR failures InterruptBeforeRealResult returns before the real call runs. Resets the call count.
EXPORT void SetInterruptedIo(int mode, int interruptionCount) {
    g_interruptedIoCalls = 0;
    g_interruptedIoRemaining = interruptionCount;
    g_interruptedIoMode = mode;
}
// How many positioned reads and writes consulted the hook since SetInterruptedIo.
EXPORT int GetInterruptedIoCalls() { return g_interruptedIoCalls.load(); }
#endif
EXPORT void SetVolumeRecordSizeOverride(uint32_t recordSize) { g_volumeRecordSizeOverride = recordSize; }
EXPORT void SetCancelCheckCountdown(int countdown) {
    g_cancelCheckCountdown = countdown;
    g_cancelCheckCountdownArmed = countdown > 0;
}

// Native hardware observation only; independent of parse allowances and test thread caps.
EXPORT unsigned GetNativeHardwareThreadCount() {
    return std::max<unsigned int>(std::thread::hardware_concurrency(), 1);
}

// Copies the thread count each chunk of the most recent parse used, in order, into counts
// and returns how many were copied (at most capacity).
EXPORT unsigned GetChunkThreadCounts(unsigned* counts, unsigned capacity) {
    return NativeBoundary([&]() {
    std::scoped_lock lock(g_parseThreadCountsMutex);
    const unsigned copied = (std::min)(capacity, g_chunkThreadCountLength);
    std::copy_n(g_chunkThreadCounts.begin(), copied, counts);
    return copied;
    }, [](const wchar_t*) { return 0U; });
}
#ifdef _WIN32
// C-ABI test hook; (error, countdown) order is fixed by the C# P/Invoke harness.
// NOLINTNEXTLINE(bugprone-easily-swappable-parameters)
EXPORT void SetUsnIoFailError(DWORD error, int countdown) {
    g_usnIoFailError = error;
    g_usnIoFailCountdown = countdown;
}

// Enqueue one synthetic IOCTL success buffer. The caller owns the buffer and
// must keep it alive until the matching native call consumes it.
EXPORT void SetUsnIoSuccess(const uint8_t* data, uint32_t size) {
    if (g_usnIoCount < 8) {
        int tail = (g_usnIoHead + g_usnIoCount) % 8;
        g_usnIoData[tail] = data;
        g_usnIoSize[tail] = size;
        g_usnIoCount++;
    }
}

// Copies the logged cancel/free calls (UsnCallCancel, UsnCallFree) in call order; returns how many were logged.
EXPORT int GetUsnCallLog(int* out, int capacity) {
    const int count = (std::min)(g_usnCallCount.load(), (std::min)(capacity, static_cast<int>(g_usnCallLog.size())));
    for (int index = 0; index < count; ++index) {
        out[index] = g_usnCallLog[static_cast<size_t>(index)];
    }
    return count;
}
EXPORT void SetUsnOverlappedAbort() { g_usnOverlappedAbort = 1; }
// Whether the most recent cancel of the watch pipe found a pending request to cancel.
EXPORT int GetUsnWatchCancelFoundRequest() { return g_usnCancelFoundRequest != FALSE ? 1 : 0; }
EXPORT void SetUsnWatchPipe(HANDLE handle, HANDLE beforeIssue, HANDLE continueIssue, HANDLE issued,
                            HANDLE cancelAttempted, int gateReadNumber) {
    UsnWatchPipeSlot* slot = FindUsnWatchPipe(handle);
    if (slot == nullptr) {
        slot = &g_usnWatchPipes[(std::min)(g_usnWatchPipeCount, g_usnWatchPipes.size() - 1)];
        g_usnWatchPipeCount = (std::min)(g_usnWatchPipeCount + 1, g_usnWatchPipes.size());
    }
    *slot = {handle, cancelAttempted};
    g_usnBeforeIssue = beforeIssue;
    g_usnContinueIssue = continueIssue;
    g_usnIssued = issued;
    g_usnGateReadNumber = gateReadNumber;
}
EXPORT void ResetTestState() {
    NativeBoundary([&]() {
    g_maximumThreads = 0;
    g_allocationFailureCountdown = 0;
    g_exceptionMode = 0;
    g_readFailCountdown = 0;
    g_failFileSize = 0;
    g_failPlatformReadCountdown = 0;
    g_failPlatformWrite = 0;
    g_volumeRecordSizeOverride = 0;
    ClearRecordedParseThreadCounts();
    g_cancelCheckCountdownArmed = false;
    g_cancelCheckCountdown = 0;
    g_usnIoFailError = 0;
    g_usnIoFailCountdown = 0;
    g_usnIoHead = 0;
    g_usnIoCount = 0;
    g_usnOverlappedAbort = 0;
    g_usnWatchPipeCount = 0;
    g_usnBeforeIssue = nullptr;
    g_usnContinueIssue = nullptr;
    g_usnIssued = nullptr;
    g_usnCancelFoundRequest = FALSE;
    g_usnGateReadNumber = 0;
    g_usnCallCount = 0;
    }, [](const wchar_t*) {});
}
#else
EXPORT void ResetTestState() {
    NativeBoundary([&]() {
    g_maximumThreads = 0;
    g_allocationFailureCountdown = 0;
    g_exceptionMode = 0;
    g_readFailCountdown = 0;
    g_failFileSize = 0;
    g_failPlatformReadCountdown = 0;
    g_failPlatformWrite = 0;
    g_interruptedIoMode = 0;
    g_interruptedIoRemaining = 0;
    g_interruptedIoCalls = 0;
    g_volumeRecordSizeOverride = 0;
    ClearRecordedParseThreadCounts();
    g_cancelCheckCountdownArmed = false;
    g_cancelCheckCountdown = 0;
    }, [](const wchar_t*) {});
}
#endif
}
