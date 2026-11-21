using System.Buffers.Binary;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace MFTLib.Tests.TestSupport;

/// <summary>
///     Builds an NTFS image whose <c>$MFT</c> is two runs with a hole between them, so records adjacent in the MFT
///     are far apart on disk, and a read that spanned the runs would return the filler instead of the records.
///     4096-byte clusters, 1024-byte records, the MFT's first run at cluster 1.
/// </summary>
static class FragmentedNtfsImage
{
    internal const int ClusterSize = 4096;
    internal const int RecordSize = 1024;
    internal const int RecordsPerCluster = ClusterSize / RecordSize;
    internal const byte Filler = 0xEE;

    /// <summary>
    ///     Writes the image: the first <paramref name="firstRunRecords" /> records of <paramref name="mft" /> at
    ///     cluster 1, then <paramref name="holeClusters" /> clusters of filler, then the rest. Record 0 is replaced
    ///     by an <c>$MFT</c> record naming both runs.
    /// </summary>
    internal static void Write(string path, byte[] mft, int firstRunRecords, int holeClusters)
    {
        Assert.AreEqual(0, firstRunRecords % RecordsPerCluster);
        Assert.AreEqual(0, mft.Length % ClusterSize);
        var firstRunClusters = firstRunRecords / RecordsPerCluster;
        var secondRunClusters = (mft.Length / ClusterSize) - firstRunClusters;
        var secondRunStart = 1 + firstRunClusters + holeClusters;
        var image = SyntheticNtfsImage.BuildBootSector((secondRunStart + secondRunClusters) * ClusterSize);
        Array.Fill(image, Filler, ClusterSize, image.Length - ClusterSize);
        mft.AsSpan(0, firstRunClusters * ClusterSize).CopyTo(image.AsSpan(ClusterSize));
        mft.AsSpan(firstRunClusters * ClusterSize).CopyTo(image.AsSpan(secondRunStart * ClusterSize));
        Array.Clear(image, ClusterSize, RecordSize);
        SyntheticNtfsImage.WriteFileRecord(image, ClusterSize);
        var end = WriteDataAttribute(image, ClusterSize + 0x38, mft.Length, firstRunClusters, secondRunClusters,
            secondRunStart - 1);
        SyntheticNtfsImage.WriteEndMarker(image, ClusterSize + 0x38 + end);
        File.WriteAllBytes(path, image);
    }

    // A nonresident unnamed $DATA of two runs: the first at cluster 1, the second at a delta from it.
    static int WriteDataAttribute(byte[] image, int offset, long fileSize, int firstClusters, int secondClusters,
        int secondDelta)
    {
        const int length = 0x50;
        var attribute = image.AsSpan(offset, length);
        BinaryPrimitives.WriteUInt32LittleEndian(attribute, 0x80);
        BinaryPrimitives.WriteUInt32LittleEndian(attribute[4..], length);
        attribute[8] = 1;
        BinaryPrimitives.WriteUInt16LittleEndian(attribute[0x20..], 0x40);
        BinaryPrimitives.WriteInt64LittleEndian(attribute[0x28..], fileSize);
        BinaryPrimitives.WriteInt64LittleEndian(attribute[0x30..], fileSize);
        BinaryPrimitives.WriteInt64LittleEndian(attribute[0x38..], fileSize);
        var runs = attribute[0x40..];
        runs[0] = 0x22;
        BinaryPrimitives.WriteUInt16LittleEndian(runs[1..], (ushort)firstClusters);
        BinaryPrimitives.WriteUInt16LittleEndian(runs[3..], 1);
        runs[5] = 0x22;
        BinaryPrimitives.WriteUInt16LittleEndian(runs[6..], (ushort)secondClusters);
        BinaryPrimitives.WriteUInt16LittleEndian(runs[8..], (ushort)secondDelta);
        return length;
    }
}
