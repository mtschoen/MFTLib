#pragma once

#include <exception>
#include <new>
#include <thread>
#include <utility>

#include "../internal.h"

// Failure handlers use existing ABI storage or a null/false channel and never allocate.
template <typename Operation, typename Failure>
auto NativeBoundary(Operation operation, Failure failure) noexcept -> decltype(operation()) {
    try {
        return operation();
    } catch (const std::bad_alloc&) {
        return failure(L"Out of memory in native operation");
    } catch (...) {
        return failure(L"Native exception in operation");
    }
}

template <typename Cleanup>
class NativeScopeExit {
    Cleanup cleanup;

   public:
    explicit NativeScopeExit(Cleanup action) : cleanup(std::move(action)) {}
    NativeScopeExit(const NativeScopeExit&) = delete;
    NativeScopeExit& operator=(const NativeScopeExit&) = delete;
    NativeScopeExit(NativeScopeExit&&) = delete;
    NativeScopeExit& operator=(NativeScopeExit&&) = delete;
    ~NativeScopeExit() noexcept { cleanup(); }
};

// Only failure classification crosses threads, so catching bad_alloc needs no exception copy or allocation.
enum class NativeFailure : uint8_t { None, OutOfMemory, Exception };

inline void ThrowNativeFailure(const NativeFailure failure) {
    if (failure == NativeFailure::OutOfMemory) {
        throw std::bad_alloc{};
    }
    if (failure == NativeFailure::Exception) {
        throw std::exception{};
    }
}

// The caller owns the failure slot until after joining, including during unwinding.
template <typename Operation>
std::thread StartNativeThread(Operation operation, NativeFailure& failure) {
    ThrowInjectedException(ExceptionTestPoint::ThreadStart);
    return std::thread([operation = std::move(operation), &failure] {
        try {
            operation();
        } catch (const std::bad_alloc&) {
            failure = NativeFailure::OutOfMemory;
        } catch (...) {
            failure = NativeFailure::Exception;
        }
    });
}
