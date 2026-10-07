using System.Collections.Concurrent;
using MFTLib.Index;
using MFTLib.Tests.TestSupport;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace MFTLib.Tests.Index;

/// <summary>
///     A dump source opens its file anew for every scan: a rescan loads whatever the path then
///     names, a rescan that fails or is cancelled keeps the last good snapshot, and no two scans
///     share an opened input.
/// </summary>
[TestClass]
[DoNotParallelize]
public class MftDumpRescanTests
{
    const string NotesPath = "dump:/D/documents/Notes.txt";
    const string RenamedPath = "dump:/D/documents/Renamed.txt";
    const string InvalidFixupMessage = "The dump contains an invalid MFT record fixup.";

    string _directory = null!;

    [TestInitialize]
    public void Initialize()
    {
        _directory = MftDumpFixture.NewOwnedDirectory();
    }

    [TestCleanup]
    public void Cleanup()
    {
        MftDumpFixture.DeleteOwnedDirectory(_directory);
    }

    [TestMethod]
    [DataRow(1024)]
    [DataRow(4096)]
    public async Task Rescan_AfterThePathIsReplaced_PublishesTheNewFilesRecords(int replacementRecordSize)
    {
        var path = MftDumpFixture.WriteFile(_directory, MftDumpFixture.Standard());
        await using var index = await OpenAsync(MftIndexSources.FromMftDumpFile(path, 'D'));
        Assert.IsNotNull(index.Find(NotesPath));

        await File.WriteAllBytesAsync(path, Renamed(replacementRecordSize));
        await index.RescanAsync('D', CancellationToken.None);

        Assert.IsNull(index.Find(NotesPath));
        Assert.AreEqual(33L, index.Find(RenamedPath)!.Value.Size);
        var drive = index.Drives.Single();
        Assert.AreEqual(DriveState.Ready, drive.State);
        Assert.IsNull(drive.MftProducerFailureMessage);
        Assert.IsFalse(drive.WatchSupported);
    }

    [TestMethod]
    public async Task Rescan_OfARejectedReplacement_FailsWithItsMessageAndKeepsTheOldRecords()
    {
        var path = MftDumpFixture.WriteFile(_directory, MftDumpFixture.Standard());
        await using var index = await OpenAsync(MftIndexSources.FromMftDumpFile(path, 'D'));
        var invalid = MftDumpFixture.Standard();
        invalid[7 * 1024 + 510] ^= 0xFF;

        await File.WriteAllBytesAsync(path, invalid);
        var failure = await CatchAsync(() => index.RescanAsync('D', CancellationToken.None));

        Assert.IsInstanceOfType<InvalidOperationException>(failure);
        Assert.IsInstanceOfType<InvalidDataException>(failure.InnerException);
        Assert.AreEqual(InvalidFixupMessage, failure.InnerException.Message);
        Assert.AreEqual($"Drive D was not rescanned: {InvalidFixupMessage}", failure.Message);
        Assert.AreEqual(11L, index.Find(NotesPath)!.Value.Size, "the last good block still answers queries");
        var drive = index.Drives.Single();
        Assert.AreEqual(DriveState.Ready, drive.State);
        Assert.AreEqual(InvalidFixupMessage, drive.MftProducerFailureMessage);

        await File.WriteAllBytesAsync(path, Renamed(1024));
        await index.RescanAsync('D', CancellationToken.None);

        Assert.IsNotNull(index.Find(RenamedPath));
        Assert.IsNull(index.Drives.Single().MftProducerFailureMessage, "a later good rescan clears the failure");
    }

    [TestMethod]
    public async Task Rescan_OfADeletedDump_FailsAndKeepsTheOldRecords()
    {
        var path = MftDumpFixture.WriteFile(_directory, MftDumpFixture.Standard());
        await using var index = await OpenAsync(MftIndexSources.FromMftDumpFile(path, 'D'));

        File.Delete(path);
        var failure = await CatchAsync(() => index.RescanAsync('D', CancellationToken.None));

        Assert.IsInstanceOfType<IOException>(failure.InnerException);
        StringAssert.StartsWith(failure.InnerException.Message, "Failed to open file. Error: ");
        Assert.IsNotNull(index.Find(NotesPath));
    }

    [TestMethod]
    public async Task Rescan_Cancelled_KeepsTheOldRecords()
    {
        var path = MftDumpFixture.WriteFile(_directory, MftDumpFixture.Standard());
        await using var index = await OpenAsync(MftIndexSources.FromMftDumpFile(path, 'D'));
        await File.WriteAllBytesAsync(path, Renamed(1024));
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();

        var failure = await CatchAsync(() => index.RescanAsync('D', cancellation.Token));

        Assert.IsInstanceOfType<OperationCanceledException>(failure);
        Assert.IsNotNull(index.Find(NotesPath));
        Assert.IsNull(index.Find(RenamedPath));
        Assert.AreEqual(DriveState.Ready, index.Drives.Single().State);
    }

