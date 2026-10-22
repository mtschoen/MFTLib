using MFTLib.Index;
using MFTLib.Tests.TestSupport;
using MFTLibTestExtensions;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace MFTLib.Tests.Index;

[TestClass]
public sealed class FileIndexQueryTests : IAsyncDisposable
{
    IndexTreeFixture _fixture = null!;
    string TreeRoot => _fixture.TreeRoot;
    FileIndex _index = null!;

    [TestInitialize]
    public async Task Initialize()
    {
        _fixture = new IndexTreeFixture();
        Directory.CreateDirectory(Path.Combine(TreeRoot, "Documents", "Projects"));
        Directory.CreateDirectory(Path.Combine(TreeRoot, "Pictures"));
        await File.WriteAllTextAsync(Path.Combine(TreeRoot, "Documents", "Projects", "report.pdf"), new string('x', 100));
        await File.WriteAllTextAsync(Path.Combine(TreeRoot, "Pictures", "readme.md"), "world");
        await File.WriteAllTextAsync(Path.Combine(TreeRoot, "Pictures", "holiday.jpg"), new string('y', 5000));

        _index = await FileIndex.OpenAsync(_fixture.Options(), CancellationToken.None);
    }

    public async ValueTask DisposeAsync()
    {
        try
        {
            await _index.DisposeAsync();
        }
        finally
        {
            _fixture.Dispose();
        }
    }

    [TestMethod]
    public void Find_ResolvesAPathToItsEntry()
    {
        var entry = _index.Find(Path.Combine(TreeRoot, "Documents", "Projects", "report.pdf"));
        Assert.IsTrue(entry.HasValue);
        Assert.AreEqual(100L, entry.GetValueOrDefault().Size);
    }

    [TestMethod]
    public void Search_ExactMode_SpansTheWholeDrive()
    {
        var results = _index.Search(new SearchQuery("readme.md", NameMatchMode.Exact));
        Assert.HasCount(2, results);
    }

    [TestMethod]
    public void Search_ReturnsTheWholeMatchSetForSlicing()
    {
        var results = _index.Search(new SearchQuery("read"));
        Assert.HasCount(2, results);

        var firstPage = results.Take(1).ToArray();
        Assert.HasCount(1, firstPage);
    }

    [TestMethod]
    public void Search_UnderRestrictsToASubtree()
    {
        var pictures = _index.Find(Path.Combine(TreeRoot, "Pictures"))!.Value;
        var results = _index.Search(new SearchQuery("readme", Subtree: pictures));
        Assert.HasCount(1, results);
        Assert.AreEqual(Path.Combine(TreeRoot, "Pictures", "readme.md"), results[0].Path);
    }

    [TestMethod]
    public void Search_Under_DefaultAncestor_ThrowsArgumentException()
    {
        Assert.ThrowsExactly<ArgumentException>(
            () => _index.Search(new SearchQuery("readme", Subtree: default(FileEntry))));
    }

    [TestMethod]
    public void Root_ReturnsTheDriveRoot()
    {
        Assert.AreEqual(TreeRoot, _index.Root('T').Path);
    }

    [TestMethod]
    [DataRow(DriveState.Offline, 'T', false)]
    [DataRow(DriveState.Offline, 'T', true)]
    [DataRow(DriveState.Offline, 't', false)]
    [DataRow(DriveState.Offline, 't', true)]
    [DataRow(DriveState.Failed, 'T', false)]
    [DataRow(DriveState.Failed, 'T', true)]
    [DataRow(DriveState.Failed, 't', false)]
    [DataRow(DriveState.Failed, 't', true)]
    public async Task OpenAsync_RejectsBlocklessDuplicateDriveConfiguration(DriveState blocklessState,
        char duplicateLetter, bool blocklessFirst)
    {
        var policy = blocklessState == DriveState.Offline ? ProducerKind.Enumeration : ProducerKind.Mft;
        var ready = new IndexedDrive('T', TreeRoot, 1, policy);
        var blockless = new IndexedDrive(duplicateLetter,
            blocklessState == DriveState.Offline ? Path.Combine(TreeRoot, "absent") : TreeRoot, 2, policy);
        var producerCalls = 0;
        var exception = await Assert.ThrowsExactlyAsync<ArgumentException>(() => FileIndex.OpenAsync(new FileIndexOptions
        {
            Drives = blocklessFirst ? [blockless, ready] : [ready, blockless],
            CacheMode = CacheMode.Disabled,
            MftSource = new MftIndexSource((_, _) =>
            {
                Interlocked.Increment(ref producerCalls);
                return Task.FromException<MftBlockProduceResult>(new InvalidOperationException("producer must not run"));
            })
        }, CancellationToken.None));

        Assert.AreEqual("options", exception.ParamName);
        Assert.Contains("Drive T is listed more than once.", exception.Message);
        Assert.AreEqual(0, producerCalls);
    }

