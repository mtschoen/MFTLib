# USN Journal Support Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Add NTFS USN journal reading to MFTLib so consumers can detect file changes since a previous scan without re-reading the entire MFT.

**Architecture:** Native C++ reads USN journal entries via `FSCTL_QUERY_USN_JOURNAL` and `FSCTL_READ_USN_JOURNAL` using the existing volume handle infrastructure. Managed C# wrapper exposes `UsnJournalEntry` records and a `UsnJournalCursor` for resumable reads. Consumers (file-wizard) pair this with a cached MFT index to apply incremental deltas.

**Tech Stack:** C++ (MFTLibNative DLL), C# .NET 8.0 (MFTLib), MSTest (MFTLib.Tests). Build with MSBuild -p:Platform=x64 (not dotnet build).

---

## File Structure

### Native C++ (MFTLibNative)

- **Modify: `MFTLibNative/mft_api.h`** - Add `UsnJournalEntry` and `UsnJournalResult` packed structs, matching the pattern of `MftFileEntry`/`MftParseResult`.
- **Modify: `MFTLibNative/dllmain.cpp`** - Add `QueryUsnJournal` and `ReadUsnJournal` exported functions. `QueryUsnJournal` returns journal metadata (ID, first/next USN). `ReadUsnJournal` reads entries since a given USN, returns them in a flat array like MFT parsing does.

### Managed C# (MFTLib)

- **Create: `MFTLib/UsnJournalEntry.cs`** - Public readonly struct with: RecordNumber, ParentRecordNumber, Usn, Timestamp, Reason (flags enum), FileName, FileAttributes.
- **Create: `MFTLib/UsnJournalCursor.cs`** - Public readonly struct with: JournalId (ulong), NextUsn (long). Serializable so consumers can persist it between runs.
- **Create: `MFTLib/UsnReason.cs`** - Public flags enum mapping Win32 USN_REASON_* constants.
- **Modify: `MFTLib/MFTLibNative.cs`** - Add P/Invoke declarations and swappable Func fields for `QueryUsnJournal` and `ReadUsnJournal`.
- **Modify: `MFTLib/MftVolume.cs`** - Add `QueryUsnJournal()` and `ReadUsnJournal(UsnJournalCursor? since)` public methods.
- **Modify: `MFTLib/Interop/MftParseResult.cs`** - Add `UsnJournalResult` interop struct (or create a new file if cleaner).

### Tests (MFTLib.Tests)

- **Create: `MFTLib.Tests/UsnJournalTests.cs`** - Unit tests using mocked native calls (same Func-swapping pattern as MockVolumeTests). Tests for: query returns cursor, read returns entries, read with stale journal ID throws, empty journal returns zero entries, reason flags parse correctly.
- **Create: `MFTLib.Tests/UsnJournalLiveTests.cs`** - Admin-required integration tests that read the real USN journal on the test machine. Skipped in CI via `[TestCategory("RequiresAdmin")]`. Tests for: query succeeds on real volume, create+delete a temp file then read journal and find the entry.

### Validation (file-wizard)

- **Modify: `file-wizard/FileWizard/FileDatabase.cs`** - After cache load, use USN journal to detect changes since the cache timestamp. Apply deltas (add new files, remove deleted, update renamed) instead of full rescan.
- **Modify: `file-wizard/FileWizard/FileWizardCache.cs`** - Persist `UsnJournalCursor` alongside the drive index so subsequent loads can resume from the right point.

---

## Task 1: Native USN structs and QueryUsnJournal export

**Files:**
- Modify: `MFTLibNative/mft_api.h`
- Modify: `MFTLibNative/dllmain.cpp`

- [ ] **Step 1: Add USN structs to mft_api.h**

Add after the `MftParseResult` struct, before `#pragma pack(pop)`:

```cpp
struct UsnJournalInfo {
    uint64_t journalId;
    int64_t  firstUsn;
    int64_t  nextUsn;
    int64_t  lowestValidUsn;
    int64_t  maxUsn;
    uint64_t maximumSize;
    uint64_t allocationDelta;
    wchar_t  errorMessage[256];
};

struct UsnJournalEntry {
    uint64_t recordNumber;        // file reference number (lower 48 bits)
    uint64_t parentRecordNumber;  // parent file reference number (lower 48 bits)
    int64_t  usn;                 // USN of this record
    int64_t  timestamp;           // FILETIME as int64
    uint32_t reason;              // USN_REASON_* flags
    uint32_t fileAttributes;      // Win32 FILE_ATTRIBUTE_* flags
    uint16_t fileNameLength;      // wchar_t count
    wchar_t  fileName[260];       // MAX_PATH, null-terminated
};

struct UsnJournalResult {
    uint64_t         entryCount;
    UsnJournalEntry* entries;        // array, owned by native side
    int64_t          nextUsn;        // cursor for next read
    uint64_t         journalId;      // journal ID for staleness detection
    wchar_t          errorMessage[256];
};
```

- [ ] **Step 2: Add QueryUsnJournal to dllmain.cpp**

Add in the `extern "C"` block, after `FreeMftResult`:

```cpp
EXPORT UsnJournalInfo* QueryUsnJournal(HANDLE volumeHandle) {
    auto* info = new UsnJournalInfo{};

    USN_JOURNAL_DATA_V1 journalData{};
    DWORD bytesReturned = 0;
    BOOL success = DeviceIoControl(
        volumeHandle,
        FSCTL_QUERY_USN_JOURNAL,
        nullptr, 0,
        &journalData, sizeof(journalData),
        &bytesReturned, nullptr);

    if (!success) {
        swprintf_s(info->errorMessage, 256, L"FSCTL_QUERY_USN_JOURNAL failed: error %lu", GetLastError());
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

EXPORT void FreeUsnJournalInfo(UsnJournalInfo* info) {
    delete info;
}
```

