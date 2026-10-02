#include "pch.h"

#ifdef _WIN32

    #include <array>
    #include <iterator>

    #include "../framework.h"
    #include "../mft_api.h"
    #include "../internal.h"

namespace {
uint8_t* AllocatePages(size_t byteCount) {
    return ShouldFailAlloc()
               ? nullptr
               : static_cast<uint8_t*>(VirtualAlloc(nullptr, byteCount, MEM_COMMIT | MEM_RESERVE, PAGE_READWRITE));
}

READ_USN_JOURNAL_DATA_V1 MakeReadRequest(int64_t startUsn, uint64_t journalId, DWORD bytesToWaitFor) {
    READ_USN_JOURNAL_DATA_V1 request{};
    request.StartUsn = startUsn;
    request.ReasonMask = 0xFFFFFFFF;
    request.BytesToWaitFor = bytesToWaitFor;
    request.UsnJournalID = journalId;
    request.MinMajorVersion = 2;
    request.MaxMajorVersion = 2;
    return request;
}

const wchar_t* DescribeJournalError(DWORD error) {
    switch (error) {
        case ERROR_JOURNAL_NOT_ACTIVE:
            return L"USN journal is not active";
        case ERROR_JOURNAL_DELETE_IN_PROGRESS:
            return L"USN journal deletion is in progress";
        default:
            return nullptr;
    }
}

// A caller-owned buffer view (pointer + byte size) for the IOCTL wrapper, so the
// input and output buffers each travel as one argument instead of a loose
// pointer/size pair that could be transposed.
struct IoBuffer {
    LPVOID data;
    DWORD size;
};

// Wraps DeviceIoControl with test hook support.
// When the USN I/O fail countdown fires, returns FALSE with the configured error code.
BOOL UsnDeviceIoControl(HANDLE handle, DWORD ioControlCode, IoBuffer input, IoBuffer output, LPDWORD bytesReturned,
                        LPOVERLAPPED overlapped) {
    DWORD hookError;
    if (ShouldFailUsnIo(hookError)) {
        SetLastError(hookError);
        return FALSE;
    }
    if (UsnIoInjectSuccess(output.data, output.size, bytesReturned)) {
        return TRUE;
    }
    BOOL pipeSuccess = FALSE;
    if (ioControlCode == FSCTL_READ_USN_JOURNAL &&
        TryUsnWatchPipeRead(handle, output.data, output.size, bytesReturned, overlapped, pipeSuccess)) {
        return pipeSuccess;
    }
    return DeviceIoControl(handle, ioControlCode, input.data, input.size, output.data, output.size, bytesReturned,
                           overlapped);
}

// Wraps GetOverlappedResult so tests can simulate a cancelled (aborted) wait
// without a real pending IOCTL - pairs with SetUsnIoFailError(ERROR_IO_PENDING).
BOOL UsnGetOverlappedResult(HANDLE handle, LPOVERLAPPED overlapped, LPDWORD bytesReturned, BOOL wait) {
    if (UsnIoShouldAbortOverlapped()) {
        SetLastError(ERROR_OPERATION_ABORTED);
        return FALSE;
    }
    return GetOverlappedResult(handle, overlapped, bytesReturned, wait);
}

BOOL CompleteWatchRead(HANDLE volumeHandle, OVERLAPPED* overlapped, DWORD* bytesReturned, HANDLE cancellationEvent) {
    if (cancellationEvent != nullptr) {
        const std::array<HANDLE, 2> events = {cancellationEvent, overlapped->hEvent};
        const DWORD waitResult = WaitForMultipleObjects(2, events.data(), FALSE, INFINITE);
        if (waitResult == WAIT_OBJECT_0) {
            const BOOL cancelled = CancelIoEx(volumeHandle, overlapped);
            const DWORD cancellationError = cancelled != FALSE ? ERROR_SUCCESS : GetLastError();
            const BOOL completed = UsnGetOverlappedResult(volumeHandle, overlapped, bytesReturned, TRUE);
            const DWORD completionError = completed != FALSE ? ERROR_SUCCESS : GetLastError();
            if (cancellationError != ERROR_SUCCESS && cancellationError != ERROR_NOT_FOUND) {
                SetLastError(cancellationError);
                return FALSE;
            }
            SetLastError(completionError);
            return completed;
        }
        if (waitResult != WAIT_OBJECT_0 + 1) {
            const DWORD waitError = waitResult == WAIT_FAILED ? GetLastError() : ERROR_INVALID_FUNCTION;
            CancelIoEx(volumeHandle, overlapped);
            UsnGetOverlappedResult(volumeHandle, overlapped, bytesReturned, TRUE);
            SetLastError(waitError);
            return FALSE;
        }
    }
    return UsnGetOverlappedResult(volumeHandle, overlapped, bytesReturned, TRUE);
}

// Translates a USN read error into result->errorMessage. ERROR_HANDLE_EOF and
// ERROR_WRITE_PROTECT are benign end-of-journal conditions and carry no message.
void ApplyUsnReadError(UsnJournalResult* result, DWORD error, const wchar_t* failContext) {
    if (error == ERROR_HANDLE_EOF || error == ERROR_WRITE_PROTECT) {
        return;
    }
    if (const wchar_t* message = DescribeJournalError(error)) {
        SetErrorMessage(result->errorMessage, message);
    } else if (error == ERROR_JOURNAL_ENTRY_DELETED) {
        SetErrorMessage(result->errorMessage, L"USN journal entries have been deleted; full rescan needed");
    } else {
        SetErrorMessage(result->errorMessage, L"%ls. Error: %lu", failContext, error);
    }
}

// Copies the fixed fields and (clamped) filename of a USN_RECORD_V2 into entry.
void CopyUsnRecordToEntry(UsnJournalEntry& entry, const USN_RECORD_V2* usnRecord) {
    constexpr uint64_t fileRefMask = 0x0000FFFFFFFFFFFF;
    memset(&entry, 0, sizeof(UsnJournalEntry));
    entry.recordNumber = usnRecord->FileReferenceNumber & fileRefMask;
    entry.parentRecordNumber = usnRecord->ParentFileReferenceNumber & fileRefMask;
    entry.sequenceNumber = static_cast<uint16_t>(usnRecord->FileReferenceNumber >> 48);
    entry.usn = usnRecord->Usn;
    entry.timestamp = usnRecord->TimeStamp.QuadPart;
    entry.reason = usnRecord->Reason;
    entry.fileAttributes = usnRecord->FileAttributes;
    uint16_t nameLength = usnRecord->FileNameLength / sizeof(WCHAR);
    entry.fileNameLength = (std::min)(nameLength, static_cast<uint16_t>(std::size(entry.fileName) - 1));
    wmemcpy_s(entry.fileName, std::size(entry.fileName),
              reinterpret_cast<const wchar_t*>(reinterpret_cast<const uint8_t*>(usnRecord) + usnRecord->FileNameOffset),
              entry.fileNameLength);
}

// Doubles result's entry array (copying existing entries). On allocation failure sets
// errorMessage and returns false; the caller must release its read buffer and bail.
bool GrowUsnEntries(UsnJournalResult* result, uint64_t& capacity) {
    uint64_t newCapacity = capacity * 2;
    auto* grown = reinterpret_cast<UsnJournalEntry*>(AllocatePages(newCapacity * sizeof(UsnJournalEntry)));
    if (grown == nullptr) {
        SetErrorMessage(result->errorMessage, L"Failed to grow entry array");
        return false;
    }
    memcpy(grown, result->entries, result->entryCount * sizeof(UsnJournalEntry));
    VirtualFree(result->entries, 0, MEM_RELEASE);
    result->entries = grown;
    capacity = newCapacity;
    return true;
}

// Counts the USN records packed in [readBuffer + 8, readBuffer + bytesReturned).
uint64_t CountUsnRecords(const uint8_t* readBuffer, DWORD bytesReturned) {
    uint64_t count = 0;
    const uint8_t* scanPtr = readBuffer + sizeof(int64_t);
    const uint8_t* endPtr = readBuffer + bytesReturned;
    while (scanPtr + sizeof(USN_RECORD_V2) <= endPtr) {
        const auto* rec = reinterpret_cast<const USN_RECORD_V2*>(scanPtr);
        if (rec->RecordLength == 0) {
            break;
        }
        count++;
        scanPtr += rec->RecordLength;
    }
    return count;
}

// Reads the next-USN cursor and copies the batch's records into result->entries.
// No-op when the buffer holds no records or the entry allocation fails.
void PopulateWatchEntries(UsnJournalResult* result, const uint8_t* readBuffer, DWORD bytesReturned) {
    if (bytesReturned < sizeof(int64_t)) {
        return;
    }
    int64_t bufferNextUsn;
    memcpy(&bufferNextUsn, readBuffer, sizeof(int64_t));
    result->nextUsn = bufferNextUsn;

    uint64_t count = CountUsnRecords(readBuffer, bytesReturned);
    if (count == 0) {
        return;
    }
    result->entries = reinterpret_cast<UsnJournalEntry*>(AllocatePages(count * sizeof(UsnJournalEntry)));
    if (result->entries == nullptr) {
        return;
    }

    const uint8_t* endPtr = readBuffer + bytesReturned;
    const uint8_t* recordPtr = readBuffer + sizeof(int64_t);
    for (uint64_t i = 0; i < count && recordPtr + sizeof(USN_RECORD_V2) <= endPtr; i++) {
        const auto* usnRecord = reinterpret_cast<const USN_RECORD_V2*>(recordPtr);
        auto& entry = result->entries[i];
        CopyUsnRecordToEntry(entry, usnRecord);
        result->entryCount++;
        recordPtr += usnRecord->RecordLength;
    }
}
}  // namespace

