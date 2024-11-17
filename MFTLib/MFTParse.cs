using System.Diagnostics;
using System.Reflection.Metadata;
using System.Runtime.InteropServices;

namespace MFTLib;

public class MFTParse
{
    static readonly int k_OffsetToFileRecordBuffer = Marshal.OffsetOf<NTFS_FILE_RECORD_OUTPUT_BUFFER>(nameof(NTFS_FILE_RECORD_OUTPUT_BUFFER.FileRecordBuffer)).ToInt32();
    static readonly int k_OffsetToFileNameUnicode = Marshal.OffsetOf<FileNameAttribute>(nameof(FileNameAttribute.FileName)).ToInt32();

    // ReSharper disable InconsistentNaming
    const uint GENERIC_READ = 0x80000000;
    const uint OPEN_EXISTING = 3;
    const uint FILE_SHARE_READ = 0x00000001;
    const uint FILE_SHARE_WRITE = 0x00000002;
    const uint FSCTL_GET_NTFS_VOLUME_DATA = 0x00090064;
    const uint FSCTL_GET_NTFS_FILE_RECORD = 0x00090068;
    // ReSharper restore InconsistentNaming

    public static MFTNode GetMFTNode(string volume)
    {
        if (string.IsNullOrEmpty(volume))
        {
            throw new ArgumentException("Volume name cannot be null or empty", nameof(volume));
        }

#if DEBUG
        var stopwatch = new Stopwatch();
        stopwatch.Start();
#endif

        var volumeHandle = Kernel32.CreateFile(
            volume,
            GENERIC_READ,
            FILE_SHARE_READ | FILE_SHARE_WRITE,
            IntPtr.Zero,
            OPEN_EXISTING,
            0,
            IntPtr.Zero);

        if (volumeHandle.IsInvalid)
        {
            throw new IOException($"Unable to open volume {volume}", Marshal.GetLastWin32Error());
        }

        var volumeData = new NTFS_VOLUME_DATA_BUFFER();
        uint bytesReturned;
        var volumeDataPtr = Marshal.AllocHGlobal(Marshal.SizeOf(volumeData));

        try
        {
            if (!Kernel32.DeviceIoControl(
                    volumeHandle,
                    FSCTL_GET_NTFS_VOLUME_DATA,
                    IntPtr.Zero,
                    0,
                    volumeDataPtr,
                    (uint)Marshal.SizeOf(volumeData),
                    out bytesReturned,
                    IntPtr.Zero))
            {
                throw new IOException("Failed to get NTFS volume data", Marshal.GetLastWin32Error());
            }

            volumeData = Marshal.PtrToStructure<NTFS_VOLUME_DATA_BUFFER>(volumeDataPtr);
        }
        finally
        {
            Marshal.FreeHGlobal(volumeDataPtr);
        }

#if DEBUG
        // Print out all volume data
        Console.WriteLine($"Volume Serial Number: {volumeData.VolumeSerialNumber}");
        Console.WriteLine($"Number of Sectors: {volumeData.NumberSectors}");
        Console.WriteLine($"Total Clusters: {volumeData.TotalClusters}");
        Console.WriteLine($"Free Clusters: {volumeData.FreeClusters}");
        Console.WriteLine($"Total Reserved: {volumeData.TotalReserved}");
        Console.WriteLine($"Bytes Per Sector: {volumeData.BytesPerSector}");
        Console.WriteLine($"Bytes Per Cluster: {volumeData.BytesPerCluster}");
        Console.WriteLine($"Bytes Per File Record Segment: {volumeData.BytesPerFileRecordSegment}");
        Console.WriteLine($"Clusters Per File Record Segment: {volumeData.ClustersPerFileRecordSegment}");
        Console.WriteLine($"MFT Valid Data Length: {volumeData.MftValidDataLength}");
        Console.WriteLine($"MFT Start LCN: {volumeData.MftStartLcn}");
        Console.WriteLine($"MFT2 Start LCN: {volumeData.Mft2StartLcn}");
        Console.WriteLine($"MFT Zone Start: {volumeData.MftZoneStart}");
        Console.WriteLine($"MFT Zone End: {volumeData.MftZoneEnd}");
#endif

        // Assume 512, 1024, or 2048 bytes per file record. Otherwise, we can't do our explicit struct layout truck
        var bytesPerFileRecord = volumeData.BytesPerFileRecordSegment;
        if (bytesPerFileRecord != 512 && bytesPerFileRecord != 1024 && bytesPerFileRecord != 2048)
            throw new NotSupportedException($"Unsupported bytes per file record: {bytesPerFileRecord}");

        var totalFileRecords = volumeData.MftValidDataLength / volumeData.BytesPerFileRecordSegment;

        Console.WriteLine($"Progress: 0 / {totalFileRecords}");
        //var files = new List<MFTLibFile>((int)totalFileRecords);
        var files = new MFTLibFile[(int)totalFileRecords];

        var mftOffset = (long)volumeData.MftStartLcn * volumeData.BytesPerCluster;

        // Seek to the MFT start offset
        if (!Kernel32.SetFilePointerEx(volumeHandle, mftOffset, IntPtr.Zero, 0))
        {
            Console.WriteLine("Failed to set file pointer. Error: " + Marshal.GetLastWin32Error());
            return null;
        }

        // Read the MFT data
        const int batchSize = 1024;
        uint bytesToRead = batchSize * 1024; // 1 MB buffer
        byte[] readBuffer = new byte[bytesToRead];
        uint bytesRead;
        uint totalBytesRead = 0;
        var validDataLength = volumeData.MftValidDataLength;

        Console.WriteLine("Reading MFT data...");
        Console.Write($"Read 0 bytes from MFT.");
        while (Kernel32.ReadFile(volumeHandle, readBuffer, bytesToRead, out bytesRead, IntPtr.Zero))
        {
            // Process the readBuffer here (parse MFT entries, etc.)
            totalBytesRead += bytesRead;
            var batch = new FileRecordBatch { Bytes = readBuffer };
            var count = bytesRead / bytesPerFileRecord;
            for (var i = 0; i < count; i++)
            {
                switch (bytesPerFileRecord)
                {
                    case 512:
                    {
                        if (batch.FileRecords512 != null)
                            ParseFileRecord(batch.FileRecords512[i]);
                        break;
                    }
                    case 1024:
                    {
                        if (batch.FileRecords1024 != null)
                            ParseFileRecord(batch.FileRecords1024[i]);
                        break;
                    }
                    case 2048:
                    {
                        if (batch.FileRecords2048 != null)
                            ParseFileRecord(batch.FileRecords2048[i]);
                        break;
                    }
                    default:
                        throw new NotSupportedException($"Unsupported bytes per file record: {bytesPerFileRecord}");
                }
            }

            Console.Write($"\rRead {totalBytesRead} / {validDataLength} bytes from MFT.");
            if (totalBytesRead >= validDataLength)
                break;
        }

#if DEBUG
        Console.WriteLine($"Found {files.Length} files in {stopwatch.Elapsed}");
        //foreach (var file in files)
        //{
        //    Console.WriteLine(file.FileName);
        //}
#endif
        
        Kernel32.CloseHandle(volumeHandle);

        return null;
    }

