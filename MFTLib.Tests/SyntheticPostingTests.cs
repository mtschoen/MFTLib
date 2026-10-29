using MFTLib.Index;
using MFTLib.Tests.TestSupport;
using MFTLibTestExtensions;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace MFTLib.Tests;

/// <summary>Blocks written and edited through <c>MFTLibTestExtensions</c> carry a posting base that covers their rows.</summary>
[TestClass]
public sealed class SyntheticPostingTests : IDisposable
{
    const uint Serial = 0x4242;

    readonly TempDirectory _directory = new("mftlib-synthetic-postings");

    public void Dispose() => _directory.Dispose();

    static SyntheticRow Row(uint row, string name, bool directory = false, bool tombstone = false) =>
        new(row, name, 5) { IsDirectory = directory, IsTombstone = tombstone };

    string Write(IEnumerable<SyntheticRow> rows) =>
        SyntheticBlock.WriteCached(_directory.Path, new VolumeKey('T'), Serial,
            new SyntheticBlockOptions { ProducerKind = ProducerKind.Mft, RootRow = 5 }, rows);

    [TestMethod]
    public void WriteCached_RootOnlyBlock_HasAnIntactPostingBase()
    {
        var path = Write([Row(5, ".", directory: true)]);

        using var block = BlockFile.Open(path, Serial, out var validation);

        Assert.IsNull(validation);
        Assert.IsNotNull(block);
        Assert.IsTrue(block.PostingsBuilt);
        Assert.AreEqual(1u, block.PostingBaseRowCount);
        Assert.IsNull(SyntheticNamePool.CheckPostingIntegrity(block));
    }

    [TestMethod]
    public void Edit_RewritingANameRebuildsThePostingBase()
    {
        var path = Write([Row(5, ".", directory: true), Row(6, "a.txt"), Row(7, "a.txt"),
            Row(8, "gone.txt", tombstone: true)]);

        SyntheticBlock.Edit(path, Serial, editor => editor.WriteRow(Row(7, "b.txt")));

        using var block = BlockFile.Open(path, Serial, out var validation);
        Assert.IsNull(validation);
        Assert.IsNotNull(block);
        Assert.AreEqual(4u, block.PostingBaseRowCount, "tombstoned rows are listed");
        Assert.IsNull(SyntheticNamePool.CheckPostingIntegrity(block));
    }
}
