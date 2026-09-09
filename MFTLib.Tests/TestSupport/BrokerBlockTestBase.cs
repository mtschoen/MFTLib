using System.Buffers;
using MFTLib.Index;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace MFTLib.Tests.TestSupport;

public abstract class BrokerBlockTestBase
{
    readonly List<IDisposable> _resources = [];

    protected TResource RegisterResource<TResource>(TResource resource) where TResource : IDisposable
    {
        _resources.Add(resource);
        return resource;
    }

    protected BlockFile CreateBlock(BlockFileCreateOptions options)
    {
        var block = BlockFile.Create(options);
        _resources.Add(block);
        return block;
    }

    protected RecordingBlockSectionWriter CreateSectionWriter()
    {
        var writer = new RecordingBlockSectionWriter();
        _resources.Add(writer);
        return writer;
    }

    protected static BrokerScanOptions CreateOptions(BrokerScanProfile profile = BrokerScanProfile.Full,
        IReadOnlyCollection<string>? keepFileNames = null) =>
        new()
        {
            Profile = profile,
            KeepFileNames = keepFileNames,
            BlockTargets = CreateTargets()
        };

    protected static Dictionary<string, BlockScanTarget> CreateTargets() =>
        new[] { "C", "D", "E", "F", "G" }.ToDictionary(letter => letter,
            _ => new BlockScanTarget(Path.Combine(Path.GetTempPath(), $"broker-test-{Guid.NewGuid():N}.bin"), 123, true));

    protected static async Task ReplyToVolumeQueryAsync(Stream stream, BrokerFrame request)
    {
        var response = new ArrayBufferWriter<byte>();
        foreach (var token in request.DrivesSpec!.Split(','))
        {
            BrokerProtocol.WriteVolumeInfo(response, token.Split(':')[0], 128, 1024, 128 * 1024);
        }

        await stream.WriteAsync(response.WrittenMemory);
    }

    protected static JournalBrokerHost CreateHost(UsnJournalCursorQuery queryCursor,
        MftRecordBatchSource scanDrive, UsnJournalCatchUpSource readJournal, JournalBatchSource? watchDrive = null) =>
        new(queryCursor, scanDrive, readJournal, watchDrive);

    [TestCleanup]
    public void DisposeRecordedBlocks()
    {
        foreach (var resource in _resources)
        {
            resource.Dispose();
        }

        _resources.Clear();
    }
}
