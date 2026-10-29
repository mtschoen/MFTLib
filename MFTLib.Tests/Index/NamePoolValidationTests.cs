using MFTLib.Index;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using NamePool = MFTLibTestExtensions.SyntheticNamePool;

namespace MFTLib.Tests.Index;

[TestClass]
public class NamePoolValidationTests
{
    static readonly DateTime ScanMoment = new(2026, 9, 2, 10, 0, 0, DateTimeKind.Utc);

    static (SyntheticBlockBuilder Builder, uint Row, uint NameId) BuildWithOneName(string name)
    {
        var builder = new SyntheticBlockBuilder();
        builder.AddRoot();
        var row = builder.AddRow(name, 0, RowFlags.InUse, 1, ScanMoment, 0);
        var nameId = builder.OpenForWriting().Rows[(int)row].NameId;
        return (builder, row, nameId);
    }

    [TestMethod]
    [DataRow("OutOfRangeId")]
    [DataRow("OddUtf16Offset")]
    [DataRow("PoolOverrun")]
    [DataRow("ZeroLength")]
    public void CorruptEntry_OpensThenFailsOnFirstNameRead(string corruption)
    {
        var (builder, row, nameId) = BuildWithOneName(corruption == "OddUtf16Offset" ? "café" : "report.pdf");
        using (builder)
        {
            var writable = builder.OpenForWriting();
            // Postings are built from the intact rows; the corruption lands afterwards, as damage to a stored block would.
            builder.Complete(ScanMoment);
            switch (corruption)
            {
                case "OutOfRangeId": builder.MutateNameDescriptor(row, writable.NameEntryCount + 5); break;
                case "OddUtf16Offset": writable.NameEntries[(int)nameId].PoolOffsetBytes = 1; break;
                case "PoolOverrun": writable.NameEntries[(int)nameId].PoolOffsetBytes = writable.NamePoolUsed - 1; break;
                case "ZeroLength": writable.NameEntries[(int)nameId].LengthAndEncoding = 0; break;
                default: Assert.Fail("Unknown corruption case."); break;
            }

            writable.Flush(null);

            using var reopened = builder.OpenForReading(out var validation);
            Assert.IsNull(validation);
            Assert.IsNotNull(reopened);
            var diagnostics = new List<string>();
            reopened.Diagnostics = diagnostics.Add;
            Assert.ThrowsExactly<InvalidDataException>(() => NamePool.ReadRowName(reopened, row));
            Assert.HasCount(1, diagnostics);
            StringAssert.Contains(diagnostics[0], reopened.Path);
        }
    }

    [TestMethod]
    [DataRow("CyclicNext")]
    [DataRow("WrongTag")]
    public void CorruptChain_OpensAndExactLookupReturnsNotFoundWithoutLooping(string corruption)
    {
        using var builder = new SyntheticBlockBuilder();
        builder.AddRoot();
        var writable = builder.OpenForWriting();
        var (older, newer) = FindSameBucketNames(writable.BucketCount);
        builder.AddRow(older, 0, RowFlags.InUse, 1, ScanMoment, 0);
        builder.AddRow(newer, 0, RowFlags.InUse, 1, ScanMoment, 0);
        Assert.IsTrue(NamePool.TryFindOrdinal(writable, older, out var olderId));
        Assert.IsTrue(NamePool.TryFindOrdinal(writable, newer, out var newerId));
        Assert.AreEqual(olderId, writable.NameEntries[(int)newerId].Next);
        if (corruption == "CyclicNext")
        {
            writable.NameEntries[(int)newerId].Next = newerId;
        }
        else
        {
            writable.NameEntries[(int)olderId].HashTag++;
        }

        builder.Complete(ScanMoment);

        using var reopened = builder.OpenForReading(out var validation);
        Assert.IsNull(validation);
        Assert.IsNotNull(reopened);
        Assert.IsTrue(NamePool.TryFindOrdinal(reopened, newer, out _));
        Assert.IsFalse(NamePool.TryFindOrdinal(reopened, older, out _));
    }

    static (string Older, string Newer) FindSameBucketNames(uint bucketCount)
    {
        var seen = new Dictionary<ulong, string>();
        for (var index = 0; index < 200_000; index++)
        {
            var name = $"collision-{index}";
            var bucket = NamePool.Hash(name) & (bucketCount - 1);
            if (seen.TryGetValue(bucket, out var earlier))
            {
                return (earlier, name);
            }

            seen.Add(bucket, name);
        }

        throw new InvalidOperationException("No bucket collision found.");
    }

