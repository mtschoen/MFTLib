namespace MFTLib.Tests.TestSupport;

/// <summary>
///     Builds files the volume parser (ParseMFTRecordsWithProgress) accepts in place of a volume
///     handle: an NTFS boot sector at offset 0 and an MFT starting at cluster 1 (offset 4096), with
///     512-byte sectors and 4096-byte clusters. Parse them with 1024-byte records forced through
///     SetVolumeRecordSizeOverride so the host volume's record size does not matter.
/// </summary>
static class SyntheticNtfsImage
{
    const int ClusterSize = 4096;
    const int RecordSize = 1024;

    /// <summary>
    ///     Writes an image whose MFT is <paramref name="recordCount" /> synthetic records (named
    ///     files under a root, so paths resolve), with record 0 replaced by an $MFT record whose
    ///     $DATA run covers them all. <paramref name="recordCount" /> must be a multiple of 4.
    /// </summary>
    public static void Write(string path, int recordCount)
    {
        var mftPath = Path.GetTempFileName();
        try
        {
            File.Delete(mftPath);
            MftVolume.GenerateSyntheticMFT(mftPath, (ulong)recordCount, 256);
            Write(path, File.ReadAllBytes(mftPath));
        }
        finally
        {
            File.Delete(mftPath);
        }
    }

    /// <summary>
    ///     Writes an image whose MFT is the 1024-byte records of <paramref name="mft" />, with record 0
    ///     replaced by an $MFT record whose $DATA run covers them all. The MFT must be a whole number
    ///     of 4096-byte clusters.
    /// </summary>
    public static void Write(string path, byte[] mft)
    {
        var data = BuildBootSector(ClusterSize + mft.Length);
        mft.CopyTo(data, ClusterSize);
        Array.Clear(data, ClusterSize, RecordSize);
        WriteFileRecord(data, ClusterSize);
        var dataAttributeLength = WriteNonResidentDataAttribute(
            data, ClusterSize + 0x38, mft.Length, 1, mft.Length / ClusterSize);
        WriteEndMarker(data, ClusterSize + 0x38 + dataAttributeLength);
        File.WriteAllBytes(path, data);
    }

    public static byte[] BuildBootSector(int fileSize = 2 * 1024 * 1024)
    {
        var data = new byte[fileSize];
        data[3] = (byte)'N';
        data[4] = (byte)'T';
        data[5] = (byte)'F';
        data[6] = (byte)'S';
        data[0x0B] = 0x00;
        data[0x0C] = 0x02; // bytesPerSector = 512
        data[0x0D] = 0x08; // sectorsPerCluster = 8 (4096 bytes/cluster)
        data[0x30] = 0x01; // mftStart = cluster 1 (offset 4096)
        return data;
    }

    public static void WriteFileRecord(byte[] data, int offset, ushort usn = 0x0001, uint recordSize = RecordSize)
    {
        data[offset] = 0x46;
        data[offset + 1] = 0x49;
        data[offset + 2] = 0x4C;
        data[offset + 3] = 0x45;
        var usaSize = (ushort)(recordSize / 512 + 1);
        data[offset + 4] = 0x30;
        data[offset + 5] = 0x00;
        data[offset + 6] = (byte)(usaSize & 0xFF);
        data[offset + 7] = (byte)(usaSize >> 8);
        var firstAttrOffset = (ushort)((48 + usaSize * 2 + 7) & ~7);
        data[offset + 0x14] = (byte)(firstAttrOffset & 0xFF);
        data[offset + 0x15] = (byte)(firstAttrOffset >> 8);
        data[offset + 0x16] = 0x01;
        BitConverter.GetBytes(recordSize).CopyTo(data, offset + 0x1C);
        data[offset + 48] = (byte)(usn & 0xFF);
        data[offset + 49] = (byte)(usn >> 8);
        var sectorCount = (int)(recordSize / 512);
        for (var i = 0; i < sectorCount; i++)
        {
            data[offset + 50 + i * 2] = 0x00;
            data[offset + 51 + i * 2] = 0x00;
            var sectorEnd = (i + 1) * 512 - 2;
            data[offset + sectorEnd] = (byte)(usn & 0xFF);
            data[offset + sectorEnd + 1] = (byte)(usn >> 8);
        }
    }

    public static int WriteNonResidentDataAttribute(byte[] data, int offset, long fileSize, int clusterOffset,
        int clusterCount)
    {
        data[offset] = 0x80; // TypeCode = Data
        data[offset + 4] = 0x48; // RecordLength = 72
        data[offset + 8] = 0x01; // FormCode = non-resident
        data[offset + 0x20] = 0x40; // MappingPairsOffset
        var sizeBytes = BitConverter.GetBytes(fileSize);
        Array.Copy(sizeBytes, 0, data, offset + 0x28, 8);
        Array.Copy(sizeBytes, 0, data, offset + 0x30, 8);
        Array.Copy(sizeBytes, 0, data, offset + 0x38, 8);
        data[offset + 0x40] = 0x12;
        data[offset + 0x41] = (byte)(clusterCount & 0xFF);
        data[offset + 0x42] = (byte)((clusterCount >> 8) & 0xFF);
        data[offset + 0x43] = (byte)(clusterOffset & 0xFF);
        data[offset + 0x44] = 0x00;
        return 0x48;
    }

    public static void WriteEndMarker(byte[] data, int offset)
    {
        data[offset] = 0xFF;
        data[offset + 1] = 0xFF;
        data[offset + 2] = 0xFF;
        data[offset + 3] = 0xFF;
    }
}