- [ ] **Step 3: Build to verify native compilation**

```bash
MSBuild.exe MFTLib.sln -p:Configuration=Release -p:Platform=x64 -v:minimal
```

Expected: builds with no errors. The new exports won't be called yet.

- [ ] **Step 4: Commit**

```bash
git add MFTLibNative/mft_api.h MFTLibNative/dllmain.cpp
git commit -m "Add native USN journal structs and QueryUsnJournal export"
```

---

## Task 2: Native ReadUsnJournal export

**Files:**
- Modify: `MFTLibNative/dllmain.cpp`

- [ ] **Step 1: Add ReadUsnJournal to dllmain.cpp**

Add after `FreeUsnJournalInfo`:

```cpp
EXPORT UsnJournalResult* ReadUsnJournal(HANDLE volumeHandle, int64_t startUsn, uint64_t journalId) {
    auto* result = new UsnJournalResult{};
    result->journalId = journalId;

    // Allocate read buffer — 64KB is a good balance for USN reads
    const DWORD bufferSize = 64 * 1024;
    auto* buffer = (uint8_t*)VirtualAlloc(nullptr, bufferSize, MEM_COMMIT | MEM_RESERVE, PAGE_READWRITE);
    if (!buffer) {
        swprintf_s(result->errorMessage, 256, L"Failed to allocate read buffer");
        return result;
    }

    // Initial capacity for entries
    uint64_t capacity = 4096;
    uint64_t count = 0;
    auto* entries = ShouldFailAlloc() ? nullptr : (UsnJournalEntry*)VirtualAlloc(
        nullptr, capacity * sizeof(UsnJournalEntry), MEM_COMMIT | MEM_RESERVE, PAGE_READWRITE);
    if (!entries) {
        VirtualFree(buffer, 0, MEM_RELEASE);
        swprintf_s(result->errorMessage, 256, L"Failed to allocate entry buffer");
        return result;
    }

    READ_USN_JOURNAL_DATA_V1 readData{};
    readData.StartUsn = startUsn;
    readData.ReasonMask = 0xFFFFFFFF; // all reasons
    readData.ReturnOnlyOnClose = 0;
    readData.Timeout = 0;
    readData.BytesToWaitFor = 0;
    readData.UsnJournalID = journalId;
    readData.MinMajorVersion = 2;
    readData.MaxMajorVersion = 2;

    for (;;) {
        DWORD bytesReturned = 0;
        BOOL success = DeviceIoControl(
            volumeHandle,
            FSCTL_READ_USN_JOURNAL,
            &readData, sizeof(readData),
            buffer, bufferSize,
            &bytesReturned, nullptr);

        if (!success) {
            DWORD err = GetLastError();
            if (err == ERROR_HANDLE_EOF || err == ERROR_WRITE_PROTECT) {
                // No more entries
                break;
            }
            if (err == ERROR_JOURNAL_NOT_ACTIVE || err == ERROR_JOURNAL_DELETE_IN_PROGRESS) {
                swprintf_s(result->errorMessage, 256, L"USN journal not active (error %lu)", err);
                break;
            }
            if (err == ERROR_JOURNAL_ENTRY_DELETED) {
                swprintf_s(result->errorMessage, 256, L"Requested USN has been overwritten — full rescan needed");
                break;
            }
            swprintf_s(result->errorMessage, 256, L"FSCTL_READ_USN_JOURNAL failed: error %lu", err);
            break;
        }

        if (bytesReturned <= sizeof(USN)) {
            // Only the next-USN header was returned, no records
            break;
        }

        // First 8 bytes of output is the next USN to use for continuation
        auto nextUsn = *(USN*)buffer;
        readData.StartUsn = nextUsn;

        // Walk variable-length USN_RECORD_V2 entries after the 8-byte USN header
        DWORD offset = sizeof(USN);
        while (offset < bytesReturned) {
            auto* record = (USN_RECORD_V2*)(buffer + offset);
            if (record->RecordLength == 0) break;

            // Grow entries array if needed
            if (count >= capacity) {
                uint64_t newCapacity = capacity * 2;
                auto* grown = (UsnJournalEntry*)VirtualAlloc(
                    nullptr, newCapacity * sizeof(UsnJournalEntry), MEM_COMMIT | MEM_RESERVE, PAGE_READWRITE);
                if (!grown) {
                    swprintf_s(result->errorMessage, 256, L"Failed to grow entry buffer");
                    goto done;
                }
                memcpy(grown, entries, count * sizeof(UsnJournalEntry));
                VirtualFree(entries, 0, MEM_RELEASE);
                entries = grown;
                capacity = newCapacity;
            }

            auto& entry = entries[count];
            memset(&entry, 0, sizeof(UsnJournalEntry));
            // Extract 48-bit record number (mask off sequence number in high 16 bits)
            entry.recordNumber = record->FileReferenceNumber & 0x0000FFFFFFFFFFFF;
            entry.parentRecordNumber = record->ParentFileReferenceNumber & 0x0000FFFFFFFFFFFF;
            entry.usn = record->Usn;
            entry.timestamp = record->TimeStamp.QuadPart;
            entry.reason = record->Reason;
            entry.fileAttributes = record->FileAttributes;

            // Copy filename from variable-length record
            auto* namePtr = (wchar_t*)((uint8_t*)record + record->FileNameOffset);
            uint16_t nameChars = record->FileNameLength / sizeof(wchar_t);
            uint16_t copyLen = min(nameChars, (uint16_t)259);
            entry.fileNameLength = copyLen;
            wmemcpy_s(entry.fileName, 260, namePtr, copyLen);
            entry.fileName[copyLen] = L'\0';

            count++;
            offset += record->RecordLength;
        }
    }

done:
    VirtualFree(buffer, 0, MEM_RELEASE);
    result->entryCount = count;
    result->entries = entries;
    result->nextUsn = readData.StartUsn;
    result->journalId = journalId;
    return result;
}

EXPORT void FreeUsnJournalResult(UsnJournalResult* result) {
    if (result) {
        if (result->entries)
            VirtualFree(result->entries, 0, MEM_RELEASE);
        delete result;
    }
}
```

