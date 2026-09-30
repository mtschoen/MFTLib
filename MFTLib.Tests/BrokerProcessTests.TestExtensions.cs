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
        var handle = explicitOptions
            ? BrokerTestHarness.StartInProcess(CreateHost(), writer, sections.Create, new BrokerTestHarnessOptions())
            : BrokerTestHarness.StartInProcess(CreateHost(), writer, sections.Create);
        var process = handle.Process;
        try
        {
            var volume = await process.QueryVolumeAsync('C', CancellationToken.None).WaitAsync(HangGuard);
            Assert.AreEqual(Volume.MftValidDataLength, volume.MftValidDataLength);
            Assert.AreEqual(Volume.BytesPerFileRecordSegment, volume.BytesPerFileRecordSegment);
            Assert.AreEqual(Volume.MftRecordCount, volume.MftRecordCount);
            await handle.DisposeAsync().AsTask().WaitAsync(HangGuard);
            await process.Ended.WaitAsync(HangGuard);
        }
        finally
        {
            await handle.DisposeAsync();
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
        var handle = BrokerTestHarness.StartInProcess(
            CreateHost(timeProvider: new UnavailableHostClock(failure)), writer, sections.Create);
        var process = handle.Process;
        try
        {
            await Assert.ThrowsExceptionAsync<BrokerChannelLostException>(() =>
                process.QueryVolumeAsync('C', CancellationToken.None).WaitAsync(HangGuard));
            await process.DisposeAsync().AsTask().WaitAsync(HangGuard);
            await process.Ended.WaitAsync(HangGuard);
        }
        finally
        {
            await process.DisposeAsync();
        }
    }

    static BlockScanTarget ScanTarget() =>
        new(Path.Combine(Path.GetTempPath(), "control-only-" + Guid.NewGuid().ToString("N") + ".mlix"), 1, true);

    [TestMethod]
    public async Task Crash_EndsTheClientAsABrokerDeathDoes()
    {
        await using var handle = BrokerTestHarness.StartInProcess(CreateHost());
        var process = handle.Process;
        await process.QueryVolumeAsync('C', CancellationToken.None).WaitAsync(HangGuard);

        handle.Crash();
        handle.Crash();

        var reason = await process.Ended.WaitAsync(HangGuard);
        Assert.AreNotEqual("The broker process was disposed.", reason);
        Assert.IsTrue(process.Ended.IsCompleted);
        await Assert.ThrowsExceptionAsync<BrokerChannelLostException>(() =>
            process.QueryVolumeAsync('C', CancellationToken.None).WaitAsync(HangGuard));
    }

    [TestMethod]
    public async Task ControlOnlyHarness_AnswersControlRequestsWithNoScanPlumbing()
    {
        await using var handle = BrokerTestHarness.StartInProcess(
            new JournalBrokerHost(_ => Armed, queryVolumeInfo: _ => Volume), new BrokerTestHarnessOptions());

        var volume = await handle.Process.QueryVolumeAsync('C', CancellationToken.None).WaitAsync(HangGuard);

        Assert.AreEqual(Volume.MftRecordCount, volume.MftRecordCount);
    }

    [TestMethod]
    public async Task ControlOnlyHarness_ScanFailsClearlyBecauseItHasNoSectionFactory()
    {
        await using var handle = BrokerTestHarness.StartInProcess(
            new JournalBrokerHost(_ => Armed, queryVolumeInfo: _ => Volume));

        var failure = await Assert.ThrowsExceptionAsync<InvalidOperationException>(() =>
            handle.Process.ScanDriveAsync('C', ScanTarget(), new BrokerScanOptions(), CancellationToken.None)
                .WaitAsync(HangGuard));

        StringAssert.Contains(failure.Message, "block section factory");
    }

    [TestMethod]
    public async Task HostWithoutScanSources_RefusesAScanWithAClearError()
    {
        using var sections = new TestBlockSections();
        using var writer = new RecordingBlockSectionWriter(sections.Resolve);
        await using var handle = BrokerTestHarness.StartInProcess(
            new JournalBrokerHost(_ => Armed, queryVolumeInfo: _ => Volume), writer, sections.Create);

        var failure = await Assert.ThrowsExceptionAsync<InvalidOperationException>(() =>
            handle.Process.ScanDriveAsync('C', ScanTarget(), new BrokerScanOptions(), CancellationToken.None)
                .WaitAsync(HangGuard));

        StringAssert.Contains(failure.Message, "no scan source");
    }
}
