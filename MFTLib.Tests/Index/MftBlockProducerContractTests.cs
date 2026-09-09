using MFTLib.Index;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace MFTLib.Tests.Index;

[TestClass]
public class MftBlockProducerContractTests
{
    [TestMethod]
    public async Task Producer_ReceivesTheRequestAndReturnsItsBlock()
    {
        MftBlockProduceRequest? seen = null;
        using var builder = SyntheticBlockBuilder.MftShaped();

        MftBlockProducer producer = (request, cancellationToken) =>
        {
            seen = request;
            var block = BlockFile.Open(request.BlockPath, request.VolumeSerial, out _)!;
            return Task.FromResult(new MftBlockProduceResult(block, JournalId: 7, NextUsn: 4096,
                SkippedRecordCount: 0, CompactionNeeded: false));
        };

        var result = await producer(new MftBlockProduceRequest
        {
            DriveLetter = 'C',
            VolumeSerial = 0x0BADF00D,
            BlockPath = builder.BlockPath,
            DeleteOnClose = false,
            Progress = null
        }, CancellationToken.None);

        using (result.Block)
        {
            Assert.AreEqual('C', seen!.DriveLetter);
            Assert.AreEqual(0x0BADF00Du, seen.VolumeSerial);
            Assert.AreEqual(builder.BlockPath, seen.BlockPath);
            Assert.IsFalse(seen.DeleteOnClose);
            Assert.IsNull(seen.Progress);
            Assert.AreEqual(7ul, result.JournalId);
            Assert.AreEqual(4096L, result.NextUsn);
            Assert.AreEqual(0, result.SkippedRecordCount);
            Assert.IsFalse(result.CompactionNeeded);
            Assert.AreEqual(5u, result.Block.Header.RootRow);
        }
    }

    [TestMethod]
    public void FileIndexOptions_CarriesTheProducer()
    {
        MftBlockProducer producer = (_, _) => throw new NotSupportedException();
        var options = new FileIndexOptions { MftProducer = producer };
        Assert.AreSame(producer, options.MftProducer);
    }
}