- [ ] **Step 2: Build to verify**

```bash
MSBuild.exe MFTLib.sln -p:Configuration=Release -p:Platform=x64 -v:minimal
```

- [ ] **Step 3: Commit**

```bash
git add MFTLibNative/dllmain.cpp
git commit -m "Add native ReadUsnJournal and FreeUsnJournalResult exports"
```

---

## Task 3: Managed interop types (UsnReason, UsnJournalEntry, UsnJournalCursor)

**Files:**
- Create: `MFTLib/UsnReason.cs`
- Create: `MFTLib/UsnJournalEntry.cs`
- Create: `MFTLib/UsnJournalCursor.cs`

- [ ] **Step 1: Create UsnReason flags enum**

Create `MFTLib/UsnReason.cs`:

```csharp
namespace MFTLib;

[Flags]
public enum UsnReason : uint
{
    None = 0,
    DataOverwrite = 0x00000001,
    DataExtend = 0x00000002,
    DataTruncation = 0x00000004,
    NamedDataOverwrite = 0x00000010,
    NamedDataExtend = 0x00000020,
    NamedDataTruncation = 0x00000040,
    FileCreate = 0x00000100,
    FileDelete = 0x00000200,
    EaChange = 0x00000400,
    SecurityChange = 0x00000800,
    RenameOldName = 0x00001000,
    RenameNewName = 0x00002000,
    IndexableChange = 0x00004000,
    BasicInfoChange = 0x00008000,
    HardLinkChange = 0x00010000,
    CompressionChange = 0x00020000,
    EncryptionChange = 0x00040000,
    ObjectIdChange = 0x00080000,
    ReparsePointChange = 0x00100000,
    StreamChange = 0x00200000,
    TransactedChange = 0x00400000,
    IntegrityChange = 0x00800000,
    Close = 0x80000000,
}
```

- [ ] **Step 2: Create UsnJournalCursor**

Create `MFTLib/UsnJournalCursor.cs`:

```csharp
namespace MFTLib;

/// <summary>
/// Tracks position in a volume's USN journal for resumable reads.
/// Persist this between runs to enable incremental scanning.
/// </summary>
public readonly struct UsnJournalCursor
{
    /// <summary>USN journal instance ID. Changes if the journal is deleted and recreated.</summary>
    public ulong JournalId { get; }

    /// <summary>Next USN to read from. Pass this to ReadUsnJournal to resume.</summary>
    public long NextUsn { get; }

    public UsnJournalCursor(ulong journalId, long nextUsn)
    {
        JournalId = journalId;
        NextUsn = nextUsn;
    }
}
```

- [ ] **Step 3: Create UsnJournalEntry**

Create `MFTLib/UsnJournalEntry.cs`:

```csharp
namespace MFTLib;

public readonly struct UsnJournalEntry
{
    public ulong RecordNumber { get; }
    public ulong ParentRecordNumber { get; }
    public long Usn { get; }
    public DateTime Timestamp { get; }
    public UsnReason Reason { get; }
    public FileAttributes FileAttributes { get; }
    public string FileName { get; }

    public bool IsClose => (Reason & UsnReason.Close) != 0;
    public bool IsCreate => (Reason & UsnReason.FileCreate) != 0;
    public bool IsDelete => (Reason & UsnReason.FileDelete) != 0;
    public bool IsRename => (Reason & (UsnReason.RenameOldName | UsnReason.RenameNewName)) != 0;

    internal UsnJournalEntry(ulong recordNumber, ulong parentRecordNumber,
        long usn, long fileTimeTimestamp, uint reason, uint fileAttributes, string fileName)
    {
        RecordNumber = recordNumber;
        ParentRecordNumber = parentRecordNumber;
        Usn = usn;
        Timestamp = fileTimeTimestamp > 0
            ? DateTime.FromFileTimeUtc(fileTimeTimestamp)
            : DateTime.MinValue;
        Reason = (UsnReason)reason;
        FileAttributes = (FileAttributes)fileAttributes;
        FileName = fileName;
    }

    public override string ToString() => $"[{Reason}] {FileName} (record {RecordNumber})";
}
```

- [ ] **Step 4: Build to verify**

```bash
MSBuild.exe MFTLib.sln -p:Configuration=Release -p:Platform=x64 -v:minimal
```

- [ ] **Step 5: Commit**

```bash
git add MFTLib/UsnReason.cs MFTLib/UsnJournalEntry.cs MFTLib/UsnJournalCursor.cs
git commit -m "Add managed USN journal types: UsnReason, UsnJournalEntry, UsnJournalCursor"
```

---

