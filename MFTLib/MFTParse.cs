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

    public static string GetFileNameForDriveLetter(string driveLetter)
    {
        if (driveLetter.EndsWith(':'))
            throw new ArgumentException("Drive letter should not end with a colon", nameof(driveLetter));

        return @$"\\.\{driveLetter}:";
    }

    public static Dictionary<string, MFTEntry> GetFileNodes(string volume)
    {
        if (string.IsNullOrEmpty(volume))
        {
            throw new ArgumentException("Volume name cannot be null or empty", nameof(volume));
        }

        var volumeHandle = NativeAPI.CreateFile(
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
            if (!NativeAPI.DeviceIoControl(
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

        var files = new Dictionary<string, MFTEntry>();

        // Read MFT records
        ulong fileReferenceNumber = 0;
        while (true)
        {
            var inputBuffer = new NTFS_FILE_RECORD_INPUT_BUFFER { FileReferenceNumber = fileReferenceNumber };
            var inputBufferPtr = Marshal.AllocHGlobal(Marshal.SizeOf(inputBuffer));
            Marshal.StructureToPtr(inputBuffer, inputBufferPtr, false);

            var outputBufferSize = (uint)(Marshal.SizeOf(typeof(NTFS_FILE_RECORD_OUTPUT_BUFFER)) + volumeData.BytesPerFileRecordSegment - 1);
            var outputBufferPtr = Marshal.AllocHGlobal((int)outputBufferSize);

            try
            {
                if (!NativeAPI.DeviceIoControl(
                        volumeHandle,
                        FSCTL_GET_NTFS_FILE_RECORD,
                        inputBufferPtr,
                        (uint)Marshal.SizeOf(inputBuffer),
                        outputBufferPtr,
                        outputBufferSize,
                        out bytesReturned,
                        IntPtr.Zero))
                {
                    var error = Marshal.GetLastWin32Error();
                    if (error == 38) // ERROR_HANDLE_EOF
                    {
                        break;
                    }
                    throw new IOException("Failed to get NTFS file record", error);
                }

                //var outputBuffer = Marshal.PtrToStructure<NTFS_FILE_RECORD_OUTPUT_BUFFER>(outputBufferPtr);
                var fileRecordPtr = outputBufferPtr + k_OffsetToFileRecordBuffer;
                var fileName = ParseFileName(fileRecordPtr);
                if (string.IsNullOrEmpty(fileName))
                {
                    Console.WriteLine("Found file record with no name");
                    continue;
                }

                files[fileName] = new MFTEntry();
            }
            finally
            {
                Marshal.FreeHGlobal(inputBufferPtr);
                Marshal.FreeHGlobal(outputBufferPtr);
                fileReferenceNumber++;
            }
        }

        return files;
    }

    static string ParseFileName(IntPtr fileRecordPtr)
    {
        var fileRecord = Marshal.PtrToStructure<FileRecordHeader>(fileRecordPtr);

#if DEBUG
        // Ensure that the first 4 bytes spell FILE
        var magicNumber = fileRecord.MagicNumber;
        if (magicNumber == null || magicNumber[0] != 'F' || magicNumber[1] != 'I' || magicNumber[2] != 'L' || magicNumber[3] != 'E')
        {
            throw new ArgumentException("File record fails magic number check (first 4 bytes should spell FILE)");
        }
#endif

        var attributeOffset = fileRecord.FirstAttributeOffset;
        var end = fileRecord.AllocatedSize;
        var fileName = string.Empty;
        while (attributeOffset < end)
        {
            var attributePtr = fileRecordPtr + attributeOffset;

            // Read the attribute type before we read the header in case the memory after the end marker isn't readable
            var attributeId = (AttributeType)Marshal.ReadInt32(attributePtr);
            if (attributeId == AttributeType.EndMarker)
            {
                break;
            }
            
            var attributeHeader = Marshal.PtrToStructure<AttributeHeader>(attributePtr);
            switch (attributeId)
            {
                case AttributeType.StandardInformation:
                    Console.WriteLine($"StandardInformation at offset {attributeOffset}");
                    //attributeOffset += 0x4; // Skip the attribute header
                    //var standardInformation = Marshal.PtrToStructure<StandardInformationAttribute>(fileRecordPtr + attributeOffset);
                    //attributeOffset += 0x48; // Skip to the next attribute
                    break;
                case AttributeType.AttributeList:
                    Console.WriteLine($"AttributeList at offset {attributeOffset}");
                    //attributeOffset += 4; // Skip the attribute
                    break;
                case AttributeType.FileName:
                    Console.WriteLine($"FileName at offset {attributeOffset}");
                    var fileNameAttributePtr = attributePtr + attributeHeader.AttributeOffset;
                    var fileNameAttribute = Marshal.PtrToStructure<FileNameAttribute>(fileNameAttributePtr);
                    Console.WriteLine($"File size is {fileNameAttribute.RealSize}");
                    var fileNamePtr = fileNameAttributePtr + k_OffsetToFileNameUnicode;
                    fileName = Marshal.PtrToStringUni(fileNamePtr, fileNameAttribute.FileNameLength);
                    Console.WriteLine($"File name is: {fileName}");
                    //attributeOffset += 4; // Skip the attribute
                    break;
                case AttributeType.ObjectId:
                    Console.WriteLine($"ObjectId at offset {attributeOffset}");
                    //attributeOffset += 4; // Skip the attribute
                    break;
                case AttributeType.SecurityDescriptor:
                    Console.WriteLine($"SecurityDescriptor at offset {attributeOffset}");
                    //attributeOffset += 4; // Skip the attribute
                    break;
                case AttributeType.VolumeName:
                    Console.WriteLine($"VolumeName at offset {attributeOffset}");
                    //attributeOffset += 4; // Skip the attribute
                    break;
                case AttributeType.VolumeInformation:
                    Console.WriteLine($"VolumeInformation at offset {attributeOffset}");
                    //attributeOffset += 4; // Skip the attribute
                    break;
                case AttributeType.Data:
                    Console.WriteLine($"Data at offset {attributeOffset}");
                    //attributeOffset += 4; // Skip the attribute
                    break;
                case AttributeType.IndexRoot:
                    Console.WriteLine($"IndexRoot at offset {attributeOffset}");
                    //attributeOffset += 4; // Skip the attribute
                    break;
                case AttributeType.IndexAllocation:
                    Console.WriteLine($"IndexAllocation at offset {attributeOffset}");
                    //attributeOffset += 4; // Skip the attribute
                    break;
                case AttributeType.Bitmap:
                    Console.WriteLine($"Bitmap at offset {attributeOffset}");
                    //attributeOffset += 4; // Skip the attribute
                    break;
                case AttributeType.ReparsePoint:
                    Console.WriteLine($"ReparsePoint at offset {attributeOffset}");
                    //attributeOffset += 4; // Skip the attribute
                    break;
                case AttributeType.EAInformation:
                    Console.WriteLine($"EAInformation at offset {attributeOffset}");
                    //attributeOffset += 4; // Skip the attribute
                    break;
                case AttributeType.EA:
                    Console.WriteLine($"EA at offset {attributeOffset}");
                    //attributeOffset += 4; // Skip the attribute
                    break;
                case AttributeType.PropertySet:
                    Console.WriteLine($"PropertySet at offset {attributeOffset}");
                    //attributeOffset += 4; // Skip the attribute
                    break;
                case AttributeType.LoggedUtilityStream:
                    Console.WriteLine($"LoggedUtilityStream at offset {attributeOffset}");
                    //attributeOffset += 4; // Skip the attribute
                    break;
                default:
                    throw new ArgumentOutOfRangeException($"Unknown attribute type {attributeId}");
            }
            
            attributeOffset += (ushort)attributeHeader.Length;
        }

        //Console.WriteLine($"Parsing file record with size {fileRecord.AllocatedSize}");
        //if (string.IsNullOrEmpty(fileName))
        //    throw new InvalidOperationException($"Could not get filename for FileRecord at {fileRecordPtr}");
        
        return fileName;
    }
}