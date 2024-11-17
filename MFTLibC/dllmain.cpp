#include "pch.h"

#include <iostream>

#include "MFTFileRecord.h"
#include "NativeStructs.h"

#define EXPORT __declspec(dllexport)

BOOL APIENTRY DllMain(HMODULE hModule, DWORD  ul_reason_for_call, LPVOID lpReserved) {
    switch (ul_reason_for_call)
    {
    case DLL_PROCESS_ATTACH:
    case DLL_THREAD_ATTACH:
    case DLL_THREAD_DETACH:
    case DLL_PROCESS_DETACH:
        break;
    }
    return TRUE;
}

static MFTFileRecord ParseFileRecord(void* fileRecordPtr)
{
    FileRecordHeader fileRecord = *(FileRecordHeader*)fileRecordPtr;

#if _DEBUG
    // Ensure that the first 4 bytes spell FILE
    auto magicNumber = fileRecord.MagicNumber;
    if (magicNumber[0] != 'F' || magicNumber[1] != 'I' || magicNumber[2] != 'L' || magicNumber[3] != 'E')
    {
        // TODO: Handle error
        wprintf(L"File record fails magic number check (first 4 bytes should spell FILE)\n");
        return MFTFileRecord();
        //throw new ArgumentException("File record fails magic number check (first 4 bytes should spell FILE)");
    }
#endif

    //Console.WriteLine("FileRecordHeader:");
    //Console.WriteLine($"  UpdateSequenceOffset: {fileRecord.UpdateSequenceOffset}");
    //Console.WriteLine($"  UpdateSequenceSize: {fileRecord.UpdateSequenceSize}");
    //Console.WriteLine($"  LogFileSequenceNumber: {fileRecord.LogFileSequenceNumber}");
    //Console.WriteLine($"  SequenceNumber: {fileRecord.SequenceNumber}");
    //Console.WriteLine($"  HardLinkCount: {fileRecord.HardLinkCount}");
    //Console.WriteLine($"  FirstAttributeOffset: {fileRecord.FirstAttributeOffset}");
    //Console.WriteLine($"  Flags: {fileRecord.Flags}");
    //Console.WriteLine($"  UsedSize: {fileRecord.RealSize}");
    //Console.WriteLine($"  AllocatedSize: {fileRecord.AllocatedSize}");
    //Console.WriteLine($"  FileReferenceToBaseRecord: {fileRecord.BaseFileRecord}");
    //Console.WriteLine($"  NextAttributeId: {fileRecord.NextAttributeId}");

    auto attributeOffset = fileRecord.FirstAttributeOffset;
    auto end = fileRecord.AllocatedSize;
    auto mftFileRecord = MFTFileRecord();
    while (attributeOffset < end)
    {
        auto attributePtr = (uint8_t*)fileRecordPtr + attributeOffset;

        // Read the attribute type before we read the header in case the memory after the end marker isn't readable
        auto attributeId = *(AttributeType*)attributePtr;
        if (attributeId == EndMarker)
        {
            break;
        }

        auto attributeHeader = (AttributeHeader*)attributePtr;
        switch (attributeId)
        {
        case StandardInformation:
            //Console.WriteLine($"StandardInformation at offset {attributeOffset}");
            //attributeOffset += 0x4; // Skip the attribute header
            //auto standardInformation = Marshal.PtrToStructure<StandardInformationAttribute>(fileRecordPtr + attributeOffset);
            //attributeOffset += 0x48; // Skip to the next attribute
            break;
        case AttributeList:
            //Console.WriteLine($"AttributeList at offset {attributeOffset}");
            //attributeOffset += 4; // Skip the attribute
            break;
        case FileName:
        {
            //Console.WriteLine($"FileName at offset {attributeOffset}");
            auto fileNameAttributePtr = attributePtr + attributeHeader->AttributeOffset;
            auto fileNameAttribute = (FileNameAttribute*)fileNameAttributePtr;
            mftFileRecord.FileName = (LPCWSTR)&fileNameAttribute->FileName;
            //Console.WriteLine($"File name is: {mftFileRecord.FileName}");
            break;
        }
        case ObjectId:
        {
            //Console.WriteLine($"ObjectId at offset {attributeOffset}");
            auto objAttributePtr = attributePtr + attributeHeader->AttributeOffset;
            auto objectAttribute = (ObjectIdAttribute*)objAttributePtr;
            mftFileRecord.Guid = objectAttribute->ObjectId;
            //Console.WriteLine($"Object Id is: {guid}");
            break;
        }
        case SecurityDescriptor:
            //Console.WriteLine($"SecurityDescriptor at offset {attributeOffset}");
            //attributeOffset += 4; // Skip the attribute
            break;
        case VolumeName:
            //Console.WriteLine($"VolumeName at offset {attributeOffset}");
            //attributeOffset += 4; // Skip the attribute
            break;
        case VolumeInformation:
            //Console.WriteLine($"VolumeInformation at offset {attributeOffset}");
            //attributeOffset += 4; // Skip the attribute
            break;
        case Data:
            //Console.WriteLine($"Data at offset {attributeOffset}");
            //attributeOffset += 4; // Skip the attribute
            break;
        case IndexRoot:
            //Console.WriteLine($"IndexRoot at offset {attributeOffset}");
            //attributeOffset += 4; // Skip the attribute
            break;
        case IndexAllocation:
            //Console.WriteLine($"IndexAllocation at offset {attributeOffset}");
            //attributeOffset += 4; // Skip the attribute
            break;
        case Bitmap:
            //Console.WriteLine($"Bitmap at offset {attributeOffset}");
            //attributeOffset += 4; // Skip the attribute
            break;
        case ReparsePoint:
            //Console.WriteLine($"ReparsePoint at offset {attributeOffset}");
            //attributeOffset += 4; // Skip the attribute
            break;
        case EAInformation:
            //Console.WriteLine($"EAInformation at offset {attributeOffset}");
            //attributeOffset += 4; // Skip the attribute
            break;
        case EA:
            //Console.WriteLine($"EA at offset {attributeOffset}");
            //attributeOffset += 4; // Skip the attribute
            break;
        case PropertySet:
            //Console.WriteLine($"PropertySet at offset {attributeOffset}");
            //attributeOffset += 4; // Skip the attribute
            break;
        case LoggedUtilityStream:
            //Console.WriteLine($"LoggedUtilityStream at offset {attributeOffset}");
            //attributeOffset += 4; // Skip the attribute
            break;
        default:
            //throw new ArgumentOutOfRangeException($"Unknown attribute type {attributeId}");
            break;
        }

        attributeOffset += attributeHeader->Length;
    }

    //Console.WriteLine($"Parsing file record with size {fileRecord.AllocatedSize}");
    //if (string.IsNullOrEmpty(fileName))
    //    throw new InvalidOperationException($"Could not get filename for FileRecord at {fileRecordPtr}");

    return mftFileRecord;
}


