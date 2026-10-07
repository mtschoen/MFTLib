using MFTLib.Index;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace MFTLib.Tests;

/// <summary>
///     The managed trust rule and the native freed path resolver are two implementations of one rule. This runs the
///     native fixture, which holds freed records of every trust outcome, through both and requires the same split.
/// </summary>
[TestClass]
public class FreedRowsFixtureParityTests
{
    static readonly ulong[] TrustedFreedRecords = [12, 13, 14, 15];
    static readonly ulong[] UntrustedFreedRecords = [16, 17, 21, 22];

    string _fixturePath = null!;

    [TestInitialize]
    public void Initialize()
    {
        _fixturePath = Path.Combine(Path.GetTempPath(), $"mftlib-freed-parity-{Guid.NewGuid():N}.mft");
        if (OperatingSystem.IsWindows())
        {
            MftVolume.GenerateFixtureMFT(_fixturePath);
        }
    }

    [TestCleanup]
    public void Cleanup() => File.Delete(_fixturePath);

    [TestMethod]
    public void ManagedTrustRule_MatchesTheNativeResolverOnTheFixture()
    {
        if (MftFixtureTests.SkipOnNonWindows())
        {
            return;
        }

        using var resolved = MftVolume.StreamMftFromFile(_fixturePath, null,
            MatchFlags.IncludeFreed | MatchFlags.ResolvePaths);
        var nativeTrusted = resolved.Where(record => !record.InUse)
            .ToDictionary(record => record.RecordNumber, record => record.FullPath is not null);

        using var scanned = MftVolume.StreamMftFromFile(_fixturePath, null, MatchFlags.IncludeFreed);
        using var block = BlockFile.Create(new BlockFileCreateOptions
        {
            Path = Path.Combine(Path.GetTempPath(), $"mft-parity-{Guid.NewGuid():N}.bin"),
            VolumeSerial = 1,
            ProducerKind = ProducerKind.Mft,
            RootRow = 5,
            SlotCapacity = 32,
            NamePoolCapacity = 4096,
            DeleteOnClose = true
        });
        MftBlockRowWriter.WriteBatches(new BlockWriter(block), [scanned.ToArray()],
            new MftBlockRowFilter(BrokerScanProfile.Full, IncludeFreed: true), null, CancellationToken.None);

        Assert.AreEqual(TrustedFreedRecords.Length + UntrustedFreedRecords.Length, nativeTrusted.Count);
        foreach (var recordNumber in nativeTrusted.Keys)
        {
            var managedTrusted = block.Rows[(int)recordNumber].ParentRow != BlockLayout.DetachedParentRow;
            Assert.IsTrue(block.Rows[(int)recordNumber].IsDeleted, $"record {recordNumber} is a deleted row");
            Assert.AreEqual(nativeTrusted[recordNumber], managedTrusted,
                $"record {recordNumber}: native and managed disagree on whether its parent chain verifies");
        }

        CollectionAssert.AreEquivalent(TrustedFreedRecords,
            nativeTrusted.Where(pair => pair.Value).Select(pair => pair.Key).ToArray());
        CollectionAssert.AreEquivalent(UntrustedFreedRecords,
            nativeTrusted.Where(pair => !pair.Value).Select(pair => pair.Key).ToArray());
    }

    [TestMethod]
    public void NativeScan_ReportsEveryRecordsReferencedParentSequence()
    {
        if (MftFixtureTests.SkipOnNonWindows())
        {
            return;
        }

        foreach (var flags in new[] { MatchFlags.IncludeFreed, MatchFlags.IncludeFreed | MatchFlags.ResolvePaths })
        {
            using var result = MftVolume.StreamMftFromFile(_fixturePath, null, flags);
            var sequences = result.ToDictionary(record => record.RecordNumber,
                record => record.ParentSequenceNumber);

            Assert.AreEqual((ushort)6, sequences[8], "sub references its parent, the root, at sequence 6");
            Assert.AreEqual((ushort)0, sequences[6], "live records without a stated parent sequence report zero");
            Assert.AreEqual((ushort)6, sequences[12]);
            Assert.AreEqual((ushort)13, sequences[13]);
            Assert.AreEqual((ushort)9, sequences[15]);
            Assert.AreEqual((ushort)12, sequences[17]);
        }
    }
}