## Task 4: Managed interop and MftVolume API

**Files:**
- Modify: `MFTLib/MFTLibNative.cs`
- Create: `MFTLib/Interop/UsnJournalInfo.cs`
- Create: `MFTLib/Interop/UsnJournalResult.cs`
- Modify: `MFTLib/MftVolume.cs`

- [ ] **Step 1: Add interop structs**

Create `MFTLib/Interop/UsnJournalInfo.cs`:

```csharp
using System.Runtime.InteropServices;

namespace MFTLib.Interop;

[StructLayout(LayoutKind.Sequential, Pack = 1, CharSet = CharSet.Unicode)]
struct UsnJournalInfoNative
{
    public ulong JournalId;
    public long FirstUsn;
    public long NextUsn;
    public long LowestValidUsn;
    public long MaxUsn;
    public ulong MaximumSize;
    public ulong AllocationDelta;

    [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 256)]
    public string ErrorMessage;
}
```

Create `MFTLib/Interop/UsnJournalResult.cs`:

```csharp
using System.Runtime.InteropServices;

namespace MFTLib.Interop;

[StructLayout(LayoutKind.Sequential, Pack = 1, CharSet = CharSet.Unicode)]
struct UsnJournalResultNative
{
    public ulong EntryCount;
    public IntPtr Entries; // UsnJournalEntry*, owned by native side
    public long NextUsn;
    public ulong JournalId;

    [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 256)]
    public string ErrorMessage;
}
```

- [ ] **Step 2: Add P/Invoke declarations to MFTLibNative.cs**

Add after the existing P/Invoke declarations:

```csharp
[DllImport(LibraryName, EntryPoint = "QueryUsnJournal", CallingConvention = CallingConvention.Cdecl)]
static extern IntPtr NativeQueryUsnJournal(SafeHandle volumeHandle);

[DllImport(LibraryName, EntryPoint = "FreeUsnJournalInfo", CallingConvention = CallingConvention.Cdecl)]
static extern void NativeFreeUsnJournalInfo(IntPtr info);

[DllImport(LibraryName, EntryPoint = "ReadUsnJournal", CallingConvention = CallingConvention.Cdecl)]
static extern IntPtr NativeReadUsnJournal(SafeHandle volumeHandle, long startUsn, ulong journalId);

[DllImport(LibraryName, EntryPoint = "FreeUsnJournalResult", CallingConvention = CallingConvention.Cdecl)]
static extern void NativeFreeUsnJournalResult(IntPtr result);
```

Add swappable Func fields:

```csharp
internal static Func<SafeHandle, IntPtr> QueryUsnJournal = NativeQueryUsnJournal;
internal static Action<IntPtr> FreeUsnJournalInfo = NativeFreeUsnJournalInfo;
internal static Func<SafeHandle, long, ulong, IntPtr> ReadUsnJournal = NativeReadUsnJournal;
internal static Action<IntPtr> FreeUsnJournalResult = NativeFreeUsnJournalResult;
```

Add resets in `ResetToDefaults()`:

```csharp
QueryUsnJournal = NativeQueryUsnJournal;
FreeUsnJournalInfo = NativeFreeUsnJournalInfo;
ReadUsnJournal = NativeReadUsnJournal;
FreeUsnJournalResult = NativeFreeUsnJournalResult;
```

- [ ] **Step 3: Add public API to MftVolume.cs**

Add before `Dispose()`:

```csharp
/// <summary>
/// Query the USN journal to get the current cursor position.
/// Use this after a full MFT scan to establish a baseline for incremental updates.
/// </summary>
public UsnJournalCursor QueryUsnJournal()
{
    ObjectDisposedException.ThrowIf(_disposed, this);
    var infoPtr = MFTLibNative.QueryUsnJournal(_volumeHandle);
    if (infoPtr == IntPtr.Zero)
        throw new InvalidOperationException("QueryUsnJournal returned null");

    try
    {
        var info = Marshal.PtrToStructure<Interop.UsnJournalInfoNative>(infoPtr);
        if (!string.IsNullOrEmpty(info.ErrorMessage))
            throw new InvalidOperationException(info.ErrorMessage);

        return new UsnJournalCursor(info.JournalId, info.NextUsn);
    }
    finally
    {
        MFTLibNative.FreeUsnJournalInfo(infoPtr);
    }
}

/// <summary>
/// Read USN journal entries since the given cursor.
/// Returns entries and an updated cursor for the next call.
/// If the journal has been deleted/recreated (journalId mismatch) or entries
/// have been overwritten, throws InvalidOperationException — caller should
/// fall back to a full MFT rescan.
/// </summary>
public (UsnJournalEntry[] Entries, UsnJournalCursor UpdatedCursor) ReadUsnJournal(UsnJournalCursor since)
{
    ObjectDisposedException.ThrowIf(_disposed, this);
    var resultPtr = MFTLibNative.ReadUsnJournal(_volumeHandle, since.NextUsn, since.JournalId);
    if (resultPtr == IntPtr.Zero)
        throw new InvalidOperationException("ReadUsnJournal returned null");

    try
    {
        var result = Marshal.PtrToStructure<Interop.UsnJournalResultNative>(resultPtr);
        if (!string.IsNullOrEmpty(result.ErrorMessage))
            throw new InvalidOperationException(result.ErrorMessage);

        var entries = MarshalUsnEntries(result);
        var updatedCursor = new UsnJournalCursor(result.JournalId, result.NextUsn);
        return (entries, updatedCursor);
    }
    finally
    {
        MFTLibNative.FreeUsnJournalResult(resultPtr);
    }
}

// Native UsnJournalEntry layout (pack 1):
//   recordNumber(8) + parentRecordNumber(8) + usn(8) + timestamp(8) +
//   reason(4) + fileAttributes(4) + fileNameLength(2) + fileName(260*2=520)
//   = 562 bytes
internal const int NativeUsnEntrySize = 562;

static unsafe UsnJournalEntry[] MarshalUsnEntries(Interop.UsnJournalResultNative result)
{
    var count = (int)result.EntryCount;
    var entries = new UsnJournalEntry[count];
    var basePtr = (byte*)result.Entries;

    for (var i = 0; i < count; i++)
    {
        var ptr = basePtr + i * NativeUsnEntrySize;
        var recordNumber = *(ulong*)ptr;
        var parentRecordNumber = *(ulong*)(ptr + 8);
        var usn = *(long*)(ptr + 16);
        var timestamp = *(long*)(ptr + 24);
        var reason = *(uint*)(ptr + 32);
        var fileAttributes = *(uint*)(ptr + 36);
        var fileNameLength = *(ushort*)(ptr + 40);
        var fileName = new string((char*)(ptr + 42), 0, fileNameLength);

        entries[i] = new UsnJournalEntry(recordNumber, parentRecordNumber,
            usn, timestamp, reason, fileAttributes, fileName);
    }

    return entries;
}
```

