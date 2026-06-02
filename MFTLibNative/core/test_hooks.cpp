#include "pch.h"

#include <algorithm>
#include <thread>

#include "../internal.h"

static unsigned g_maxThreads = 0;
static int g_allocFailCountdown = 0;
static int g_readFailCountdown = 0;
static uint64_t g_namePoolCapacityOverride = 0;
static int g_failFileSize = 0;
static int g_failPathConversion = 0;
static int g_failPlatformReadCountdown = 0;
static int g_failPlatformWrite = 0;
#ifdef _WIN32
static DWORD g_usnIoFailError = 0;
static int g_usnIoFailCountdown = 0;
// Ring queue of synthetic IOCTL success responses (buffers owned by the caller).
static const uint8_t* g_usnIoData[8] = {};
static uint32_t g_usnIoSize[8] = {};
static int g_usnIoHead = 0;
static int g_usnIoCount = 0;
static int g_usnOverlappedAbort = 0;
#endif

unsigned EffectiveThreadCount() {
    unsigned n = std::thread::hardware_concurrency();
    n = std::max<unsigned int>(n, 1);
    if (g_maxThreads > 0 && g_maxThreads < n) {
        n = g_maxThreads;
    }
    return n;
}

bool ShouldFailAlloc() {
    if (g_allocFailCountdown <= 0) {
        return false;
    }
    return --g_allocFailCountdown == 0;
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

#ifdef _WIN32
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
    if ((data != nullptr) && (copyLen != 0u)) {
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
#ifdef _WIN32
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
EXPORT void ResetTestState() {
    g_maxThreads = 0;
    g_allocFailCountdown = 0;
    g_readFailCountdown = 0;
    g_namePoolCapacityOverride = 0;
    g_failFileSize = 0;
    g_failPathConversion = 0;
    g_failPlatformReadCountdown = 0;
    g_failPlatformWrite = 0;
    g_usnIoFailError = 0;
    g_usnIoFailCountdown = 0;
    g_usnIoHead = 0;
    g_usnIoCount = 0;
    g_usnOverlappedAbort = 0;
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
}
#endif
}
