using MFTLib.Index;
using MFTLib.Tests.TestSupport;
using MFTLibTestExtensions;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace MFTLib.Tests.Index;

/// <summary>
///     The library compares the scan shape a block was built under on warm start: a change to any row-shaping
///     option records <see cref="BlockValidationResult.WrongScanShape" /> and cold-scans, or fails a cache-only
///     drive with <see cref="DriveFailureKind.CacheIdentityMismatch" />. A pruned path counts only on the drive
///     whose root contains it.
/// </summary>
[TestClass]
[DoNotParallelize]
public sealed class FileIndexScanShapeTests : IDisposable
{
    static readonly DateTime Moment = new(2026, 10, 18, 0, 0, 0, DateTimeKind.Utc);
    TempDirectory _directoryOwner = null!;
    string _directory = null!;
    IDisposable? _journalScope;
    int _mftProductions;

    public TestContext TestContext { get; set; } = null!;

    string RootA => Path.Combine(_directory, "a");
    string RootB => Path.Combine(_directory, "b");
    string Cache => Path.Combine(_directory, "cache");

    [TestInitialize]
    public void Initialize()
    {
        _directoryOwner = new TempDirectory("mftlib-scan-shape");
        _directory = _directoryOwner.Path;
        foreach (var root in new[] { RootA, RootB })
        {
            Directory.CreateDirectory(Path.Combine(root, "build", "output"));
            Directory.CreateDirectory(Path.Combine(root, ".git"));
            File.WriteAllText(Path.Combine(root, "build", "output", "app.dll"), "x");
        }

        _journalScope = JournalIsolation.OverrideJournalWindow(
            _ => new SyntheticJournalWindow(7, 100, 4096, 4096, 65536));
    }

    [TestCleanup]
    public void Cleanup()
    {
        _journalScope?.Dispose();
        _journalScope = null;
        _directoryOwner.Dispose();
    }

    public void Dispose() => Cleanup();

    FileIndexOptions EnumerationOptions(List<string> diagnostics, CacheMode mode = CacheMode.Persistent) => new()
    {
        Drives =
        [
            new IndexedDrive('A', RootA, 1, ProducerKind.Enumeration),
            new IndexedDrive('B', RootB, 2, ProducerKind.Enumeration)
        ],
        CacheDirectory = Cache,
        CacheMode = mode,
        Diagnostics = line =>
        {
            lock (diagnostics)
            {
                diagnostics.Add(line);
            }
        }
    };

    static BlockOrigin? OriginOf(FileIndex index, DriveKey drive) =>
        index.Drives.Single(status => status.Drive == drive).Block?.Origin;

    static bool RecordedWrongScanShape(List<string> diagnostics)
    {
        lock (diagnostics)
        {
            return diagnostics.Any(line => line.Contains("cache validation failed: WrongScanShape", StringComparison.Ordinal));
        }
    }

    async Task OpenOnce(FileIndexOptions options)
    {
        await using var index = await FileIndex.OpenAsync(options, TestContext.CancellationToken);
        Assert.IsTrue(index.Drives.All(status => status.State == DriveState.Ready));
    }

    FileIndexOptions Changed(string option, List<string> diagnostics)
    {
        var options = EnumerationOptions(diagnostics);
        return option switch
        {
            "retention" => options with { RetainedFileNames = ["app.dll"] },
            "names" => options with { DirectoryWalk = new DirectoryWalkOptions { PrunedDirectoryNames = ["build"] } },
            "paths" => options with { DirectoryWalk = new DirectoryWalkOptions { PrunedDirectoryPaths = [RootA, RootB] } },
            "attributes" => options with { DirectoryWalk = new DirectoryWalkOptions { PrunedDirectoryAttributes = FileAttributes.Hidden } },
            "dot" => options with { DirectoryWalk = new DirectoryWalkOptions { PruneDotDirectories = true } },
            _ => throw new ArgumentOutOfRangeException(nameof(option), option, "unknown option")
        };
    }

    [TestMethod]
    [DataRow("retention")]
    [DataRow("names")]
    [DataRow("paths")]
    [DataRow("attributes")]
    [DataRow("dot")]
    public async Task ChangingAnEnumerationOption_RecordsWrongScanShapeAndColdScans(string label)
    {
        var diagnostics = new List<string>();
        await OpenOnce(EnumerationOptions(diagnostics));

        var reopenedOptions = Changed(label, diagnostics);
        await using var reopened = await FileIndex.OpenAsync(reopenedOptions, TestContext.CancellationToken);

        Assert.IsTrue(RecordedWrongScanShape(diagnostics), label);
        Assert.AreEqual(BlockOrigin.ProducedByScan, OriginOf(reopened, 'A'), label);
        Assert.AreEqual(BlockOrigin.ProducedByScan, OriginOf(reopened, 'B'), label);
    }

