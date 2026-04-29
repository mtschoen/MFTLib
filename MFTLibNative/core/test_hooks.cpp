#include "pch.h"

#include <thread>

#include "../internal.h"

static unsigned g_maxThreads = 0;
static int g_allocFailCountdown = 0;
static int g_readFailCountdown = 0;
#ifdef _WIN32
static DWORD g_usnIoFailError = 0;
static int g_usnIoFailCountdown = 0;
#endif

unsigned EffectiveThreadCount() {
    unsigned n = std::thread::hardware_concurrency();
    if (n < 1) n = 1;
    if (g_maxThreads > 0 && g_maxThreads < n) n = g_maxThreads;
    return n;
}

bool ShouldFailAlloc() {
    if (g_allocFailCountdown <= 0) return false;
    return --g_allocFailCountdown == 0;
}

bool ShouldFailRead() {
    if (g_readFailCountdown <= 0) return false;
    return --g_readFailCountdown == 0;
}

#ifdef _WIN32
bool ShouldFailUsnIo(DWORD& outError) {
    if (g_usnIoFailCountdown <= 0) return false;
    if (--g_usnIoFailCountdown == 0) {
        outError = g_usnIoFailError;
        return true;
    }
    return false;
}
#endif

extern "C" {
    EXPORT void SetMaxThreads(unsigned maxThreads) { g_maxThreads = maxThreads; }
    EXPORT void SetAllocFailCountdown(int countdown) { g_allocFailCountdown = countdown; }
    EXPORT void SetReadFailCountdown(int countdown) { g_readFailCountdown = countdown; }
#ifdef _WIN32
    EXPORT void SetUsnIoFailError(DWORD error, int countdown) { g_usnIoFailError = error; g_usnIoFailCountdown = countdown; }
    EXPORT void ResetTestState() { g_maxThreads = 0; g_allocFailCountdown = 0; g_readFailCountdown = 0; g_usnIoFailError = 0; g_usnIoFailCountdown = 0; }
#else
    EXPORT void ResetTestState() { g_maxThreads = 0; g_allocFailCountdown = 0; g_readFailCountdown = 0; }
#endif
}
