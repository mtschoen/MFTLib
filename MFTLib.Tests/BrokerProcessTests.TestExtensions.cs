using MFTLib.Tests.TestSupport;
using MFTLibTestExtensions;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace MFTLib.Tests;

public partial class BrokerProcessTests
{
    sealed class UnavailableHostClock(Exception failure) : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => throw failure;
    }

    [DataTestMethod]
    [DataRow(false)]
    [DataRow(true)]
    public async Task PublicHarnessOverloads_QueryAndDispose(bool explicitOptions)
    {
        using var sections = new TestBlockSections();
        using var writer = new RecordingBlockSectionWriter(sections.Resolve);
        var process = explicitOptions
            ? BrokerTestHarness.StartInProcess(CreateHost(), writer, sections.Create, new BrokerTestHarnessOptions())
            : BrokerTestHarness.StartInProcess(CreateHost(), writer, sections.Create);
        try
        {
            var volume = await process.QueryVolumeAsync('C', CancellationToken.None).WaitAsync(HangGuard);
            Assert.AreEqual(Volume.MftValidDataLength, volume.MftValidDataLength);
            Assert.AreEqual(Volume.BytesPerFileRecordSegment, volume.BytesPerFileRecordSegment);
            Assert.AreEqual(Volume.MftRecordCount, volume.MftRecordCount);
            await process.DisposeAsync().AsTask().WaitAsync(HangGuard);
            Assert.IsTrue(process.HasEnded);
        }
        finally
        {
            await process.DisposeAsync();
        }
    }

    sealed class NonDisposingBlockSectionWriter : IBlockSectionWriter
    {
        public BlockWriteResult Write(
            string sectionName,
            UsnJournalCursor cursor,
            IEnumerable<IReadOnlyList<MftRecord>> batches,
            MftBlockRowFilter filter,
            BlockWriteReporting reporting,
            CancellationToken cancellationToken) =>
            throw new NotImplementedException();
    }

    static (string SectionName, MFTLib.Index.BlockFile Block, IDisposable Lifetime) NonDisposingCreateBlockSection(
        char _1, MFTLib.Index.BlockFileCreateOptions _2) =>
        throw new NotImplementedException();

    [DataTestMethod]
    [DataRow("host")]
    [DataRow("blockSectionWriter")]
    [DataRow("createBlockSection")]
    [DataRow("options")]
    public void PublicHarness_RejectsNullArgumentsBeforeStarting(string parameter)
    {
        var writer = new NonDisposingBlockSectionWriter();
        BrokerBlockSectionFactory createBlockSection = NonDisposingCreateBlockSection;
        var options = new BrokerTestHarnessOptions();
        var failure = Assert.ThrowsException<ArgumentNullException>(() =>
            BrokerTestHarness.StartInProcess(parameter == "host" ? null! : CreateHost(),
                parameter == "blockSectionWriter" ? null! : writer,
                parameter == "createBlockSection" ? null! : createBlockSection,
                parameter == "options" ? null! : options));
        Assert.AreEqual(parameter, failure.ParamName);
    }

    [DataTestMethod]
    [DataRow(false)]
    [DataRow(true)]
    public async Task HostStartupFailure_EndsProcessAndDisposalDoesNotThrow(bool cancelled)
    {
        Exception failure = cancelled ? new OperationCanceledException("host cancelled")
            : new InvalidOperationException("host unavailable");
        using var sections = new TestBlockSections();
        using var writer = new RecordingBlockSectionWriter(sections.Resolve);
        var process = BrokerTestHarness.StartInProcess(
            CreateHost(timeProvider: new UnavailableHostClock(failure)), writer, sections.Create);
        try
        {
            await Assert.ThrowsExceptionAsync<BrokerChannelLostException>(() =>
                process.QueryVolumeAsync('C', CancellationToken.None).WaitAsync(HangGuard));
            await process.DisposeAsync().AsTask().WaitAsync(HangGuard);
            Assert.IsTrue(process.HasEnded);
        }
        finally
        {
            await process.DisposeAsync();
        }
    }
}