Add `using System.Runtime.InteropServices;` to the top of MftVolume.cs if not already there.

- [ ] **Step 4: Build to verify**

```bash
MSBuild.exe MFTLib.sln -p:Configuration=Release -p:Platform=x64 -v:minimal
```

- [ ] **Step 5: Commit**

```bash
git add MFTLib/MFTLibNative.cs MFTLib/Interop/UsnJournalInfo.cs MFTLib/Interop/UsnJournalResult.cs MFTLib/MftVolume.cs
git commit -m "Add managed USN journal interop and MftVolume.QueryUsnJournal/ReadUsnJournal API"
```

---

## Task 5: Unit tests with mocked native calls

**Files:**
- Create: `MFTLib.Tests/UsnJournalTests.cs`

- [ ] **Step 1: Write tests for QueryUsnJournal**

Create `MFTLib.Tests/UsnJournalTests.cs`:

```csharp
using System.Runtime.InteropServices;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using Microsoft.Win32.SafeHandles;
using MFTLib;
using MFTLib.Interop;

namespace MFTLib.Tests;

[TestClass]
public class UsnJournalTests
{
    [TestCleanup]
    public void Cleanup()
    {
        MFTLibNative.ResetToDefaults();
        FileUtilities.ResetToDefaults();
    }

    static SafeFileHandle FakeHandle() => new(new IntPtr(1), ownsHandle: false);

    [TestMethod]
    public void QueryUsnJournal_ReturnsValidCursor()
    {
        var info = new UsnJournalInfoNative
        {
            JournalId = 0x123456789ABCDEF0,
            FirstUsn = 0,
            NextUsn = 42000,
            LowestValidUsn = 100,
            MaxUsn = long.MaxValue,
            MaximumSize = 32 * 1024 * 1024,
            AllocationDelta = 4 * 1024 * 1024,
        };

        var infoPtr = Marshal.AllocHGlobal(Marshal.SizeOf<UsnJournalInfoNative>());
        Marshal.StructureToPtr(info, infoPtr, false);

        FileUtilities.GetVolumeHandle = _ => FakeHandle();
        MFTLibNative.QueryUsnJournal = _ => infoPtr;
        MFTLibNative.FreeUsnJournalInfo = _ => Marshal.FreeHGlobal(infoPtr);
        // Stub out ParseMFTRecords so Open doesn't fail
        MFTLibNative.ParseMFTRecords = (_, _, _, _) => IntPtr.Zero;

        using var volume = MftVolume.Open("C");
        var cursor = volume.QueryUsnJournal();

        Assert.AreEqual(0x123456789ABCDEF0UL, cursor.JournalId);
        Assert.AreEqual(42000L, cursor.NextUsn);
    }

    [TestMethod]
    public void QueryUsnJournal_WithError_Throws()
    {
        var info = new UsnJournalInfoNative
        {
            ErrorMessage = "FSCTL_QUERY_USN_JOURNAL failed: error 5",
        };

        var infoPtr = Marshal.AllocHGlobal(Marshal.SizeOf<UsnJournalInfoNative>());
        Marshal.StructureToPtr(info, infoPtr, false);

        FileUtilities.GetVolumeHandle = _ => FakeHandle();
        MFTLibNative.QueryUsnJournal = _ => infoPtr;
        MFTLibNative.FreeUsnJournalInfo = _ => Marshal.FreeHGlobal(infoPtr);

        using var volume = MftVolume.Open("C");
        var exception = Assert.ThrowsException<InvalidOperationException>(() => volume.QueryUsnJournal());
        Assert.IsTrue(exception.Message.Contains("error 5"));
    }

    [TestMethod]
    public void QueryUsnJournal_NullPointer_Throws()
    {
        FileUtilities.GetVolumeHandle = _ => FakeHandle();
        MFTLibNative.QueryUsnJournal = _ => IntPtr.Zero;

        using var volume = MftVolume.Open("C");
        Assert.ThrowsException<InvalidOperationException>(() => volume.QueryUsnJournal());
    }
}
```

- [ ] **Step 2: Run tests to verify they pass**

