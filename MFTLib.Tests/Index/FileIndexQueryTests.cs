using MFTLib.Index;
using MFTLib.Tests.TestSupport;
using MFTLibTestExtensions;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace MFTLib.Tests.Index;

[TestClass]
public sealed class FileIndexQueryTests : IndexTreeTestBase, IAsyncDisposable
{
    FileIndex? _index;

    [TestInitialize]
    public async Task Initialize()
    {
        Directory.CreateDirectory(Path.Combine(TreeRoot, "Documents", "Projects"));
        Directory.CreateDirectory(Path.Combine(TreeRoot, "Pictures"));
        await File.WriteAllTextAsync(Path.Combine(TreeRoot, "Documents", "Projects", "report.pdf"), new string('x', 100),
            TestContext.CancellationToken);
        await File.WriteAllTextAsync(Path.Combine(TreeRoot, "Pictures", "readme.md"), "world",
            TestContext.CancellationToken);
        await File.WriteAllTextAsync(Path.Combine(TreeRoot, "Pictures", "holiday.jpg"), new string('y', 5000),
            TestContext.CancellationToken);

        _index = await FileIndex.OpenAsync(Fixture.Options(), CancellationToken.None);
    }

    public async ValueTask DisposeAsync()
    {
        try
        {
            if (_index is not null)
            {
                await _index.DisposeAsync();
            }
        }
        finally
        {
            Dispose();
        }
    }

    [TestMethod]
    public void Find_ResolvesAPathToItsEntry()
    {
        var entry = _index!.Find(Path.Combine(TreeRoot, "Documents", "Projects", "report.pdf"), TestContext.CancellationToken);
        Assert.IsTrue(entry.HasValue);
        Assert.AreEqual(100L, entry.GetValueOrDefault().Size);
    }

    [TestMethod]
    public void Search_ExactMode_SpansTheWholeRoot()
    {
        var results = _index!.Search(new SearchQuery("readme.md", NameMatchMode.Exact), TestContext.CancellationToken);
        Assert.HasCount(2, results);
    }

    [TestMethod]
    public void Search_ReturnsTheWholeMatchSetForSlicing()
    {
        var results = _index!.Search(new SearchQuery("read"), TestContext.CancellationToken);
        Assert.HasCount(2, results);

        var firstPage = results.Take(1).ToArray();
        Assert.HasCount(1, firstPage);
    }

    [TestMethod]
    public void Search_UnderRestrictsToASubtree()
    {
        var pictures = _index!.Find(Path.Combine(TreeRoot, "Pictures"), TestContext.CancellationToken)!.Value;
        var results = _index!.Search(new SearchQuery("readme", Subtree: pictures), TestContext.CancellationToken);
        Assert.HasCount(1, results);
        Assert.AreEqual(Path.Combine(TreeRoot, "Pictures", "readme.md"), results[0].Path);
    }

    [TestMethod]
    public void Search_Under_DefaultAncestor_ThrowsArgumentException()
    {
        Assert.ThrowsExactly<ArgumentException>(
            () => _index!.Search(new SearchQuery("readme", Subtree: default(FileEntry)), TestContext.CancellationToken));
    }

    [TestMethod]
    public void Root_ReturnsTheRootRoot()
    {
        Assert.AreEqual(TreeRoot, _index!.Root(TestContext.CancellationToken).Path);
    }

