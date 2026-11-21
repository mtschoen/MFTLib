using System.Buffers.Binary;
using System.Numerics;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace MFTLib.Tests.TestSupport;

/// <summary>
///     Builds an NTFS image whose <c>$MFT</c> is several runs with holes between them, so records adjacent in the MFT
///     are far apart on disk, and a read that spanned two runs would return the filler instead of the records.
///     4096-byte clusters, 1024-byte records unless a caller names another size, the MFT's first run at cluster 1.
/// </summary>
static class FragmentedNtfsImage
{
    internal const int ClusterSize = 4096;
    internal const int RecordSize = 1024;
    internal const int RecordsPerCluster = ClusterSize / RecordSize;
    internal const byte Filler = 0xEE;

    // The mapping pairs stay inside the first sector of record 0, clear of its update sequence slot at 510.
    const int MappingPairsLimit = 508;

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
        WriteRuns(path, mft, [(firstRunClusters, 0), ((mft.Length / ClusterSize) - firstRunClusters, holeClusters)]);
    }

    /// <summary>
    ///     Writes the image with every cluster of <paramref name="mft" /> its own run, one filler cluster after each,
    ///     so each run holds four records and no two runs touch on disk.
    /// </summary>
    internal static void WriteOneClusterRuns(string path, byte[] mft)
    {
        Assert.AreEqual(0, mft.Length % ClusterSize);
        WriteRuns(path, mft, [.. Enumerable.Range(0, mft.Length / ClusterSize).Select(run => (1, run == 0 ? 0 : 1))]);
    }

    /// <summary>
    ///     Writes the image with the runs given, each its cluster count and the filler clusters before it, and
    ///     <paramref name="recordSize" />-byte records, so a record larger than a cluster can straddle two runs.
    /// </summary>
    internal static void WriteRuns(string path, byte[] mft, (int Clusters, int HoleBefore)[] runs,
        int recordSize = RecordSize)
    {
        Assert.AreEqual(mft.Length, runs.Sum(run => run.Clusters) * ClusterSize, "the runs hold the whole MFT");
        var imageClusters = 1 + runs.Sum(run => run.Clusters + run.HoleBefore);
        var image = SyntheticNtfsImage.BuildBootSector(imageClusters * ClusterSize);
        // Clusters per file record: a count when a record is at least a cluster, else minus log2 of its size.
        image[0x40] = recordSize >= ClusterSize
            ? (byte)(recordSize / ClusterSize)
            : unchecked((byte)(sbyte)-BitOperations.Log2((uint)recordSize));
        Array.Fill(image, Filler, ClusterSize, image.Length - ClusterSize);
        var cluster = 1;
        var mftOffset = 0;
        var starts = new int[runs.Length];
        for (var index = 0; index < runs.Length; index++)
        {
            cluster += runs[index].HoleBefore;
            starts[index] = cluster;
            var length = runs[index].Clusters * ClusterSize;
            mft.AsSpan(mftOffset, length).CopyTo(image.AsSpan(cluster * ClusterSize));
            cluster += runs[index].Clusters;
            mftOffset += length;
        }

        Array.Clear(image, ClusterSize, recordSize);
        SyntheticNtfsImage.WriteFileRecord(image, ClusterSize, recordSize: (uint)recordSize);
        // The first attribute follows the header and its update sequence array, as WriteFileRecord places it.
        var attributeOffset = (48 + (((recordSize / 512) + 1) * 2) + 7) & ~7;
        var end = WriteDataAttribute(image, ClusterSize, attributeOffset, mft.Length, runs, starts);
        SyntheticNtfsImage.WriteEndMarker(image, ClusterSize + attributeOffset + end);
        File.WriteAllBytes(path, image);
    }

    // A nonresident unnamed $DATA whose mapping pairs name each run: its length, then its start as a delta from the
    // previous run's start, each in the fewest little-endian bytes that hold it (the delta signed).
    static int WriteDataAttribute(byte[] image, int recordStart, int attributeOffset, long fileSize,
        (int Clusters, int HoleBefore)[] runs, int[] starts)
    {
        var pairs = new List<byte>();
        var previous = 0;
        for (var index = 0; index < runs.Length; index++)
        {
            var lengthBytes = MinimumBytes(runs[index].Clusters);
            var deltaBytes = MinimumBytes(starts[index] - previous);
            pairs.Add((byte)((deltaBytes.Length << 4) | lengthBytes.Length));
            pairs.AddRange(lengthBytes);
            pairs.AddRange(deltaBytes);
            previous = starts[index];
        }

        pairs.Add(0);
        var length = (0x40 + pairs.Count + 7) & ~7;
        Assert.IsLessThanOrEqualTo(MappingPairsLimit, attributeOffset + 0x40 + pairs.Count,
            "the runs fit record 0's first sector");
        var attribute = image.AsSpan(recordStart + attributeOffset, length);
        attribute.Clear();
        BinaryPrimitives.WriteUInt32LittleEndian(attribute, 0x80);
        BinaryPrimitives.WriteUInt32LittleEndian(attribute[4..], (uint)length);
        attribute[8] = 1;
        BinaryPrimitives.WriteUInt16LittleEndian(attribute[0x20..], 0x40);
        BinaryPrimitives.WriteInt64LittleEndian(attribute[0x28..], fileSize);
        BinaryPrimitives.WriteInt64LittleEndian(attribute[0x30..], fileSize);
        BinaryPrimitives.WriteInt64LittleEndian(attribute[0x38..], fileSize);
        pairs.ToArray().CopyTo(attribute[0x40..]);
        return length;
    }

    // The value in the fewest little-endian bytes that still read back as the same signed number.
    static byte[] MinimumBytes(int value)
    {
        var bytes = BitConverter.GetBytes(value);
        var count = 4;
        while (count > 1 && bytes[count - 1] == (value < 0 ? 0xFF : 0) &&
               (bytes[count - 2] & 0x80) == (value < 0 ? 0x80 : 0))
        {
            count--;
        }

        return bytes[..count];
    }
}