extern "C" {
EXPORT UsnJournalInfo* QueryUsnJournal(HANDLE volumeHandle) {
    auto* info = new UsnJournalInfo{};

    USN_JOURNAL_DATA_V0 journalData{};
    DWORD bytesReturned = 0;
    if (UsnDeviceIoControl(volumeHandle, FSCTL_QUERY_USN_JOURNAL, IoBuffer{nullptr, 0},
                           IoBuffer{&journalData, static_cast<DWORD>(sizeof(journalData))}, &bytesReturned,
                           nullptr) == 0) {
        DWORD error = GetLastError();
        if (const wchar_t* message = DescribeJournalError(error)) {
            SetErrorMessage(info->errorMessage, message);
        } else {
            SetErrorMessage(info->errorMessage, L"FSCTL_QUERY_USN_JOURNAL failed. Error: %lu", error);
        }
        return info;
    }

    info->journalId = journalData.UsnJournalID;
    info->firstUsn = journalData.FirstUsn;
    info->nextUsn = journalData.NextUsn;
    info->lowestValidUsn = journalData.LowestValidUsn;
    info->maxUsn = journalData.MaxUsn;
    info->maximumSize = journalData.MaximumSize;
    info->allocationDelta = journalData.AllocationDelta;
    return info;
}

EXPORT void FreeUsnJournalInfo(const UsnJournalInfo* info) { std::unique_ptr<const UsnJournalInfo> owned(info); }

// NOLINTNEXTLINE(bugprone-easily-swappable-parameters): C-ABI export, fixed C# P/Invoke signature
EXPORT UsnJournalResult* ReadUsnJournal(HANDLE volumeHandle, int64_t startUsn, uint64_t journalId,
                                        uint32_t maximumBufferReads) {
    auto* result = new UsnJournalResult{};
    result->journalId = journalId;

    constexpr size_t readBufferSize = 64ULL * 1024;
    auto* readBuffer = AllocatePages(readBufferSize);
    if (readBuffer == nullptr) {
        SetErrorMessage(result->errorMessage, L"Failed to allocate read buffer");
        return result;
    }

    constexpr uint64_t initialCapacity = 1024;
    uint64_t capacity = initialCapacity;
    result->entries = reinterpret_cast<UsnJournalEntry*>(AllocatePages(capacity * sizeof(UsnJournalEntry)));
    if (result->entries == nullptr) {
        VirtualFree(readBuffer, 0, MEM_RELEASE);
        SetErrorMessage(result->errorMessage, L"Failed to allocate entry array");
        return result;
    }

    auto readData = MakeReadRequest(startUsn, journalId, 0);

    int64_t nextUsn = startUsn;

    // maximumBufferReads == 0 reads to the journal tip; otherwise at most that many 64 KB buffers.
    for (uint32_t reads = 0; maximumBufferReads == 0 || reads < maximumBufferReads; reads++) {
        readData.StartUsn = nextUsn;

        DWORD bytesReturned = 0;
        BOOL success = UsnDeviceIoControl(
            volumeHandle, FSCTL_READ_USN_JOURNAL, IoBuffer{&readData, static_cast<DWORD>(sizeof(readData))},
            IoBuffer{readBuffer, static_cast<DWORD>(readBufferSize)}, &bytesReturned, nullptr);
        if (success == 0) {
            ApplyUsnReadError(result, GetLastError(), L"FSCTL_READ_USN_JOURNAL failed");
            break;
        }

        if (bytesReturned < sizeof(int64_t)) {
            break;
        }

        int64_t bufferNextUsn;
        memcpy(&bufferNextUsn, readBuffer, sizeof(int64_t));

        if (bufferNextUsn == nextUsn) {
            break;
        }

        nextUsn = bufferNextUsn;

        uint8_t* recordPtr = readBuffer + sizeof(int64_t);
        const uint8_t* endPtr = readBuffer + bytesReturned;

        while (recordPtr + sizeof(USN_RECORD_V2) <= endPtr) {
            const auto* usnRecord = reinterpret_cast<const USN_RECORD_V2*>(recordPtr);
            if (usnRecord->RecordLength == 0) {
                break;
            }

            if (result->entryCount >= capacity && !GrowUsnEntries(result, capacity)) {
                VirtualFree(readBuffer, 0, MEM_RELEASE);
                result->nextUsn = nextUsn;
                return result;
            }

            auto& entry = result->entries[result->entryCount];
            CopyUsnRecordToEntry(entry, usnRecord);

            result->entryCount++;
            recordPtr += usnRecord->RecordLength;
        }
    }

    VirtualFree(readBuffer, 0, MEM_RELEASE);
    result->nextUsn = nextUsn;
    return result;
}

EXPORT void FreeUsnJournalResult(const UsnJournalResult* result) {
    std::unique_ptr<const UsnJournalResult> owned(result);
    if (owned != nullptr && owned->entries != nullptr) {
        VirtualFree(owned->entries, 0, MEM_RELEASE);
    }
}

}  // extern "C"