    static MFTLibFile ParseFileRecord(IFIleRecord record)
    {
        var fileRecord = record.Header;
#if DEBUG
        // Ensure that the first 4 bytes spell FILE
        var magicNumber = fileRecord.MagicNumber;
        if (magicNumber == null || magicNumber[0] != 'F' || magicNumber[1] != 'I' || magicNumber[2] != 'L' || magicNumber[3] != 'E')
        {
            throw new ArgumentException("File record fails magic number check (first 4 bytes should spell FILE)");
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

        var attributeOffset = fileRecord.FirstAttributeOffset;
        var end = fileRecord.AllocatedSize;
        var mftFileRecord = new MFTLibFile();
        return mftFileRecord;
        //while (attributeOffset < end)
        //{
        //    var attributePtr = fileRecordPtr + attributeOffset;

        //    // Read the attribute type before we read the header in case the memory after the end marker isn't readable
        //    var attributeId = (AttributeType)Marshal.ReadInt32(attributePtr);
        //    if (attributeId == AttributeType.EndMarker)
        //    {
        //        break;
        //    }
            
        //    var attributeHeader = Marshal.PtrToStructure<AttributeHeader>(attributePtr);
        //    switch (attributeId)
        //    {
        //        case AttributeType.StandardInformation:
        //            //Console.WriteLine($"StandardInformation at offset {attributeOffset}");
        //            //attributeOffset += 0x4; // Skip the attribute header
        //            //var standardInformation = Marshal.PtrToStructure<StandardInformationAttribute>(fileRecordPtr + attributeOffset);
        //            //attributeOffset += 0x48; // Skip to the next attribute
        //            break;
        //        case AttributeType.AttributeList:
        //            //Console.WriteLine($"AttributeList at offset {attributeOffset}");
        //            //attributeOffset += 4; // Skip the attribute
        //            break;
        //        case AttributeType.FileName:
        //            //Console.WriteLine($"FileName at offset {attributeOffset}");
        //            var fileNameAttributePtr = attributePtr + attributeHeader.AttributeOffset;
        //            var fileNameAttribute = Marshal.PtrToStructure<FileNameAttribute>(fileNameAttributePtr);
        //            var fileNamePtr = fileNameAttributePtr + k_OffsetToFileNameUnicode;
        //            mftFileRecord.FileName = Marshal.PtrToStringUni(fileNamePtr, fileNameAttribute.FileNameLength);
        //            //Console.WriteLine($"File name is: {mftFileRecord.FileName}");
        //            break;
        //        case AttributeType.ObjectId:
        //            //Console.WriteLine($"ObjectId at offset {attributeOffset}");
        //            var objAttributePtr = attributePtr + attributeHeader.AttributeOffset;
        //            var objectAttribute = Marshal.PtrToStructure<ObjectIdAttribute>(objAttributePtr);
        //            mftFileRecord.Guid = objectAttribute.ObjectId;
        //            //Console.WriteLine($"Object Id is: {guid}");
        //            break;
        //        case AttributeType.SecurityDescriptor:
        //            //Console.WriteLine($"SecurityDescriptor at offset {attributeOffset}");
        //            //attributeOffset += 4; // Skip the attribute
        //            break;
        //        case AttributeType.VolumeName:
        //            //Console.WriteLine($"VolumeName at offset {attributeOffset}");
        //            //attributeOffset += 4; // Skip the attribute
        //            break;
        //        case AttributeType.VolumeInformation:
        //            //Console.WriteLine($"VolumeInformation at offset {attributeOffset}");
        //            //attributeOffset += 4; // Skip the attribute
        //            break;
        //        case AttributeType.Data:
        //            //Console.WriteLine($"Data at offset {attributeOffset}");
        //            //attributeOffset += 4; // Skip the attribute
        //            break;
        //        case AttributeType.IndexRoot:
        //            //Console.WriteLine($"IndexRoot at offset {attributeOffset}");
        //            //attributeOffset += 4; // Skip the attribute
        //            break;
        //        case AttributeType.IndexAllocation:
        //            //Console.WriteLine($"IndexAllocation at offset {attributeOffset}");
        //            //attributeOffset += 4; // Skip the attribute
        //            break;
        //        case AttributeType.Bitmap:
        //            //Console.WriteLine($"Bitmap at offset {attributeOffset}");
        //            //attributeOffset += 4; // Skip the attribute
        //            break;
        //        case AttributeType.ReparsePoint:
        //            //Console.WriteLine($"ReparsePoint at offset {attributeOffset}");
        //            //attributeOffset += 4; // Skip the attribute
        //            break;
        //        case AttributeType.EAInformation:
        //            //Console.WriteLine($"EAInformation at offset {attributeOffset}");
        //            //attributeOffset += 4; // Skip the attribute
        //            break;
        //        case AttributeType.EA:
        //            //Console.WriteLine($"EA at offset {attributeOffset}");
        //            //attributeOffset += 4; // Skip the attribute
        //            break;
        //        case AttributeType.PropertySet:
        //            //Console.WriteLine($"PropertySet at offset {attributeOffset}");
        //            //attributeOffset += 4; // Skip the attribute
        //            break;
        //        case AttributeType.LoggedUtilityStream:
        //            //Console.WriteLine($"LoggedUtilityStream at offset {attributeOffset}");
        //            //attributeOffset += 4; // Skip the attribute
        //            break;
        //        default:
        //            throw new ArgumentOutOfRangeException($"Unknown attribute type {attributeId}");
        //    }
            
        //    attributeOffset += (ushort)attributeHeader.Length;
        //}

        ////Console.WriteLine($"Parsing file record with size {fileRecord.AllocatedSize}");
        ////if (string.IsNullOrEmpty(fileName))
        ////    throw new InvalidOperationException($"Could not get filename for FileRecord at {fileRecordPtr}");
        
        //return mftFileRecord;
    }
}