    [TestMethod]
    public async Task ReplacementCharacterInARetainedName_IsADistinctShapeFromTheNameWithoutIt()
    {
        var diagnostics = new List<string>();
        var withReplacement = EnumerationOptions(diagnostics) with { RetainedFileNames = ["app\uFFFD.dll"] };
        await OpenOnce(withReplacement);

        var withoutReplacement = EnumerationOptions(diagnostics) with { RetainedFileNames = ["app.dll"] };
        await using var reopened = await FileIndex.OpenAsync(withoutReplacement, TestContext.CancellationToken);

        Assert.IsTrue(RecordedWrongScanShape(diagnostics));
        Assert.AreEqual(BlockOrigin.ProducedByScan, OriginOf(reopened, 'A'));
    }

    [TestMethod]
    public async Task UnchangedOptions_WarmStartBothDrives()
    {
        var diagnostics = new List<string>();
        var walk = new DirectoryWalkOptions { PrunedDirectoryNames = ["build"], PrunedDirectoryPaths = [RootB + "/.git"] };
        await OpenOnce(EnumerationOptions(diagnostics) with { DirectoryWalk = walk });

        // Equal content in other collections and another spelling of the same path are the same shape.
        var respelled = new DirectoryWalkOptions
        {
            PrunedDirectoryNames = ["build", "build"],
            PrunedDirectoryPaths = [Path.Combine(RootB, ".git") + Path.DirectorySeparatorChar]
        };
        await using var reopened = await FileIndex.OpenAsync(
            EnumerationOptions(diagnostics) with { DirectoryWalk = respelled }, TestContext.CancellationToken);

        Assert.IsFalse(RecordedWrongScanShape(diagnostics));
        Assert.AreEqual(BlockOrigin.WarmStartedFromCache, OriginOf(reopened, 'A'));
        Assert.AreEqual(BlockOrigin.WarmStartedFromCache, OriginOf(reopened, 'B'));
    }

    [TestMethod]
    public async Task EnumerationRetentionCasingFollowsTheDriveComparison()
    {
        var diagnostics = new List<string>();
        await OpenOnce(EnumerationOptions(diagnostics) with { RetainedFileNames = ["app.dll"] });

        await using var reopened = await FileIndex.OpenAsync(EnumerationOptions(diagnostics) with
        {
            RetainedFileNames = ["APP.DLL"]
        }, TestContext.CancellationToken);

        var expectedOrigin = OperatingSystem.IsWindows() ? BlockOrigin.WarmStartedFromCache : BlockOrigin.ProducedByScan;
        Assert.AreEqual(!OperatingSystem.IsWindows(), RecordedWrongScanShape(diagnostics));
        Assert.AreEqual(expectedOrigin, OriginOf(reopened, 'A'));
        Assert.AreEqual(expectedOrigin, OriginOf(reopened, 'B'));
    }

    [TestMethod]
    public async Task EditingAPrunedPathUnderDriveB_LeavesDriveAsBlockAdopted()
    {
        var diagnostics = new List<string>();
        await OpenOnce(EnumerationOptions(diagnostics) with
        {
            DirectoryWalk = new DirectoryWalkOptions { PrunedDirectoryPaths = [Path.Combine(RootB, "build")] }
        });

        await using var reopened = await FileIndex.OpenAsync(EnumerationOptions(diagnostics) with
        {
            DirectoryWalk = new DirectoryWalkOptions { PrunedDirectoryPaths = [Path.Combine(RootB, ".git")] }
        }, TestContext.CancellationToken);

        Assert.AreEqual(BlockOrigin.WarmStartedFromCache, OriginOf(reopened, 'A'));
        Assert.AreEqual(BlockOrigin.ProducedByScan, OriginOf(reopened, 'B'));
        Assert.IsNotNull(reopened.Find(Path.Combine(RootB, "build", "output", "app.dll"), TestContext.CancellationToken));
        Assert.IsNotNull(reopened.Find(Path.Combine(RootA, "build", "output", "app.dll"), TestContext.CancellationToken),
            "drive A's warm block still holds its rows");
    }