    [TestMethod]
    [DataRow(DriveState.Failed, 'T', "normal")]
    [DataRow(DriveState.Failed, 't', "normal")]
    [DataRow(DriveState.Offline, 'T', "normal")]
    [DataRow(DriveState.Offline, 't', "normal")]
    [DataRow(DriveState.Failed, 'T', "rescan")]
    [DataRow(DriveState.Failed, 't', "rescan")]
    [DataRow(DriveState.Failed, 'T', "dispose-at-admission")]
    [DataRow(DriveState.Failed, 't', "dispose-at-admission")]
    [DataRow(DriveState.Failed, 'T', "dispose-at-drive-read")]
    [DataRow(DriveState.Failed, 't', "dispose-at-drive-read")]
    public async Task Root_ConfiguredDriveWithoutBlock_ReportsCurrentState(DriveState state, char driveLetter,
        string scenario)
    {
        var canProduce = false;
        var producer = new SyntheticMftProducer(_ => [new SyntheticRow(5, "", 5) { IsDirectory = true }])
        {
            BeforeProduceAsync = (_, _) => canProduce
                ? Task.CompletedTask
                : Task.FromException(new InvalidOperationException("production is disabled"))
        };
        var index = await FileIndex.OpenAsync(new FileIndexOptions
        {
            Drives = [new IndexedDrive('T', state == DriveState.Offline ? Path.Combine(TreeRoot, "absent") : TreeRoot, 1)],
            CacheMode = CacheMode.Disabled,
            MftSource = SyntheticIndexSource.Create(producer)
        }, CancellationToken.None);

        try
        {
            var status = index.Drives.Single();
            Assert.AreEqual(state, status.State);
            Assert.IsNull(status.Block);

            Exception outcome;
            if (scenario == "rescan")
            {
                outcome = await RootAfterAdmissionAsync(index, driveLetter, async () =>
                {
                    canProduce = true;
                    await index.RescanAsync(['T'], CancellationToken.None);
                    Assert.AreEqual(DriveState.Ready, index.Drives.Single().State);
                    Assert.IsNotNull(index.Drives.Single().Block);
                });
            }
            else if (scenario.StartsWith("dispose-", StringComparison.Ordinal))
            {
                outcome = await RootAcrossDisposalAsync(index, driveLetter, scenario == "dispose-at-drive-read");
                if (scenario == "dispose-at-admission")
                {
                    Assert.IsInstanceOfType<OperationCanceledException>(outcome);
                    return;
                }
            }
            else
            {
                outcome = RootFailure(index, driveLetter);
            }

            Assert.IsInstanceOfType<ArgumentException>(outcome);
            var exception = (ArgumentException)outcome;
            Assert.Contains($"Drive {new DriveKey(driveLetter)} has no block (state: {state}).", exception.Message);
            Assert.IsFalse(exception.Message.Contains("not part of this index", StringComparison.Ordinal));
            Assert.AreEqual("drive", exception.ParamName);
        }
        finally
        {
            await index.DisposeAsync();
        }
    }

    [TestMethod]
    [DataRow('Z', "normal")]
    [DataRow('z', "normal")]
    [DataRow('Z', "dispose-at-admission")]
    [DataRow('z', "dispose-at-admission")]
    [DataRow('Z', "dispose-at-drive-read")]
    [DataRow('z', "dispose-at-drive-read")]
    public async Task Root_UnconfiguredDrive_ReportsNotPartOfIndex(char driveLetter, string scenario)
    {
        var outcome = scenario == "normal"
            ? RootFailure(_index, driveLetter)
            : await RootAcrossDisposalAsync(_index, driveLetter, scenario == "dispose-at-drive-read");
        if (scenario == "dispose-at-admission")
        {
            Assert.IsInstanceOfType<OperationCanceledException>(outcome);
            return;
        }

        Assert.IsInstanceOfType<ArgumentException>(outcome);
        var exception = (ArgumentException)outcome;
        Assert.Contains($"Drive {new DriveKey(driveLetter)} is not part of this index.", exception.Message);
        Assert.AreEqual("drive", exception.ParamName);
    }