#pragma pack(push, 1)
struct NTFS_BootSector {
    BYTE jump[3];
    BYTE oemID[8];
    WORD bytesPerSector;
    BYTE sectorsPerCluster;
    WORD reservedSectors;
    BYTE reserved1[3];
    WORD notUsed1;
    BYTE mediaDescriptor;
    WORD notUsed2;
    WORD sectorsPerTrack;
    WORD numberOfHeads;
    DWORD hiddenSectors;
    DWORD notUsed3;
    DWORD notUsed4;
    ULONGLONG totalSectors;
    ULONGLONG mftCluster;
    ULONGLONG mftMirrorCluster;
    BYTE clustersPerFileRecordSegment;
    BYTE reserved2[3];
    BYTE clustersPerIndexBuffer;
    BYTE reserved3[3];
    ULONGLONG volumeSerialNumber;
    DWORD checksum;
    BYTE bootCode[426];
    WORD endOfSectorMarker;
};
#pragma pack(pop)

ULONGLONG GetMFTOffset(HANDLE volumeHandle) {
    NTFS_BootSector bootSector;
    DWORD bytesRead;
    if (!ReadFile(volumeHandle, &bootSector, sizeof(bootSector), &bytesRead, NULL) || bytesRead != sizeof(bootSector)) {
        std::cerr << "Failed to read boot sector" << std::endl;
        return 0;
    }

    // Calculate the MFT offset in bytes
    ULONGLONG mftOffset = bootSector.mftCluster * bootSector.bytesPerSector * bootSector.sectorsPerCluster;
    return mftOffset;
}

extern "C" {
    EXPORT void ExtractMFTRecords(HANDLE volumeHandle, MFTFileRecord* files, ULONG fileCount) {
        // Seek to the MFT location
        LARGE_INTEGER mftOffset;
        mftOffset.QuadPart = GetMFTOffset(volumeHandle);
        wprintf(L"MFT Offset: %llu\n", mftOffset.QuadPart);
        if (SetFilePointerEx(volumeHandle, mftOffset, NULL, FILE_BEGIN) == 0) {
            std::cerr << "Failed to seek to MFT" << std::endl;
            CloseHandle(volumeHandle);
            return;
        }

        // Read the MFT
        // This is a simplified example and may require additional error handling and logic
        char buffer[1024 * 512];
        DWORD bytesRead;
        long count = 0;
        while (ReadFile(volumeHandle, buffer, sizeof(buffer), &bytesRead, NULL)) {
            // Process the MFT entries
            // This is a simplified example
            //MFTEntry entry;
            //memcpy(entry.fileName, buffer, 256);
            //entry.fileSize = *reinterpret_cast<uint64_t*>(buffer + 256);
            //entries.push_back(entry);

            wprintf(L"\rProgresssssss: %d / %d", bytesRead, fileCount);
        }

        CloseHandle(volumeHandle);
    }
}