```bash
MSBuild.exe MFTLib.sln -p:Configuration=Release -p:Platform=x64 -v:minimal
dotnet test MFTLib.Tests/MFTLib.Tests.csproj -c Release -p:Platform=x64 --no-build --filter "UsnJournal"
```

- [ ] **Step 3: Write tests for ReadUsnJournal**

Add to `UsnJournalTests.cs`:

```csharp
[TestMethod]
public unsafe void ReadUsnJournal_ReturnsEntries()
{
    const int entrySize = MftVolume.NativeUsnEntrySize;
    var entryCount = 2;
    var entriesSize = entrySize * entryCount;
    var entriesPtr = Marshal.AllocHGlobal(entriesSize);
    new Span<byte>((void*)entriesPtr, entriesSize).Clear();

    // Entry 0: file create
    var ptr = (byte*)entriesPtr;
    *(ulong*)ptr = 100;                 // recordNumber
    *(ulong*)(ptr + 8) = 5;            // parentRecordNumber
    *(long*)(ptr + 16) = 1000;         // usn
    *(long*)(ptr + 24) = 0;            // timestamp (0 = DateTime.MinValue)
    *(uint*)(ptr + 32) = 0x00000100;   // reason = FileCreate
    *(uint*)(ptr + 36) = 0x20;         // fileAttributes = Archive
    var name0 = "newfile.txt";
    *(ushort*)(ptr + 40) = (ushort)name0.Length;
    name0.AsSpan().CopyTo(new Span<char>(ptr + 42, name0.Length));

    // Entry 1: file delete
    ptr = (byte*)entriesPtr + entrySize;
    *(ulong*)ptr = 200;
    *(ulong*)(ptr + 8) = 5;
    *(long*)(ptr + 16) = 2000;
    *(long*)(ptr + 24) = 0;
    *(uint*)(ptr + 32) = 0x00000200;   // reason = FileDelete
    *(uint*)(ptr + 36) = 0;
    var name1 = "deleted.txt";
    *(ushort*)(ptr + 40) = (ushort)name1.Length;
    name1.AsSpan().CopyTo(new Span<char>(ptr + 42, name1.Length));

    var nativeResult = new UsnJournalResultNative
    {
        EntryCount = 2,
        Entries = entriesPtr,
        NextUsn = 3000,
        JournalId = 0xABCD,
    };

    var resultPtr = Marshal.AllocHGlobal(Marshal.SizeOf<UsnJournalResultNative>());
    Marshal.StructureToPtr(nativeResult, resultPtr, false);

    FileUtilities.GetVolumeHandle = _ => FakeHandle();
    MFTLibNative.ReadUsnJournal = (_, _, _) => resultPtr;
    MFTLibNative.FreeUsnJournalResult = _ =>
    {
        Marshal.FreeHGlobal(entriesPtr);
        Marshal.FreeHGlobal(resultPtr);
    };

    using var volume = MftVolume.Open("C");
    var cursor = new UsnJournalCursor(0xABCD, 500);
    var (entries, updatedCursor) = volume.ReadUsnJournal(cursor);

    Assert.AreEqual(2, entries.Length);
    Assert.AreEqual(100UL, entries[0].RecordNumber);
    Assert.AreEqual("newfile.txt", entries[0].FileName);
    Assert.IsTrue(entries[0].IsCreate);
    Assert.AreEqual(200UL, entries[1].RecordNumber);
    Assert.AreEqual("deleted.txt", entries[1].FileName);
    Assert.IsTrue(entries[1].IsDelete);
    Assert.AreEqual(3000L, updatedCursor.NextUsn);
    Assert.AreEqual(0xABCDUL, updatedCursor.JournalId);
}

[TestMethod]
public void ReadUsnJournal_EmptyJournal_ReturnsEmptyArray()
{
    var nativeResult = new UsnJournalResultNative
    {
        EntryCount = 0,
        Entries = IntPtr.Zero,
        NextUsn = 500,
        JournalId = 0xABCD,
    };

    var resultPtr = Marshal.AllocHGlobal(Marshal.SizeOf<UsnJournalResultNative>());
    Marshal.StructureToPtr(nativeResult, resultPtr, false);

    FileUtilities.GetVolumeHandle = _ => FakeHandle();
    MFTLibNative.ReadUsnJournal = (_, _, _) => resultPtr;
    MFTLibNative.FreeUsnJournalResult = _ => Marshal.FreeHGlobal(resultPtr);

    using var volume = MftVolume.Open("C");
    var (entries, updatedCursor) = volume.ReadUsnJournal(new UsnJournalCursor(0xABCD, 500));

    Assert.AreEqual(0, entries.Length);
    Assert.AreEqual(500L, updatedCursor.NextUsn);
}

[TestMethod]
public void ReadUsnJournal_WithError_Throws()
{
    var nativeResult = new UsnJournalResultNative
    {
        ErrorMessage = "Requested USN has been overwritten",
    };

    var resultPtr = Marshal.AllocHGlobal(Marshal.SizeOf<UsnJournalResultNative>());
    Marshal.StructureToPtr(nativeResult, resultPtr, false);

    FileUtilities.GetVolumeHandle = _ => FakeHandle();
    MFTLibNative.ReadUsnJournal = (_, _, _) => resultPtr;
    MFTLibNative.FreeUsnJournalResult = _ => Marshal.FreeHGlobal(resultPtr);

    using var volume = MftVolume.Open("C");
    var exception = Assert.ThrowsException<InvalidOperationException>(
        () => volume.ReadUsnJournal(new UsnJournalCursor(0xABCD, 500)));
    Assert.IsTrue(exception.Message.Contains("overwritten"));
}

[TestMethod]
public void ReadUsnJournal_NullPointer_Throws()
{
    FileUtilities.GetVolumeHandle = _ => FakeHandle();
    MFTLibNative.ReadUsnJournal = (_, _, _) => IntPtr.Zero;

    using var volume = MftVolume.Open("C");
    Assert.ThrowsException<InvalidOperationException>(
        () => volume.ReadUsnJournal(new UsnJournalCursor(0xABCD, 500)));
}

[TestMethod]
public void UsnJournalEntry_ReasonHelpers_Work()
{
    var create = new UsnJournalEntry(1, 5, 100, 0, (uint)UsnReason.FileCreate, 0, "test.txt");
    Assert.IsTrue(create.IsCreate);
    Assert.IsFalse(create.IsDelete);
    Assert.IsFalse(create.IsClose);
    Assert.IsFalse(create.IsRename);

    var delete = new UsnJournalEntry(1, 5, 200, 0, (uint)(UsnReason.FileDelete | UsnReason.Close), 0, "gone.txt");
    Assert.IsTrue(delete.IsDelete);
    Assert.IsTrue(delete.IsClose);

    var rename = new UsnJournalEntry(1, 5, 300, 0, (uint)UsnReason.RenameNewName, 0, "renamed.txt");
    Assert.IsTrue(rename.IsRename);
}

[TestMethod]
public void UsnJournalEntry_ToString_IncludesReasonAndName()
{
    var entry = new UsnJournalEntry(42, 5, 100, 0, (uint)UsnReason.FileCreate, 0, "test.txt");
    var text = entry.ToString();
    Assert.IsTrue(text.Contains("FileCreate"));
    Assert.IsTrue(text.Contains("test.txt"));
    Assert.IsTrue(text.Contains("42"));
}

[TestMethod]
public void UsnJournalCursor_StoresValues()
{
    var cursor = new UsnJournalCursor(0xDEADBEEF, 12345);
    Assert.AreEqual(0xDEADBEEFUL, cursor.JournalId);
    Assert.AreEqual(12345L, cursor.NextUsn);
}
```