    static Exception RootFailure(FileIndex index, char driveLetter, CancellationToken cancellationToken = default)
    {
        try
        {
            _ = index.Root(driveLetter, cancellationToken);
        }
        catch (Exception exception)
        {
            return exception;
        }

        Assert.Fail("Root unexpectedly returned an entry for a missing block.");
        throw new AssertFailedException();
    }

    static async Task<Exception> RootAfterAdmissionAsync(FileIndex index, char driveLetter, Func<Task> afterAdmission,
        CancellationToken cancellationToken = default)
    {
        var admitted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var released = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        index.Hooks.ReaderAdmitted = () =>
        {
            admitted.TrySetResult();
            released.Task.GetAwaiter().GetResult();
        };
        var reader = Task.Run(() => RootFailure(index, driveLetter, cancellationToken));
        try
        {
            await admitted.Task;
            await afterAdmission();
        }
        finally
        {
            released.TrySetResult();
            await reader;
            index.Hooks.ReaderAdmitted = null;
        }

        return await reader;
    }

    static async Task<Exception> RootAcrossDisposalAsync(FileIndex index, char driveLetter, bool atDriveRead)
    {
        using var callerCancellation = new CancellationTokenSource();
        var cancellationDelivered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        Task? disposal = null;
        index.Hooks.LifecycleGatesTakenForDisposal = () => cancellationDelivered.TrySetResult();
        index.Hooks.DrivesReadBeforeLock = () =>
        {
            disposal = index.DisposeAsync().AsTask();
            cancellationDelivered.Task.GetAwaiter().GetResult();
        };
        try
        {
            return await RootAfterAdmissionAsync(index, driveLetter, async () =>
            {
                if (!atDriveRead)
                {
                    disposal = index.DisposeAsync().AsTask();
                    await cancellationDelivered.Task;
                }
            }, callerCancellation.Token);
        }
        finally
        {
            index.Hooks.DrivesReadBeforeLock = null;
            index.Hooks.LifecycleGatesTakenForDisposal = null;
            await (disposal ?? index.DisposeAsync().AsTask());
            Assert.IsFalse(callerCancellation.IsCancellationRequested);
        }
    }

    [TestMethod]
    public void Children_OfTheRootAreTheTopLevelDirectories()
    {
        var names = _index.Root('T').Children().Select(child => child.Name)
            .OrderBy(name => name, StringComparer.Ordinal).ToArray();
        Assert.AreSequenceEqual(["Documents", "Pictures"], names);
    }

    [TestMethod]
    public void Open_ReadsTheRealFileBehindAnEnumerationEntry()
    {
        var entry = _index.Find(Path.Combine(TreeRoot, "Documents", "readme.md"))!.Value;
        using var stream = entry.Open(FileAccess.Read);
        using var reader = new StreamReader(stream);
        Assert.AreEqual("hello", reader.ReadToEnd());
    }

    [TestMethod]
    public void Search_AfterARescanSeesNewFiles()
    {
        File.WriteAllText(Path.Combine(TreeRoot, "Documents", "added.md"), "added");
        _index.RescanAsync(['T'], CancellationToken.None).GetAwaiter().GetResult();

        Assert.HasCount(1, _index.Search(new SearchQuery("added")));
    }

    [TestMethod]
    public void HandleFromAnOldSnapshot_StaysReadableAcrossARescan()
    {
        var before = _index.Find(Path.Combine(TreeRoot, "Documents", "readme.md"))!.Value;
        _index.RescanAsync(['T'], CancellationToken.None).GetAwaiter().GetResult();

        // The old block stays mapped because this handle still references its snapshot.
        Assert.AreEqual("readme.md", before.Name);
        Assert.AreEqual(Path.Combine(TreeRoot, "Documents", "readme.md"), before.Path);
    }
}
