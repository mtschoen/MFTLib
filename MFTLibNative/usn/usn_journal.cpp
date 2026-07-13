#include "pch.h"

#ifdef _WIN32

    #include "../framework.h"
    #include "../mft_api.h"
    #include "../internal.h"

namespace {

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
    return DeviceIoControl(handle, ioControlCode, input.data, input.size, output.data, output.size, bytesReturned,
                           overlapped);
}

// Wraps GetOverlappedResult so tests can simulate a cancelled (aborted) wait
// without a real pending IOCTL — pairs with SetUsnIoFailError(ERROR_IO_PENDING).
BOOL UsnGetOverlappedResult(HANDLE handle, LPOVERLAPPED overlapped, LPDWORD bytesReturned, BOOL wait) {
    if (UsnIoShouldAbortOverlapped()) {
        SetLastError(ERROR_OPERATION_ABORTED);
        return FALSE;
    }
    return GetOverlappedResult(handle, overlapped, bytesReturned, wait);
}

// Translates a USN read error into result->errorMessage. ERROR_HANDLE_EOF and
// ERROR_WRITE_PROTECT are benign end-of-journal conditions and carry no message.
void ApplyUsnReadError(UsnJournalResult* result, DWORD error, const wchar_t* failContext) {
    if (error == ERROR_HANDLE_EOF || error == ERROR_WRITE_PROTECT) {
        return;
    }
    if (error == ERROR_JOURNAL_NOT_ACTIVE) {
        SetErrorMessage(result->errorMessage, L"USN journal is not active");
    } else if (error == ERROR_JOURNAL_DELETE_IN_PROGRESS) {
        SetErrorMessage(result->errorMessage, L"USN journal deletion is in progress");
    } else if (error == ERROR_JOURNAL_ENTRY_DELETED) {
        SetErrorMessage(result->errorMessage, L"USN journal entries have been deleted; full rescan needed");
    } else {
        SetErrorMessage(result->errorMessage, L"%ls. Error: %lu", failContext, error);
    }
}

// Copies the fixed fields and (clamped) filename of a USN_RECORD_V2 into entry.
// Returns the unclamped name length in WCHAR units; the caller stores the length it
// wants in entry.fileNameLength (ReadUsnJournal keeps the full length, the watch path
// stores the clamped copy length).
uint16_t CopyUsnRecordToEntry(UsnJournalEntry& entry, const USN_RECORD_V2* usnRecord) {
    constexpr uint64_t fileRefMask = 0x0000FFFFFFFFFFFF;
    memset(&entry, 0, sizeof(UsnJournalEntry));
    entry.recordNumber = usnRecord->FileReferenceNumber & fileRefMask;
    entry.parentRecordNumber = usnRecord->ParentFileReferenceNumber & fileRefMask;
    entry.usn = usnRecord->Usn;
    entry.timestamp = usnRecord->TimeStamp.QuadPart;
    entry.reason = usnRecord->Reason;
    entry.fileAttributes = usnRecord->FileAttributes;
    uint16_t nameLenChars = usnRecord->FileNameLength / sizeof(WCHAR);
    uint16_t copyLen = min(nameLenChars, static_cast<uint16_t>(259));
    wmemcpy_s(entry.fileName, 260,
              reinterpret_cast<const wchar_t*>(reinterpret_cast<const uint8_t*>(usnRecord) + usnRecord->FileNameOffset),
              copyLen);
    return nameLenChars;
}