    [TestMethod]
    public void Open_OfALargeBlock_TouchesNoEntryBucketOrPoolRegion()
    {
        const int rows = 200_000;
        using var builder = new SyntheticBlockBuilder(slotCapacity: rows + 16, namePoolCapacity: 16 * 1024 * 1024);
        builder.AddRoot();
        for (var index = 0; index < rows; index++)
        {
            builder.AddRow($"file-{index}.dat", 0, RowFlags.InUse, index, ScanMoment, 0);
        }

        builder.Complete(ScanMoment);

        using var reopened = builder.OpenForReading(out var validation);
        Assert.IsNull(validation);
        Assert.IsNotNull(reopened);
        Assert.AreEqual(0L, reopened.NameRegionAccessCount);
        Assert.AreEqual((uint)rows, reopened.NameEntryCount);
        Assert.AreEqual("file-7.dat", NamePool.ReadRowName(reopened, 8).ToString());
        Assert.IsGreaterThan(0L, reopened.NameRegionAccessCount);
    }

    [TestMethod]
    [DataRow("PoolUsedBelowEntryCount")]
    [DataRow("PoolUsedPastCapacity")]
    [DataRow("EntryCountPastCapacity")]
    [DataRow("BucketCountOffTheFormula")]
    public void Open_HeaderNameRegionFacts_AreCheckedEagerly(string corruption)
    {
        var (builder, _, _) = BuildWithOneName("report.pdf");
        using (builder)
        {
            builder.Complete(ScanMoment);
            builder.MutateHeader((ref header) =>
            {
                switch (corruption)
                {
                    case "PoolUsedBelowEntryCount": header.NamePoolUsed = 0; break;
                    case "PoolUsedPastCapacity": header.NamePoolUsed = header.NamePoolCapacity + 1; break;
                    case "EntryCountPastCapacity": header.NameEntryCount = header.NameEntryCapacity + 1; break;
                    default: header.BucketCount *= 2; break;
                }
            });

            using var reopened = builder.OpenForReading(out var validation);
            Assert.IsNull(reopened);
            Assert.AreEqual(BlockValidationResult.InvalidNameDescriptor, validation);
        }
    }

    [TestMethod]
    public void IntegrityCheck_PassesForAWriterProducedBlock()
    {
        using var builder = new SyntheticBlockBuilder();
        builder.AddRoot();
        foreach (var name in new[] { "Makefile", "makefile", "café", "CAFÉ", "\uD800lone", "a.txt", "a.txt" })
        {
            builder.AddRow(name, 0, RowFlags.InUse, 1, ScanMoment, 0);
        }

        Assert.IsNull(NamePool.CheckIntegrity(builder.OpenForWriting()));
    }

    [TestMethod]
    [DataRow("tag")]
    [DataRow("bucket")]
    [DataRow("unreachable")]
    [DataRow("ascii")]
    [DataRow("rowId")]
    [DataRow("reservedBits")]
    [DataRow("chain")]
    public void IntegrityCheck_ReportsEachCorruptionKind(string corruption)
    {
        var (builder, row, nameId) = BuildWithOneName("alpha");
        using (builder)
        {
            var block = builder.OpenForWriting();
            var bucket = (int)(NamePool.Hash("alpha") & (block.BucketCount - 1));
            switch (corruption)
            {
                case "tag": block.NameEntries[(int)nameId].HashTag++; break;
                case "bucket":
                    block.Buckets[bucket] = 0;
                    block.Buckets[(bucket + 1) % block.Buckets.Length] = nameId;
                    break;
                case "unreachable": block.Buckets[bucket] = 0; break;
                case "ascii": block.NamePoolBytes[0] = 0xff; break;
                case "rowId": builder.MutateNameDescriptor(row, nameId + 3); break;
                case "reservedBits": block.Rows[(int)row].Reserved = 1; break;
                case "chain": block.NameEntries[(int)nameId].Next = nameId; break;
                default: Assert.Fail("Unknown corruption case."); break;
            }

            Assert.IsNotNull(NamePool.CheckIntegrity(block));
        }
    }
}
