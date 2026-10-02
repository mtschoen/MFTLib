// These hooks ship in the Release DLL on purpose: the shipped binary is the tested binary, and
// their cost was measured as not significant. Rationale and numbers: docs/architecture.md,
// "Native test hooks ship in the release DLL".
#include "pch.h"

#include <algorithm>
#include <array>
#include <atomic>
#include <mutex>
#include <thread>

#include "../internal.h"
#include "../mft_api.h"

namespace {
unsigned g_maxThreads = 0;
int g_allocFailCountdown = 0;
int g_readFailCountdown = 0;
uint64_t g_namePoolCapacityOverride = 0;
int g_failFileSize = 0;
int g_failPathConversion = 0;
int g_failPlatformReadCountdown = 0;
int g_failPlatformWrite = 0;
uint32_t g_volumeRecordSizeOverride = 0;
// Process-global observations of the most recent parse, including the production parse path.
// Every chunk records under the mutex; reset, path-resolution recording, and getters also lock.
// Chunks past the array's capacity are not recorded. The mutex protects access, not test ownership:
// tests using these hooks must not run concurrently with other parses or tests touching the hooks.
std::mutex g_parseThreadCountsMutex;
std::array<unsigned, 1024> g_chunkThreadCounts = {};
unsigned g_chunkThreadCountLength = 0;
unsigned g_resolveThreadCount = 0;
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
HANDLE g_usnWatchPipe = nullptr;
HANDLE g_usnBeforeIssue = nullptr;
HANDLE g_usnContinueIssue = nullptr;
HANDLE g_usnIssued = nullptr;
int g_usnGateReadNumber = 0;
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
    if (g_maxThreads > 0 && g_maxThreads < threadCount) {
        threadCount = g_maxThreads;
    }
    return threadCount;
}

void ResetRecordedParseThreadCounts() {
    std::scoped_lock lock(g_parseThreadCountsMutex);
    g_chunkThreadCountLength = 0;
    g_resolveThreadCount = 0;
}

void RecordChunkThreadCount(unsigned threadCount) {
    std::scoped_lock lock(g_parseThreadCountsMutex);
    if (g_chunkThreadCountLength < g_chunkThreadCounts.size()) {
        g_chunkThreadCounts[g_chunkThreadCountLength++] = threadCount;
    }
}

void RecordResolveThreadCount(unsigned threadCount) {
    std::scoped_lock lock(g_parseThreadCountsMutex);
    g_resolveThreadCount = threadCount;
}

bool ShouldFailAlloc() {
    if (g_allocFailCountdown <= 0) {
        return false;
    }
    return --g_allocFailCountdown == 0;
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

uint64_t NamePoolCapacityOverride() { return g_namePoolCapacityOverride; }

bool ShouldFailFileSize() { return g_failFileSize != 0; }
bool ShouldFailPathConversion() { return g_failPathConversion != 0; }

bool ShouldFailPlatformRead() {
    if (g_failPlatformReadCountdown <= 0) {
        return false;
    }
    return --g_failPlatformReadCountdown == 0;
}

bool ShouldFailPlatformWrite() { return g_failPlatformWrite != 0; }
uint32_t VolumeRecordSizeOverride() { return g_volumeRecordSizeOverride; }

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
    unsigned long copyLen = size < outBufferSize ? size : outBufferSize;
    if ((data != nullptr) && (copyLen != 0U)) {
        memcpy(outBuffer, data, copyLen);
    }
    if (bytesReturned != nullptr) {
        *bytesReturned = copyLen;
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
    if (g_usnWatchPipe == nullptr || handle != g_usnWatchPipe || overlapped == nullptr) {
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
#endif

extern "C" {
EXPORT void SetMaxThreads(unsigned maxThreads) { g_maxThreads = maxThreads; }
EXPORT void SetAllocFailCountdown(int countdown) { g_allocFailCountdown = countdown; }
EXPORT void SetReadFailCountdown(int countdown) { g_readFailCountdown = countdown; }
EXPORT void SetNamePoolCapacityOverride(uint64_t bytes) { g_namePoolCapacityOverride = bytes; }
EXPORT void SetFailFileSize(int fail) { g_failFileSize = fail; }
EXPORT void SetFailPathConversion(int fail) { g_failPathConversion = fail; }
EXPORT void SetFailPlatformRead(int countdown) { g_failPlatformReadCountdown = countdown; }
EXPORT void SetFailPlatformWrite(int fail) { g_failPlatformWrite = fail; }
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
    std::scoped_lock lock(g_parseThreadCountsMutex);
    const unsigned copied = (std::min)(capacity, g_chunkThreadCountLength);
    std::copy_n(g_chunkThreadCounts.begin(), copied, counts);
    return copied;
}

// The thread count path resolution of the most recent parse used; 0 when it did not run.
EXPORT unsigned GetResolveThreadCount() {
    std::scoped_lock lock(g_parseThreadCountsMutex);
    return g_resolveThreadCount;
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

EXPORT void SetUsnOverlappedAbort() { g_usnOverlappedAbort = 1; }
EXPORT void SetUsnWatchPipe(HANDLE handle, HANDLE beforeIssue, HANDLE continueIssue, HANDLE issued,
                            int gateReadNumber) {
    g_usnWatchPipe = handle;
    g_usnBeforeIssue = beforeIssue;
    g_usnContinueIssue = continueIssue;
    g_usnIssued = issued;
    g_usnGateReadNumber = gateReadNumber;
}
EXPORT void ResetTestState() {
    g_maxThreads = 0;
    g_allocFailCountdown = 0;
    g_readFailCountdown = 0;
    g_namePoolCapacityOverride = 0;
    g_failFileSize = 0;
    g_failPathConversion = 0;
    g_failPlatformReadCountdown = 0;
    g_failPlatformWrite = 0;
    g_volumeRecordSizeOverride = 0;
    ResetRecordedParseThreadCounts();
    g_cancelCheckCountdownArmed = false;
    g_cancelCheckCountdown = 0;
    g_usnIoFailError = 0;
    g_usnIoFailCountdown = 0;
    g_usnIoHead = 0;
    g_usnIoCount = 0;
    g_usnOverlappedAbort = 0;
    g_usnWatchPipe = nullptr;
    g_usnBeforeIssue = nullptr;
    g_usnContinueIssue = nullptr;
    g_usnIssued = nullptr;
    g_usnGateReadNumber = 0;
}
#else
EXPORT void ResetTestState() {
    g_maxThreads = 0;
    g_allocFailCountdown = 0;
    g_readFailCountdown = 0;
    g_namePoolCapacityOverride = 0;
    g_failFileSize = 0;
    g_failPathConversion = 0;
    g_failPlatformReadCountdown = 0;
    g_failPlatformWrite = 0;
    g_volumeRecordSizeOverride = 0;
    ResetRecordedParseThreadCounts();
    g_cancelCheckCountdownArmed = false;
    g_cancelCheckCountdown = 0;
}
#endif
}
