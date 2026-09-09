using System.Buffers;
using System.Buffers.Binary;
using System.Runtime.CompilerServices;
using MFTLib.Tests.TestSupport;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace MFTLib.Tests;

[TestClass]
public partial class JournalBrokerHostTests : BrokerBlockTestBase
{
    static readonly string[] KeepFileNamesGit = [".git"];
    static readonly string[] KeepFileNamesGitUppercase = [".GIT"];
    static readonly string[] KeepFileNamesNonMatching = ["other.txt"];

    static readonly MftRecord[] DirectoryIndexSampleRecords =
    [
        new(100, 5, new MftRecordFields(3, FileAttributes.Directory), "repo", null),
        new(101, 100, new MftRecordFields(1, FileAttributes.Archive), ".git", null),
        new(102, 100, new MftRecordFields(1, FileAttributes.Archive), "file.txt", null)
    ];

    async Task<RecordingBlockSectionWriter> ServeDirectoryIndexAsync(
        MftRecord[] records, IReadOnlyCollection<string>? keepFileNames)
    {
        var (clientSide, serverSide) = DuplexStream.CreatePair();
        var host = MakeFakeHost(records, Array.Empty<UsnJournalEntry>());

        var request = new ArrayBufferWriter<byte>();
        BrokerProtocol.WriteArmAndScan(request,
            $"C:0:0:mftlib-scan-C:{(int)BrokerScanProfile.DirectoryIndex}", keepFileNames);
        await clientSide.WriteAsync(request.WrittenMemory);
        await clientSide.FlushAsync();

        var writer = CreateSectionWriter();
        await host.ServeAsync(serverSide, writer, true, CancellationToken.None);
        return writer;
    }

    static async IAsyncEnumerable<(UsnJournalEntry[], UsnJournalCursor)> FakeWatch(
        (UsnJournalEntry[], UsnJournalCursor)[] batches,
        [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        foreach (var batch in batches)
        {
            yield return batch;
        }

        // A live watch stays open until cancelled; mimic that so the serve loop
        // does not end before the test cancels.
        await Task.Delay(Timeout.Infinite, cancellationToken);
    }

    // Yields its batches and then completes normally (no infinite delay), so a
    // consumer's await-foreach exits without cancellation or an exception.
    static async IAsyncEnumerable<(UsnJournalEntry[], UsnJournalCursor)> FiniteWatch(
        (UsnJournalEntry[], UsnJournalCursor)[] batches)
    {
        foreach (var batch in batches)
        {
            yield return batch;
        }

        await Task.CompletedTask;
    }

    static async IAsyncEnumerable<(UsnJournalEntry[], UsnJournalCursor)> ThrowingWatchMidStream()
    {
        yield return ([SampleEntry()], new UsnJournalCursor(7UL, 110L));
        await Task.Yield();
        throw new InvalidOperationException("journal wrapped mid-stream");
    }

    static async Task<BrokerFrame> ReadOneFrameAsync(Stream stream)
    {
        var header = new byte[4];
        await stream.ReadExactlyAsync(header);
        var totalLength = BinaryPrimitives.ReadInt32LittleEndian(header);
        var frameBytes = new byte[4 + totalLength];
        header.CopyTo(frameBytes.AsMemory());
        await stream.ReadExactlyAsync(frameBytes.AsMemory(4, totalLength));
        return BrokerProtocol.ReadFrame(frameBytes, out _);
    }

    static MftRecord SampleRecord()
    {
        return new MftRecord(100, 5, new MftRecordFields(1, FileAttributes.Archive, 2048), "a.txt", null);
    }

    static UsnJournalEntry SampleEntry()
    {
        return JournalEntryFactory.Create(
            100, 110, "a.txt", UsnReason.FileCreate | UsnReason.Close);
    }

    static JournalBrokerHost MakeFakeHost(MftRecord[] records, UsnJournalEntry[] catchUp)
    {
        return CreateHost(
            _ => new UsnJournalCursor(7UL, 0L),
            (_, _, _) => [records],
            (_, cursor) => (catchUp, new UsnJournalCursor(cursor.JournalId, cursor.NextUsn + catchUp.Length)));
    }

    static List<BrokerFrame> ReadAllFrames(Stream stream)
    {
        using var memory = new MemoryStream();
        stream.CopyTo(memory);
        var bytes = memory.ToArray();
        var frames = new List<BrokerFrame>();
        var offset = 0;
        while (offset < bytes.Length)
        {
            var frame = BrokerProtocol.ReadFrame(bytes.AsSpan(offset), out var consumed);
            frames.Add(frame);
            offset += consumed;
        }

        return frames;
    }

    [TestMethod]
    public async Task ServeOnce_ScanProgress_WritePhase_PassesProgressToStreamingWriterAndReportsBytes()
    {
        var originalThrottle = JournalBrokerHost._progressThrottleInterval;
        try
        {
            JournalBrokerHost._progressThrottleInterval = TimeSpan.Zero;

            var (clientSide, serverSide) = DuplexStream.CreatePair();
            var host = CreateHost(
                _ => new UsnJournalCursor(7UL, 0L),
                (_, progress, _) =>
                {
                    progress?.Report(new BlockWriteProgress(100, 0, 1000, null, BrokerScanPhase.Parsing));
                    return
                    [
                        [new MftRecord(1, 0, new MftRecordFields(1, FileAttributes.Archive, 100), "r1.txt", null)],
                        [new MftRecord(2, 0, new MftRecordFields(1, FileAttributes.Archive, 200), "r2.txt", null)]
                    ];
                },
                (_, cursor) => (Array.Empty<UsnJournalEntry>(), cursor));

            var request = new ArrayBufferWriter<byte>();
            BrokerProtocol.WriteArmAndScan(request, "C:0:0:mftlib-write-prog-C");
            await clientSide.WriteAsync(request.WrittenMemory);
            await clientSide.FlushAsync();

            using var blockWriter = CreateSectionWriter();
            await host.ServeAsync(serverSide, blockWriter, true, CancellationToken.None);
            await serverSide.DisposeAsync();

            var frames = ReadAllFrames(clientSide);
            var progressFrames = frames.Where(f => f.Kind == BrokerFrameKind.ScanProgress).ToList();

            Assert.IsTrue(progressFrames.Count >= 2, "Expected intermediate parse and write progress frames");
            Assert.IsTrue(progressFrames.Any(f => f.Progress?.BytesProcessed > 0),
                "At least one progress frame must report BytesProcessed > 0 from the write phase");

            var finalProgress = progressFrames.Last().Progress!.Value;
            Assert.AreEqual(1000L, finalProgress.TotalRecords);
            Assert.IsTrue(finalProgress.BytesProcessed > 0);
            Assert.AreEqual(finalProgress.BytesProcessed, finalProgress.TotalBytes);
        }
        finally
        {
            JournalBrokerHost._progressThrottleInterval = originalThrottle;
        }
    }
}