- [ ] **Step 4: Run all tests**

```bash
MSBuild.exe MFTLib.sln -p:Configuration=Release -p:Platform=x64 -v:minimal
dotnet test MFTLib.Tests/MFTLib.Tests.csproj -c Release -p:Platform=x64 --no-build
```

Expected: all existing tests still pass + new USN tests pass.

- [ ] **Step 5: Commit**

```bash
git add MFTLib.Tests/UsnJournalTests.cs
git commit -m "Add USN journal unit tests with mocked native calls"
```

---

## Task 6: Live integration tests (admin-required)

**Files:**
- Create: `MFTLib.Tests/UsnJournalLiveTests.cs`

- [ ] **Step 1: Write live integration tests**

Create `MFTLib.Tests/UsnJournalLiveTests.cs`:

```csharp
using Microsoft.VisualStudio.TestTools.UnitTesting;
using MFTLib;

namespace MFTLib.Tests;

[TestClass]
public class UsnJournalLiveTests
{
    static bool IsAdmin() => ElevationUtilities.IsElevated();

    [TestMethod]
    [TestCategory("RequiresAdmin")]
    public void QueryUsnJournal_OnRealVolume_ReturnsCursor()
    {
        if (!IsAdmin()) { Assert.Inconclusive("Requires admin"); return; }

        using var volume = MftVolume.Open("C");
        var cursor = volume.QueryUsnJournal();

        Assert.IsTrue(cursor.JournalId > 0, "JournalId should be nonzero");
        Assert.IsTrue(cursor.NextUsn > 0, "NextUsn should be positive");
    }

    [TestMethod]
    [TestCategory("RequiresAdmin")]
    public void ReadUsnJournal_AfterTempFileCreate_ContainsEntry()
    {
        if (!IsAdmin()) { Assert.Inconclusive("Requires admin"); return; }

        using var volume = MftVolume.Open("C");

        // Get current cursor
        var cursor = volume.QueryUsnJournal();

        // Create and delete a temp file to generate journal entries
        var tempPath = Path.Combine(Path.GetTempPath(), $"mftlib-usn-test-{Guid.NewGuid()}.tmp");
        File.WriteAllText(tempPath, "usn test");
        var tempFileName = Path.GetFileName(tempPath);

        try
        {
            // Read journal since our cursor
            var (entries, updatedCursor) = volume.ReadUsnJournal(cursor);

            Assert.IsTrue(entries.Length > 0, "Should have at least one journal entry");
            Assert.IsTrue(updatedCursor.NextUsn >= cursor.NextUsn, "Cursor should advance");

            // Find our temp file in the entries
            var found = entries.Any(e => e.FileName.Equals(tempFileName, StringComparison.OrdinalIgnoreCase));
            Assert.IsTrue(found, $"Should find {tempFileName} in USN journal entries");
        }
        finally
        {
            File.Delete(tempPath);
        }
    }

    [TestMethod]
    [TestCategory("RequiresAdmin")]
    public void ReadUsnJournal_CurrentPosition_ReturnsEmptyOrFew()
    {
        if (!IsAdmin()) { Assert.Inconclusive("Requires admin"); return; }

        using var volume = MftVolume.Open("C");
        var cursor = volume.QueryUsnJournal();

        // Reading from current position should return very few entries
        // (only those generated between QueryUsnJournal and ReadUsnJournal)
        var (entries, _) = volume.ReadUsnJournal(cursor);

        // We can't assert exactly 0 because background system activity may
        // generate entries, but it should be a small number
        Assert.IsTrue(entries.Length < 1000,
            $"Expected few entries from current position, got {entries.Length}");
    }
}
```