    [TestMethod]
    public async Task Rescan_CancelledWhileItsRecordsAreRead_KeepsTheOldRecordsAndReleasesTheInput()
    {
        var path = MftDumpFixture.WriteFile(_directory, MftDumpFixture.Standard());
        using var cancellation = new CancellationTokenSource();
        var producer = new MftDumpBlockProducer(path, CancelOnTheSecondScan(cancellation));
        await using var index = await OpenAsync(Source(producer, path));

        var failure = await CatchAsync(() => index.RescanAsync('D', cancellation.Token));

        Assert.IsInstanceOfType<OperationCanceledException>(failure);
        Assert.IsNotNull(index.Find(NotesPath));
        using var exclusive = new FileStream(path, FileMode.Open, FileAccess.ReadWrite, FileShare.None);
        Assert.IsTrue(exclusive.Length > 0, "the cancelled scan closed its input");
    }

    [TestMethod]
    public async Task Open_TwoIndexesFromOneSourceAtOnce_EachScanOpensItsOwnInput()
    {
        var path = MftDumpFixture.WriteFile(_directory, MftDumpFixture.Standard());
        var inputs = new ConcurrentBag<MftDumpInput>();
        using var bothOpened = new CountdownEvent(2);
        var producer = new MftDumpBlockProducer(path, HoldUntilBothOpened(inputs, bothOpened));
        var source = Source(producer, path);

        var indexes = await Task.WhenAll(OpenAsync(source), OpenAsync(source));

        Assert.AreEqual(2, inputs.Distinct().Count());
        await indexes[0].DisposeAsync();
        await using var remaining = indexes[1];
        Assert.AreEqual(DriveState.Ready, remaining.Drives.Single().State);
        Assert.AreEqual(11L, remaining.Find(NotesPath)!.Value.Size, "one index's disposal leaves the other's block mapped");
    }

    [TestMethod]
    public async Task Open_TwoSourcesWithTheSameKey_StayIndependent()
    {
        var first = MftDumpFixture.WriteFile(_directory, MftDumpFixture.Standard(), "first.mft");
        var second = MftDumpFixture.WriteFile(_directory, Renamed(4096), "second.mft");
        await using var firstIndex = await OpenAsync(MftIndexSources.FromMftDumpFile(first, 'D'));
        await using var secondIndex = await OpenAsync(MftIndexSources.FromMftDumpFile(second, 'd'));

        await File.WriteAllBytesAsync(first, Renamed(1024));
        await secondIndex.RescanAsync('D', CancellationToken.None);

        Assert.IsNotNull(firstIndex.Find(NotesPath), "the first index was not rescanned");
        Assert.IsNull(firstIndex.Find(RenamedPath));
        Assert.IsNotNull(secondIndex.Find(RenamedPath));
        Assert.IsNull(secondIndex.Find(NotesPath));
    }

    [TestMethod]
    public async Task StartWatching_OnAFactoryDumpSource_IsRefused()
    {
        var path = MftDumpFixture.WriteFile(_directory, MftDumpFixture.Standard());
        await using var index = await OpenAsync(MftIndexSources.FromMftDumpFile(path, 'D'));

        var refusal = await CatchAsync(() => index.StartWatchingAsync('D', CancellationToken.None));

        Assert.IsInstanceOfType<InvalidOperationException>(refusal);
        StringAssert.Contains(refusal.Message, MftIndexSource.NoWatchReason);
    }

    static MftDumpRecordSource CancelOnTheSecondScan(CancellationTokenSource cancellation)
    {
        var scans = 0;
        return (input, progress, token) =>
        {
            if (Interlocked.Increment(ref scans) == 2)
            {
                cancellation.Cancel();
            }

            return input.ReadRecordBatches(4096, progress, token);
        };
    }

    // Each scan waits, holding its opened input, until the other scan holds one too.
    static MftDumpRecordSource HoldUntilBothOpened(ConcurrentBag<MftDumpInput> inputs, CountdownEvent bothOpened) =>
        (input, progress, token) =>
        {
            inputs.Add(input);
            bothOpened.Signal();
            Assert.IsTrue(bothOpened.Wait(TimeSpan.FromMinutes(1), token), "both scans open an input");
            return input.ReadRecordBatches(4096, progress, token);
        };

    static MftIndexSource Source(MftDumpBlockProducer producer, string path) =>
        new(producer.ProduceAsync, dumpIdentity: new MftDumpSourceIdentity(path, 'D'));

    static Task<FileIndex> OpenAsync(MftIndexSource source) => FileIndex.OpenAsync(new FileIndexOptions
    {
        Drives = [new IndexedDrive('D', "dump:/D", 0)],
        NoCache = true,
        MftSource = source
    }, CancellationToken.None);

    // The standard tree with record 7 renamed and resized, at either record size.
    static byte[] Renamed(int recordSize)
    {
        var records = MftDumpFixture.StandardRecords();
        records[7] = new DumpRecord("Renamed.txt", 6) { Size = 33 };
        return MftDumpFixture.Build(recordSize, 16, records);
    }

    static async Task<Exception> CatchAsync(Func<Task> action)
    {
        try
        {
            await action();
        }
        catch (Exception exception)
        {
            return exception;
        }

        throw new AssertFailedException("The operation was expected to fail.");
    }
}