namespace {
// NOLINTNEXTLINE(bugprone-easily-swappable-parameters): internal counterpart of the fixed C-ABI signatures
UsnJournalResult* WatchUsnJournalBatchCore(HANDLE volumeHandle, int64_t startUsn, uint64_t journalId,
                                           HANDLE cancellationEvent) {
    auto* result = new UsnJournalResult{};
    result->journalId = journalId;
    result->nextUsn = startUsn;

    constexpr size_t readBufferSize = 64ULL * 1024;
    auto* readBuffer = AllocatePages(readBufferSize);
    if (readBuffer == nullptr) {
        SetErrorMessage(result->errorMessage, L"Failed to allocate read buffer");
        return result;
    }

    auto readData = MakeReadRequest(startUsn, journalId, 1);

    OVERLAPPED overlapped{};
    overlapped.hEvent = ShouldFailAlloc() ? nullptr : CreateEventW(nullptr, TRUE, FALSE, nullptr);
    if (overlapped.hEvent == nullptr) {
        VirtualFree(readBuffer, 0, MEM_RELEASE);
        SetErrorMessage(result->errorMessage, L"Failed to create event. Error: %lu", GetLastError());
        return result;
    }

    DWORD bytesReturned = 0;
    BOOL success = UsnDeviceIoControl(
        volumeHandle, FSCTL_READ_USN_JOURNAL, IoBuffer{&readData, static_cast<DWORD>(sizeof(readData))},
        IoBuffer{readBuffer, static_cast<DWORD>(readBufferSize)}, &bytesReturned, &overlapped);

    DWORD error = success != FALSE ? ERROR_SUCCESS : GetLastError();
    if (success == FALSE && error == ERROR_IO_PENDING) {
        success = CompleteWatchRead(volumeHandle, &overlapped, &bytesReturned, cancellationEvent);
        error = success != FALSE ? ERROR_SUCCESS : GetLastError();
    }
    if (success == FALSE) {
        CloseHandle(overlapped.hEvent);
        VirtualFree(readBuffer, 0, MEM_RELEASE);
        // ERROR_OPERATION_ABORTED is the CancelIoEx-driven stop and returns an empty result so the managed iterator
        // can end the stream normally.
        if (error != ERROR_OPERATION_ABORTED) {
            ApplyUsnReadError(result, error, L"FSCTL_READ_USN_JOURNAL watch failed");
        }
        return result;
    }

    CloseHandle(overlapped.hEvent);
    PopulateWatchEntries(result, readBuffer, bytesReturned);
    VirtualFree(readBuffer, 0, MEM_RELEASE);
    return result;
}

}  // namespace

extern "C" {
// NOLINTNEXTLINE(bugprone-easily-swappable-parameters): C-ABI export, fixed C# P/Invoke signature
EXPORT UsnJournalResult* WatchUsnJournalBatch(HANDLE volumeHandle, int64_t startUsn, uint64_t journalId) {
    return WatchUsnJournalBatchCore(volumeHandle, startUsn, journalId, nullptr);
}

// NOLINTNEXTLINE(bugprone-easily-swappable-parameters): C-ABI export, fixed C# P/Invoke signature
EXPORT UsnJournalResult* WatchUsnJournalBatchCancelable(HANDLE volumeHandle, int64_t startUsn, uint64_t journalId,
                                                        HANDLE cancellationEvent) {
    return WatchUsnJournalBatchCore(volumeHandle, startUsn, journalId, cancellationEvent);
}

EXPORT BOOL CancelUsnJournalWatch(HANDLE volumeHandle) { return CancelIoEx(volumeHandle, nullptr); }
}

#endif  // _WIN32