    [TestMethod]
    [DataRow(RootState.Offline, 'T', false)]
    [DataRow(RootState.Offline, 'T', true)]
    [DataRow(RootState.Offline, 't', false)]
    [DataRow(RootState.Offline, 't', true)]
    [DataRow(RootState.Failed, 'T', false)]
    [DataRow(RootState.Failed, 'T', true)]
    [DataRow(RootState.Failed, 't', false)]
    [DataRow(RootState.Failed, 't', true)]
    public async Task FileIndexSet_RejectsBlocklessDuplicateRootConfiguration(RootState blocklessState,
        char duplicateLetter, bool blocklessFirst)
    {
        var policy = blocklessState == RootState.Offline ? ProducerKind.Enumeration : ProducerKind.Mft;
        var ready = SyntheticRoot.Create(new VolumeKey('T'), TreeRoot, 1, ProducerKind.Enumeration);
        var blockless = SyntheticRoot.Create(new VolumeKey(duplicateLetter),
            blocklessState == RootState.Offline ? Path.Combine(TreeRoot, "absent") : TreeRoot, 2, policy);
        await using var readyIndex = await FileIndex.OpenAsync(new FileIndexOptions
        {

            CacheMode = CacheMode.Disabled
        }.WithSyntheticRoot(ready), CancellationToken.None);
        await using var blocklessIndex = await FileIndex.OpenAsync(new FileIndexOptions
        {

            CacheMode = CacheMode.Disabled,
            MftSource = new MftIndexSource((_, _) =>
                Task.FromException<MftBlockProduceResult>(new InvalidOperationException("producer failed")))
        }.WithSyntheticRoot(blockless), CancellationToken.None);

        var exception = Assert.ThrowsExactly<ArgumentException>(() => new FileIndexSet(
            blocklessFirst ? [blocklessIndex, readyIndex] : [readyIndex, blocklessIndex]));

        Assert.AreEqual("indices", exception.ParamName);
        Assert.Contains("Root T is listed more than once.", exception.Message);
    }