// Doubles result's entry array (copying existing entries). On allocation failure sets
// errorMessage and returns false; the caller must release its read buffer and bail.
bool GrowUsnEntries(UsnJournalResult* result, uint64_t& capacity) {
    uint64_t newCapacity = capacity * 2;
    auto* grown = ShouldFailAlloc() ? nullptr
                                    : static_cast<UsnJournalEntry*>(VirtualAlloc(
                                          nullptr, static_cast<size_t>(newCapacity) * sizeof(UsnJournalEntry),
                                          MEM_COMMIT | MEM_RESERVE, PAGE_READWRITE));
    if (grown == nullptr) {
        SetErrorMessage(result->errorMessage, L"Failed to grow entry array");
        return false;
    }
    memcpy(grown, result->entries, static_cast<size_t>(result->entryCount) * sizeof(UsnJournalEntry));
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
    result->entries =
        ShouldFailAlloc()
            ? nullptr
            : static_cast<UsnJournalEntry*>(VirtualAlloc(nullptr, static_cast<size_t>(count) * sizeof(UsnJournalEntry),
                                                         MEM_COMMIT | MEM_RESERVE, PAGE_READWRITE));
    if (result->entries == nullptr) {
        return;
    }

    const uint8_t* endPtr = readBuffer + bytesReturned;
    const uint8_t* recordPtr = readBuffer + sizeof(int64_t);
    for (uint64_t i = 0; i < count && recordPtr + sizeof(USN_RECORD_V2) <= endPtr; i++) {
        const auto* usnRecord = reinterpret_cast<const USN_RECORD_V2*>(recordPtr);
        auto& entry = result->entries[i];
        uint16_t nameLenChars = CopyUsnRecordToEntry(entry, usnRecord);
        entry.fileNameLength = min(nameLenChars, static_cast<uint16_t>(259));
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
        if (error == ERROR_JOURNAL_NOT_ACTIVE) {
            SetErrorMessage(info->errorMessage, L"USN journal is not active");
        } else if (error == ERROR_JOURNAL_DELETE_IN_PROGRESS) {
            SetErrorMessage(info->errorMessage, L"USN journal deletion is in progress");
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

// aislop-ignore-next-line cpp-manual-delete -- C-ABI free; ownership crosses P/Invoke, no RAII scope
EXPORT void FreeUsnJournalInfo(const UsnJournalInfo* info) { delete info; }

// NOLINTNEXTLINE(bugprone-easily-swappable-parameters): C-ABI export, fixed C# P/Invoke signature
EXPORT UsnJournalResult* ReadUsnJournal(HANDLE volumeHandle, int64_t startUsn, uint64_t journalId) {
    auto* result = new UsnJournalResult{};
    result->journalId = journalId;

    constexpr size_t readBufferSize = 64ULL * 1024;
    auto* readBuffer =
        ShouldFailAlloc()
            ? nullptr
            : static_cast<uint8_t*>(VirtualAlloc(nullptr, readBufferSize, MEM_COMMIT | MEM_RESERVE, PAGE_READWRITE));
    if (readBuffer == nullptr) {
        SetErrorMessage(result->errorMessage, L"Failed to allocate read buffer");
        return result;
    }

    constexpr uint64_t initialCapacity = 1024;
    uint64_t capacity = initialCapacity;
    result->entries = ShouldFailAlloc() ? nullptr
                                        : static_cast<UsnJournalEntry*>(VirtualAlloc(
                                              nullptr, static_cast<size_t>(capacity) * sizeof(UsnJournalEntry),
                                              MEM_COMMIT | MEM_RESERVE, PAGE_READWRITE));
    if (result->entries == nullptr) {
        VirtualFree(readBuffer, 0, MEM_RELEASE);
        SetErrorMessage(result->errorMessage, L"Failed to allocate entry array");
        return result;
    }

    READ_USN_JOURNAL_DATA_V1 readData;
    readData.StartUsn = startUsn;
    readData.ReasonMask = 0xFFFFFFFF;
    readData.ReturnOnlyOnClose = 0;
    readData.Timeout = 0;
    readData.BytesToWaitFor = 0;
    readData.UsnJournalID = journalId;
    readData.MinMajorVersion = 2;
    readData.MaxMajorVersion = 2;

    int64_t nextUsn = startUsn;

    for (;;) {
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
        uint8_t* endPtr = readBuffer + bytesReturned;

        while (recordPtr + sizeof(USN_RECORD_V2) <= endPtr) {
            auto* usnRecord = reinterpret_cast<USN_RECORD_V2*>(recordPtr);
            if (usnRecord->RecordLength == 0) {
                break;
            }

            if (result->entryCount >= capacity && !GrowUsnEntries(result, capacity)) {
                VirtualFree(readBuffer, 0, MEM_RELEASE);
                result->nextUsn = nextUsn;
                return result;
            }

            auto& entry = result->entries[result->entryCount];
            entry.fileNameLength = CopyUsnRecordToEntry(entry, usnRecord);

            result->entryCount++;
            recordPtr += usnRecord->RecordLength;
        }
    }

    VirtualFree(readBuffer, 0, MEM_RELEASE);
    result->nextUsn = nextUsn;
    return result;
}

EXPORT void FreeUsnJournalResult(const UsnJournalResult* result) {
    if (result != nullptr) {
        if (result->entries != nullptr) {
            VirtualFree(result->entries, 0, MEM_RELEASE);
        }
        // aislop-ignore-next-line cpp-manual-delete -- C-ABI free; ownership crosses P/Invoke, no RAII scope
        delete result;
    }
}

// NOLINTNEXTLINE(bugprone-easily-swappable-parameters): C-ABI export, fixed C# P/Invoke signature
EXPORT UsnJournalResult* WatchUsnJournalBatch(HANDLE volumeHandle, int64_t startUsn, uint64_t journalId) {
    auto* result = new UsnJournalResult{};
    result->journalId = journalId;
    result->nextUsn = startUsn;

    constexpr size_t readBufferSize = 64ULL * 1024;
    auto* readBuffer =
        ShouldFailAlloc()
            ? nullptr
            : static_cast<uint8_t*>(VirtualAlloc(nullptr, readBufferSize, MEM_COMMIT | MEM_RESERVE, PAGE_READWRITE));
    if (readBuffer == nullptr) {
        SetErrorMessage(result->errorMessage, L"Failed to allocate read buffer");
        return result;
    }

    READ_USN_JOURNAL_DATA_V1 readData;
    readData.StartUsn = startUsn;
    readData.ReasonMask = 0xFFFFFFFF;
    readData.ReturnOnlyOnClose = 0;
    readData.Timeout = 0;
    readData.BytesToWaitFor = 1;
    readData.UsnJournalID = journalId;
    readData.MinMajorVersion = 2;
    readData.MaxMajorVersion = 2;

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

    if (success == 0) {
        DWORD error = GetLastError();
        if (error == ERROR_IO_PENDING) {
            success = UsnGetOverlappedResult(volumeHandle, &overlapped, &bytesReturned, TRUE);
            if (success == 0) {
                error = GetLastError();
                if (error == ERROR_OPERATION_ABORTED) {
                    CloseHandle(overlapped.hEvent);
                    VirtualFree(readBuffer, 0, MEM_RELEASE);
                    return result;
                }
            }
        }

        if (success == 0) {
            CloseHandle(overlapped.hEvent);
            VirtualFree(readBuffer, 0, MEM_RELEASE);
            ApplyUsnReadError(result, GetLastError(), L"FSCTL_READ_USN_JOURNAL watch failed");
            return result;
        }
    }

    CloseHandle(overlapped.hEvent);
    PopulateWatchEntries(result, readBuffer, bytesReturned);
    VirtualFree(readBuffer, 0, MEM_RELEASE);
    return result;
}

EXPORT BOOL CancelUsnJournalWatch(HANDLE volumeHandle) { return CancelIoEx(volumeHandle, nullptr); }
}

#endif  // _WIN32
