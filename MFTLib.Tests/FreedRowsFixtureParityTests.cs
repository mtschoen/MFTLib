using MFTLib.Index;
using MFTLib.Tests.TestSupport;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace MFTLib.Tests;

/// <summary>
///     The fixture holds freed records of every trust outcome. A native scan of it that includes freed rows,
///     written through the managed trust rule, attaches exactly the freed records whose parent chain verifies
///     and detaches the rest.
/// </summary>
[TestClass]
[DoNotParallelize]
public class FreedRowsFixtureParityTests
{
    static readonly ulong[] TrustedFreedRecords = [12, 13, 14, 15];
    static readonly ulong[] UntrustedFreedRecords = [16, 17, 21, 22];

    string _fixturePath = null!;
    string _imagePath = null!;

    [TestInitialize]
    public void Initialize()
    {
        _fixturePath = Path.Combine(Path.GetTempPath(), $"mftlib-freed-parity-{Guid.NewGuid():N}.mft");
        _imagePath = Path.ChangeExtension(_fixturePath, ".img");
        MftVolume.GenerateFixtureMFT(_fixturePath);
    }

    [TestCleanup]
    public void Cleanup()
    {
        NativeTestHooks.NativeResetTestState();
        File.Delete(_fixturePath);
        File.Delete(_imagePath);
    }

    [TestMethod]
    public void ManagedTrustRule_SplitsTheFixturesFreedRecordsByTheirParentChain()
    {
        if (WindowsOnlyNative.SkipWithoutVolumeParse())
        {
            return;
        }

        using var scanned = FixtureVolume.Parse(_imagePath, File.ReadAllBytes(_fixturePath), true);
        var freedRecords = scanned.Where(record => !record.InUse).Select(record => record.RecordNumber).ToArray();
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

        CollectionAssert.AreEquivalent(TrustedFreedRecords.Concat(UntrustedFreedRecords).ToArray(), freedRecords);
        foreach (var recordNumber in freedRecords)
        {
            Assert.IsTrue(block.Rows[(int)recordNumber].IsDeleted, $"record {recordNumber} is a deleted row");
            Assert.AreEqual(TrustedFreedRecords.Contains(recordNumber),
                block.Rows[(int)recordNumber].ParentRow != BlockLayout.DetachedParentRow,
                $"record {recordNumber}: the managed rule attaches it exactly when its parent chain verifies");
        }
    }

    [TestMethod]
    public void NativeScan_ReportsEveryRecordsReferencedParentSequence()
    {
        if (WindowsOnlyNative.SkipWithoutVolumeParse())
        {
            return;
        }

        using var result = FixtureVolume.Parse(_imagePath, File.ReadAllBytes(_fixturePath), true);
        var sequences = result.ToDictionary(record => record.RecordNumber, record => record.ParentSequenceNumber);

        Assert.AreEqual((ushort)6, sequences[8], "sub references its parent, the root, at sequence 6");
        Assert.AreEqual((ushort)0, sequences[6], "live records without a stated parent sequence report zero");
        Assert.AreEqual((ushort)6, sequences[12]);
        Assert.AreEqual((ushort)13, sequences[13]);
        Assert.AreEqual((ushort)9, sequences[15]);
        Assert.AreEqual((ushort)12, sequences[17]);
    }
}