    [TestMethod]
    public async Task CacheOnlyOpenUnderAnotherShape_FailsWithCacheIdentityMismatch()
    {
        var diagnostics = new List<string>();
        await OpenOnce(EnumerationOptions(diagnostics));

        await using var reopened = await FileIndex.OpenAsync(EnumerationOptions(diagnostics, CacheMode.ExistingOnly) with
        {
            DirectoryWalk = new DirectoryWalkOptions { PruneDotDirectories = true }
        }, TestContext.CancellationToken);

        foreach (var status in reopened.Drives)
        {
            Assert.AreEqual(DriveState.Failed, status.State);
            Assert.AreEqual(DriveFailureKind.CacheIdentityMismatch, status.Failure?.Kind);
        }

        Assert.IsTrue(RecordedWrongScanShape(diagnostics));
        Assert.HasCount(2, Directory.GetFiles(Cache, "*.mlix"), "a cache-only open leaves both rejected blocks in place");
    }

    FileIndexOptions MftOptions(bool includeFreed, IReadOnlyCollection<string>? retained, List<string> diagnostics,
        CacheMode mode = CacheMode.Persistent) => new()
        {
            Drives = [new IndexedDrive('T', RootA, 123)],
            CacheDirectory = Cache,
            CacheMode = mode,
            RetainedFileNames = retained,
            MftSource = new MftIndexSource(ProduceMft, includeFreed: includeFreed),
            Diagnostics = diagnostics.Add
        };

    Task<MftBlockProduceResult> ProduceMft(MftBlockProduceRequest request, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        _mftProductions++;
        // aislop-ignore-next-line dotnet/IDISP001 -- ownership moves to the returned result; the catch disposes it on failure
        var block = TestBlocks.Create(request.BlockPath, ProducerKind.Mft, 16, 1024,
            volumeSerial: request.VolumeSerial, rootRow: 5,
            deleteOnClose: request.DeleteOnClose, cacheTag: request.CacheTag,
            scanShape: request.ScanShape);
        try
        {
            var writer = new BlockWriter(block);
            Assert.IsTrue(writer.TryWriteRow(5, ".",
                new RowColumns(5, RowFlags.InUse | RowFlags.Directory, 0, 0, Moment.Ticks, 1)));
            writer.SetJournalCursor(7, 4096);
            writer.Complete(Moment, null);
            return Task.FromResult(new MftBlockProduceResult(block, 0));
        }
        catch
        {
            block.Dispose();
            throw;
        }
    }

    [TestMethod]
    [DataRow(true, null, DisplayName = "IncludeFreed")]
    [DataRow(false, "HEAD", DisplayName = "Retention")]
    public async Task ChangingAnMftOption_RecordsWrongScanShapeAndColdScans(bool includeFreed, string? retainedName)
    {
        var diagnostics = new List<string>();
        await using (var first = await FileIndex.OpenAsync(MftOptions(false, null, diagnostics), TestContext.CancellationToken))
        {
            Assert.AreEqual(DriveState.Ready, first.Drives.Single().State);
        }

        IReadOnlyCollection<string>? retained = retainedName is null ? null : [retainedName];
        await using var reopened = await FileIndex.OpenAsync(MftOptions(includeFreed, retained, diagnostics),
            TestContext.CancellationToken);

        Assert.IsTrue(RecordedWrongScanShape(diagnostics));
        Assert.AreEqual(BlockOrigin.ProducedByScan, OriginOf(reopened, 'T'));
        Assert.AreEqual(2, _mftProductions);
    }

    [TestMethod]
    public async Task MftDrives_IgnoreTheWalkOptions()
    {
        var diagnostics = new List<string>();
        await using (var first = await FileIndex.OpenAsync(MftOptions(false, ["head"], diagnostics), TestContext.CancellationToken))
        {
            Assert.AreEqual(DriveState.Ready, first.Drives.Single().State);
        }

        // MFT retention compares ignoring case, so another spelling of the same name is the same shape.
        await using var reopened = await FileIndex.OpenAsync(MftOptions(false, ["HEAD"], diagnostics) with
        {
            DirectoryWalk = new DirectoryWalkOptions { PruneDotDirectories = true, PrunedDirectoryNames = ["build"] }
        }, TestContext.CancellationToken);

        Assert.IsFalse(RecordedWrongScanShape(diagnostics));
        Assert.AreEqual(BlockOrigin.WarmStartedFromCache, OriginOf(reopened, 'T'));
        Assert.AreEqual(1, _mftProductions);
    }

    [TestMethod]
    public async Task AProducerReturningAnotherShape_FailsTheDrive()
    {
        var diagnostics = new List<string>();
        var options = MftOptions(false, null, diagnostics) with
        {
            MftSource = new MftIndexSource((request, token) => ProduceMft(request with { ScanShape = request.ScanShape + 1 }, token))
        };

        await using var index = await FileIndex.OpenAsync(options, TestContext.CancellationToken);

        var status = index.Drives.Single();
        Assert.AreEqual(DriveFailureKind.ProducerFailed, status.Failure?.Kind);
        Assert.Contains("scan shape", status.Failure?.Message!);
    }
}