    [TestMethod]
    [DataRow(RootState.Failed, 'T', "normal")]
    [DataRow(RootState.Failed, 't', "normal")]
    [DataRow(RootState.Offline, 'T', "normal")]
    [DataRow(RootState.Offline, 't', "normal")]
    [DataRow(RootState.Failed, 'T', "rescan")]
    [DataRow(RootState.Failed, 't', "rescan")]
    [DataRow(RootState.Failed, 'T', "rescan-cancelled")]
    [DataRow(RootState.Failed, 't', "rescan-cancelled")]
    [DataRow(RootState.Failed, 'T', "dispose-at-admission")]
    [DataRow(RootState.Failed, 't', "dispose-at-admission")]
    [DataRow(RootState.Failed, 'T', "dispose-at-drive-read")]
    [DataRow(RootState.Failed, 't', "dispose-at-drive-read")]
    public async Task Root_ConfiguredRootWithoutBlock_ReportsCurrentState(RootState state, char driveLetter,
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

            CacheMode = CacheMode.Disabled,
            MftSource = SyntheticIndexSource.Create(producer)
        }.WithSyntheticRoot(SyntheticRoot.Create(new VolumeKey('T'), state == RootState.Offline ? Path.Combine(TreeRoot, "absent") : TreeRoot, 1)), CancellationToken.None);

        try
        {
            var status = index.Status;
            Assert.AreEqual(state, status.State);
            Assert.IsNull(status.Block);

            Exception outcome;
            if (scenario.StartsWith("rescan", StringComparison.Ordinal))
            {
                using var rescanCancellation = scenario == "rescan-cancelled"
                    ? new CancellationTokenSource()
                    : CancellationTokenSource.CreateLinkedTokenSource(TestContext.CancellationToken);
                if (scenario == "rescan-cancelled")
                {
                    await rescanCancellation.CancelAsync();
                }

                outcome = await RootAfterAdmissionAsync(index, async () =>
                {
                    canProduce = true;
                    await index.RescanAsync(CancellationToken.None);
                    Assert.AreEqual(RootState.Ready, index.Status.State);
                    Assert.IsNotNull(index.Status.Block);
                }, rescanCancellation.Token);

                if (scenario == "rescan-cancelled")
                {
                    Assert.IsInstanceOfType<OperationCanceledException>(outcome);
                    return;
                }
            }
            else if (scenario.StartsWith("dispose-", StringComparison.Ordinal))
            {
                outcome = await RootAcrossDisposalAsync(index, scenario == "dispose-at-drive-read");
                if (scenario == "dispose-at-admission")
                {
                    Assert.IsInstanceOfType<OperationCanceledException>(outcome);
                    return;
                }
            }
            else
            {
                outcome = RootFailure(index, TestContext.CancellationToken);
            }

            Assert.IsInstanceOfType<InvalidOperationException>(outcome);
            Assert.Contains($"Root {new VolumeKey(driveLetter)} has no block (state: {state}).", outcome.Message);
            Assert.IsFalse(outcome.Message.Contains("not part of this index", StringComparison.Ordinal));
        }
        finally
        {
            await index.DisposeAsync();
        }
    }

    static Exception RootFailure(FileIndex index, CancellationToken cancellationToken = default)
    {
        try
        {
            _ = index.Root(cancellationToken);
        }
        catch (Exception exception)
        {
            return exception;
        }

        Assert.Fail("Root unexpectedly returned an entry for a missing block.");
        throw new AssertFailedException();
    }

    static async Task<Exception> RootAfterAdmissionAsync(FileIndex index, Func<Task> afterAdmission,
        CancellationToken cancellationToken = default)
    {
        var admitted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var released = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        index.Hooks.ReaderAdmitted = () =>
        {
            admitted.TrySetResult();
            try
            {
                released.Task.GetAwaiter().GetResult();
            }
            catch (OperationCanceledException)
            {
            }
        };

        // The token is query input, not a scheduling token: even a pre-cancelled query must run.
        var reader = Task.Run(() => RootFailure(index, cancellationToken), CancellationToken.None);
        try
        {
            var cancellationTask = Task.Delay(Timeout.Infinite, cancellationToken);
            var completed = await Task.WhenAny(admitted.Task, reader, cancellationTask);
            if (completed == admitted.Task)
            {
                await afterAdmission();
            }
            else if (completed == cancellationTask)
            {
                cancellationToken.ThrowIfCancellationRequested();
            }
        }
        catch (OperationCanceledException)
        {
        }
        finally
        {
            released.TrySetResult();
            index.Hooks.ReaderAdmitted = null;
            try
            {
                await reader;
            }
            catch (OperationCanceledException)
            {
            }
        }

        return await reader;
    }

    static async Task<Exception> RootAcrossDisposalAsync(FileIndex index, bool atRootRead)
    {
        using var callerCancellation = new CancellationTokenSource();
        var cancellationDelivered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        Task? disposal = null;
        index.Hooks.LifecycleGatesTakenForDisposal = () => cancellationDelivered.TrySetResult();
        index.Hooks.RootsReadBeforeLock = () =>
        {
            disposal = index.DisposeAsync().AsTask();
            cancellationDelivered.Task.GetAwaiter().GetResult();
        };
        try
        {
            return await RootAfterAdmissionAsync(index, async () =>
            {
                if (!atRootRead)
                {
                    disposal = index.DisposeAsync().AsTask();
                    await cancellationDelivered.Task;
                }
            }, callerCancellation.Token);
        }
        finally
        {
            index.Hooks.RootsReadBeforeLock = null;
            index.Hooks.LifecycleGatesTakenForDisposal = null;
            await (disposal ?? index.DisposeAsync().AsTask());
            Assert.IsFalse(callerCancellation.IsCancellationRequested);
        }
    }

    [TestMethod]
    public void Children_OfTheRootAreTheTopLevelDirectories()
    {
        var names = _index!.Root(TestContext.CancellationToken).Children(TestContext.CancellationToken).Select(child => child.Name)
            .OrderBy(name => name, StringComparer.Ordinal).ToArray();
        Assert.AreSequenceEqual(["Documents", "Pictures"], names);
    }

    [TestMethod]
    public void Open_ReadsTheRealFileBehindAnEnumerationEntry()
    {
        var entry = _index!.Find(Path.Combine(TreeRoot, "Documents", "readme.md"), TestContext.CancellationToken)!.Value;
        using var stream = entry.Open(FileAccess.Read);
        using var reader = new StreamReader(stream);
        Assert.AreEqual("hello", reader.ReadToEnd());
    }

    [TestMethod]
    public void Search_AfterARescanSeesNewFiles()
    {
        File.WriteAllText(Path.Combine(TreeRoot, "Documents", "added.md"), "added");
        _index!.RescanAsync(CancellationToken.None).GetAwaiter().GetResult();

        Assert.HasCount(1, _index!.Search(new SearchQuery("added"), TestContext.CancellationToken));
    }

    [TestMethod]
    public void HandleFromAnOldSnapshot_StaysReadableAcrossARescan()
    {
        var before = _index!.Find(Path.Combine(TreeRoot, "Documents", "readme.md"), TestContext.CancellationToken)!.Value;
        _index!.RescanAsync(CancellationToken.None).GetAwaiter().GetResult();

        // The old block stays mapped because this handle still references its snapshot.
        Assert.AreEqual("readme.md", before.Name);
        Assert.AreEqual(Path.Combine(TreeRoot, "Documents", "readme.md"), before.Path);
    }

    public TestContext TestContext { get; set; } = null!;
}