- [ ] **Step 2: Run non-admin tests to verify nothing broke**

```bash
MSBuild.exe MFTLib.sln -p:Configuration=Release -p:Platform=x64 -v:minimal
dotnet test MFTLib.Tests/MFTLib.Tests.csproj -c Release -p:Platform=x64 --no-build
```

- [ ] **Step 3: Run admin tests if elevated** (manual step — requires UAC)

```bash
.\TestProgram\bin\x64\Release\net8.0\TestProgram.exe  # verify elevation works
dotnet test MFTLib.Tests/MFTLib.Tests.csproj -c Release -p:Platform=x64 --no-build --filter "TestCategory=RequiresAdmin"
```

- [ ] **Step 4: Commit**

```bash
git add MFTLib.Tests/UsnJournalLiveTests.cs
git commit -m "Add live USN journal integration tests (admin-required)"
```

---

## Task 7: file-wizard integration

**Files:**
- Modify: `C:\Users\mtsch\file-wizard\FileWizard\FileWizardCache.cs`
- Modify: `C:\Users\mtsch\file-wizard\FileWizard\FileDatabase.cs`
- Modify: `C:\Users\mtsch\file-wizard\FileWizard\FileWizard.csproj`

This task is in the **file-wizard** repo, not MFTLib. It validates the MFTLib API works end-to-end.

- [ ] **Step 1: Update MFTLib NuGet reference**

In `file-wizard/FileWizard/FileWizard.csproj`, update the MFTLib PackageReference to use a local project reference during development (or update the version once published). For local dev:

```xml
<!-- Temporarily swap NuGet ref for project ref during USN development -->
<ProjectReference Include="..\..\MFTLib\MFTLib\MFTLib.csproj" />
```

- [ ] **Step 2: Add UsnJournalCursor to CachedDriveIndex**

In `FileWizardCache.cs`, the `CachedDriveIndex` class needs a cursor field. Find the class definition and add:

```csharp
public ulong UsnJournalId { get; set; }
public long UsnNextUsn { get; set; }
```

Update `FormatVersion` to 2 to invalidate old caches. Update `SaveDriveIndex` to write the two new fields. Update `TryLoadDriveIndex` to read them (with version 1 fallback returning 0 for both).

- [ ] **Step 3: Save cursor after MFT scan in FileDatabase.cs**

After `ScanWithMFT` succeeds, query the USN journal and store the cursor on the drive metadata or pass it through to the cache save. In the cache save section, include the cursor values.

- [ ] **Step 4: Add incremental update path in FileDatabase.Refresh**

After cache load succeeds (`cacheLoaded == true`), before starting content dedup:

```csharp
// Try incremental USN update if cache has a valid cursor
if (cacheLoaded && cachedIndex.UsnJournalId > 0)
{
    try
    {
        var cursor = new UsnJournalCursor(cachedIndex.UsnJournalId, cachedIndex.UsnNextUsn);
        using var volume = MftVolume.Open(driveLetter);
        var (entries, updatedCursor) = volume.ReadUsnJournal(cursor);

        _currentStatus = $"Applying {entries.Length} USN journal updates";
        // Apply deltas...
        // For FileCreate/RenameNewName: add/update the file in the index
        // For FileDelete: remove from the index
        // Save updated cursor
    }
    catch (InvalidOperationException)
    {
        // Journal wrapped or recreated — need full rescan
        cacheLoaded = false;
    }
}
```

The exact implementation depends on file-wizard's indexing strategy — the key validation is that `MftVolume.QueryUsnJournal()` and `ReadUsnJournal()` work correctly on a real volume and return meaningful entries.

- [ ] **Step 5: Manual validation**

1. Run file-wizard, let it do a full MFT scan (builds cache + saves cursor)
2. Create/delete/rename some files
3. Run file-wizard again — should load cache, read USN journal, and apply deltas instead of full rescan
4. Verify the changed files appear correctly in the index

- [ ] **Step 6: Commit in file-wizard repo**

```bash
cd C:\Users\mtsch\file-wizard
git add -A
git commit -m "Integrate MFTLib USN journal for incremental index updates"
```

---

## Task 8: Version bump and release prep

**Files:**
- Modify: `MFTLib/MFTLib.csproj` (version bump)
- Modify: `CLAUDE.md` (update roadmap reference)
- Modify: `.plan` (mark USN journal as done)
- Modify: `TEST-REPORT.md` (update test counts)

- [ ] **Step 1: Bump version to 0.3.0**

In `MFTLib/MFTLib.csproj`, change:
```xml
<Version>0.3.0</Version>
```

- [ ] **Step 2: Update .plan**

Mark the USN journal section as complete. Move it to a "Done" section or remove it.

- [ ] **Step 3: Update TEST-REPORT.md**

Run the full test suite and update the test counts and git hash.

- [ ] **Step 4: Run release dry run**

```powershell
.\scripts\release.ps1
```

Verify: builds, tests pass (coverage gate), NuGet package created.

- [ ] **Step 5: Commit**

```bash
git add MFTLib/MFTLib.csproj CLAUDE.md .plan TEST-REPORT.md
git commit -m "Bump version to 0.3.0 for USN journal release"
```

- [ ] **Step 6: Publish**

```powershell
.\scripts\release.ps1 -Publish
```
