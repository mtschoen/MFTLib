using System.Text.RegularExpressions;
using MFTLib.Index;
using MFTLib.Tests.TestSupport;
using MFTLibTestExtensions;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace MFTLib.Tests.Index;

/// <summary>
///     The library compares the scan shape a block was built under on warm start: a change to any row-shaping
///     option records <see cref="BlockValidationResult.WrongScanShape" /> and cold-scans, or fails a cache-only
///     root with <see cref="RootFailureKind.CacheIdentityMismatch" />. A pruned path counts only on the root
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

    FileIndexOptions EnumerationOptions(List<string> diagnostics, CacheMode mode = CacheMode.Persistent) => new FileIndexOptions
    {

        CacheDirectory = Cache,
        CacheMode = mode,
        Diagnostics = line =>
        {
            lock (diagnostics)
            {
                diagnostics.Add(line);
            }
        }
    }.WithSyntheticRoot(SyntheticRoot.Create(new VolumeKey('A'), RootA, 1, ProducerKind.Enumeration));

    FileIndexOptions ForB(FileIndexOptions baseOptions) => baseOptions.WithSyntheticRoot(SyntheticRoot.Create(new VolumeKey('B'), RootB, 2, ProducerKind.Enumeration));

    static bool RecordedWrongScanShape(List<string> diagnostics)
    {
        lock (diagnostics)
        {
            return diagnostics.Any(line => line.Contains("cache validation failed: WrongScanShape", StringComparison.Ordinal));
        }
    }

    async Task OpenOnce(FileIndexOptions options)
    {
        await using var indexA = await FileIndex.OpenAsync(options, TestContext.CancellationToken);
        await using var indexB = await FileIndex.OpenAsync(ForB(options), TestContext.CancellationToken);
        Assert.AreEqual(RootState.Ready, indexA.Status.State);
        Assert.AreEqual(RootState.Ready, indexB.Status.State);
    }

    FileIndexOptions Changed(string option, List<string> diagnostics)
    {
        var options = EnumerationOptions(diagnostics);
        return option switch
        {
            "names" => options with { DirectoryWalk = new DirectoryWalkOptions { PrunedDirectoryNames = ["build"] } },
            "pattern-text" => options with
            {
                DirectoryWalk = new DirectoryWalkOptions
                {
                    PrunedDirectoryNamePatterns = [new Regex(@"^\.git$", RegexOptions.IgnoreCase)]
                }
            },
            "pattern-options" => options with
            {
                DirectoryWalk = new DirectoryWalkOptions
                {
                    PrunedDirectoryNamePatterns = [new Regex(@"^\.git$", RegexOptions.None)]
                }
            },
            "paths" => options with { DirectoryWalk = new DirectoryWalkOptions { PrunedDirectoryPaths = [RootA, RootB] } },
            "attributes" => options with { DirectoryWalk = new DirectoryWalkOptions { PrunedDirectoryAttributes = FileAttributes.Hidden } },
            "dot" => options with { DirectoryWalk = new DirectoryWalkOptions { PruneDotDirectories = true } },
            _ => throw new ArgumentOutOfRangeException(nameof(option), option, "unknown option")
        };
    }

    [TestMethod]
    [DataRow("names")]
    [DataRow("pattern-text")]
    [DataRow("pattern-options")]
    [DataRow("paths")]
    [DataRow("attributes")]
    [DataRow("dot")]
    public async Task ChangingAnEnumerationOption_RecordsWrongScanShapeAndColdScans(string label)
    {
        var diagnostics = new List<string>();
        await OpenOnce(EnumerationOptions(diagnostics));

        var reopenedOptions = Changed(label, diagnostics);
        await using var reopenedA = await FileIndex.OpenAsync(reopenedOptions, TestContext.CancellationToken);
        await using var reopenedB = await FileIndex.OpenAsync(ForB(reopenedOptions), TestContext.CancellationToken);

        Assert.IsTrue(RecordedWrongScanShape(diagnostics), label);
        Assert.AreEqual(BlockOrigin.ProducedByScan, reopenedA.Status.Block?.Origin, label);
        Assert.AreEqual(BlockOrigin.ProducedByScan, reopenedB.Status.Block?.Origin, label);
    }

    [TestMethod]
    public async Task ChangingBetweenTwoPatternOptionsOnly_RecordsWrongScanShapeAndColdScans()
    {
        var diagnostics = new List<string>();
        await OpenOnce(EnumerationOptions(diagnostics) with
        {
            DirectoryWalk = new DirectoryWalkOptions
            {
                PrunedDirectoryNamePatterns = [new Regex(@"^\.git$", RegexOptions.None)]
            }
        });

        var options = EnumerationOptions(diagnostics) with
        {
            DirectoryWalk = new DirectoryWalkOptions
            {
                PrunedDirectoryNamePatterns = [new Regex(@"^\.git$", RegexOptions.IgnoreCase)]
            }
        };
        await using var reopenedA = await FileIndex.OpenAsync(options, TestContext.CancellationToken);
        await using var reopenedB = await FileIndex.OpenAsync(ForB(options), TestContext.CancellationToken);

        Assert.IsTrue(RecordedWrongScanShape(diagnostics));
        Assert.AreEqual(BlockOrigin.ProducedByScan, reopenedA.Status.Block?.Origin);
        Assert.AreEqual(BlockOrigin.ProducedByScan, reopenedB.Status.Block?.Origin);
    }

    [TestMethod]
    public void ForEnumeration_DistinguishesPatternTextOptionsAndUnpairedSurrogatesWhileIgnoringOrderDuplicatesAndTimeout()
    {
        static ulong Shape(params Regex[] patterns) =>
            ScanShape.ForEnumeration(new DirectoryWalkOptions { PrunedDirectoryNamePatterns = patterns }, "/root");

        var lower = Shape(new Regex(@"^\.git$", RegexOptions.None));
        var upper = Shape(new Regex(@"^\.GIT$", RegexOptions.None));
        var ignoreCase = Shape(new Regex(@"^\.git$", RegexOptions.IgnoreCase));
        var otherText = Shape(new Regex(@"^build$", RegexOptions.None));

        Assert.AreNotEqual(lower, upper, "pattern text is not folded by the root comparison");
        Assert.AreNotEqual(lower, ignoreCase, "pattern options are part of the scan shape");
        Assert.AreNotEqual(lower, otherText, "pattern text is part of the scan shape");

        var reorderedAndDuplicated = Shape(
            new Regex(@"^build$", RegexOptions.None, TimeSpan.FromSeconds(5)),
            new Regex(@"^\.git$", RegexOptions.IgnoreCase, TimeSpan.FromSeconds(1)),
            new Regex(@"^build$", RegexOptions.None, TimeSpan.FromSeconds(2)));
        var canonical = Shape(
            new Regex(@"^\.git$", RegexOptions.IgnoreCase, TimeSpan.FromSeconds(10)),
            new Regex(@"^build$", RegexOptions.None));
        Assert.AreEqual(canonical, reorderedAndDuplicated, "equivalent pairs and differing timeouts share a shape");

        var sameTextDifferentOptions = Shape(
            new Regex(@"^\.git$", RegexOptions.IgnoreCase),
            new Regex(@"^\.git$", RegexOptions.None));
        var sameTextDifferentOptionsReordered = Shape(
            new Regex(@"^\.git$", RegexOptions.None),
            new Regex(@"^\.git$", RegexOptions.IgnoreCase));
        Assert.AreEqual(sameTextDifferentOptions, sameTextDifferentOptionsReordered);
        Assert.AreNotEqual(lower, sameTextDifferentOptions);

        var surrogateFirst = Shape(new Regex(MalformedText.Make(0xD800, true), RegexOptions.None));
        var surrogateSecond = Shape(new Regex(MalformedText.Make(0xD801, true), RegexOptions.None));
        Assert.AreNotEqual(surrogateFirst, surrogateSecond, "unpaired UTF-16 code units are encoded losslessly");
    }

    [TestMethod]
    public async Task UnchangedOptions_WarmStartBothRoots()
    {
        var diagnostics = new List<string>();
        var walk = new DirectoryWalkOptions
        {
            PrunedDirectoryNames = ["build"],
            PrunedDirectoryNamePatterns =
            [
                new Regex(@"^node_modules$", RegexOptions.None, TimeSpan.FromSeconds(1)),
                new Regex(@"^\.git$", RegexOptions.IgnoreCase, TimeSpan.FromSeconds(2))
            ],
            PrunedDirectoryPaths = [RootB + "/.git"]
        };
        await OpenOnce(EnumerationOptions(diagnostics) with { DirectoryWalk = walk });

        // Equal content in other collections, reordered equivalent regexes and another spelling of the same path are the same shape.
        var respelled = new DirectoryWalkOptions
        {
            PrunedDirectoryNames = ["build", "build"],
            PrunedDirectoryNamePatterns =
            [
                new Regex(@"^\.git$", RegexOptions.IgnoreCase, TimeSpan.FromSeconds(5)),
                new Regex(@"^node_modules$", RegexOptions.None, TimeSpan.FromSeconds(10)),
                new Regex(@"^\.git$", RegexOptions.IgnoreCase)
            ],
            PrunedDirectoryPaths = [Path.Combine(RootB, ".git") + Path.DirectorySeparatorChar]
        };
        var options = EnumerationOptions(diagnostics) with { DirectoryWalk = respelled };
        await using var reopenedA = await FileIndex.OpenAsync(options, TestContext.CancellationToken);
        await using var reopenedB = await FileIndex.OpenAsync(ForB(options), TestContext.CancellationToken);

        Assert.IsFalse(RecordedWrongScanShape(diagnostics));
        Assert.AreEqual(BlockOrigin.WarmStartedFromCache, reopenedA.Status.Block?.Origin);
        Assert.AreEqual(BlockOrigin.WarmStartedFromCache, reopenedB.Status.Block?.Origin);
    }

    [TestMethod]
    public async Task EditingAPrunedPathUnderRootB_LeavesRootAsBlockAdopted()
    {
        var diagnostics = new List<string>();
        await OpenOnce(EnumerationOptions(diagnostics) with
        {
            DirectoryWalk = new DirectoryWalkOptions { PrunedDirectoryPaths = [Path.Combine(RootB, "build")] }
        });

        var options = EnumerationOptions(diagnostics) with
        {
            DirectoryWalk = new DirectoryWalkOptions { PrunedDirectoryPaths = [Path.Combine(RootB, ".git")] }
        };
        await using var reopenedA = await FileIndex.OpenAsync(options, TestContext.CancellationToken);
        await using var reopenedB = await FileIndex.OpenAsync(ForB(options), TestContext.CancellationToken);

        Assert.AreEqual(BlockOrigin.WarmStartedFromCache, reopenedA.Status.Block?.Origin);
        Assert.AreEqual(BlockOrigin.ProducedByScan, reopenedB.Status.Block?.Origin);
        Assert.IsNotNull(reopenedB.Find(Path.Combine(RootB, "build", "output", "app.dll"), TestContext.CancellationToken));
        Assert.IsNotNull(reopenedA.Find(Path.Combine(RootA, "build", "output", "app.dll"), TestContext.CancellationToken),
            "root A's warm block still holds its rows");
    }

    [TestMethod]
    public async Task CacheOnlyOpenUnderAnotherShape_FailsWithCacheIdentityMismatch()
    {
        var diagnostics = new List<string>();
        await OpenOnce(EnumerationOptions(diagnostics));

        var options = EnumerationOptions(diagnostics, CacheMode.ExistingOnly) with
        {
            DirectoryWalk = new DirectoryWalkOptions
            {
                PrunedDirectoryNamePatterns = [new Regex(@"^\.git$", RegexOptions.IgnoreCase)]
            }
        };
        await using var reopenedA = await FileIndex.OpenAsync(options, TestContext.CancellationToken);
        await using var reopenedB = await FileIndex.OpenAsync(ForB(options), TestContext.CancellationToken);

        foreach (var status in new[] { reopenedA.Status, reopenedB.Status })
        {
            Assert.AreEqual(RootState.Failed, status.State);
            Assert.AreEqual(RootFailureKind.CacheIdentityMismatch, status.Failure?.Kind);
        }

        Assert.IsTrue(RecordedWrongScanShape(diagnostics));
        Assert.HasCount(2, Directory.GetFiles(Cache, "*.mlix"), "a cache-only open leaves both rejected blocks in place");
    }

    [TestMethod]
    public void ForMft_DependsOnlyOnIncludeFreed()
    {
        var withoutFreed = ScanShape.ForMft(includeFreed: false);
        var withFreed = ScanShape.ForMft(includeFreed: true);

        Assert.AreEqual(withoutFreed, ScanShape.ForMft(includeFreed: false));
        Assert.AreEqual(withFreed, ScanShape.ForMft(includeFreed: true));
        Assert.AreNotEqual(withoutFreed, withFreed);
    }

    [TestMethod]
    public void ForMft_HashesTheProducerKindThenTheFlag()
    {
        byte[] payload = [1, 0, 0, 0, 0];
        var expected = System.Buffers.Binary.BinaryPrimitives.ReadUInt64LittleEndian(
            System.Security.Cryptography.SHA256.HashData(payload));

        Assert.AreEqual(expected, ScanShape.ForMft(includeFreed: false));
    }

    FileIndexOptions MftOptions(bool includeFreed, List<string> diagnostics,
        CacheMode mode = CacheMode.Persistent) => new FileIndexOptions
        {

            CacheDirectory = Cache,
            CacheMode = mode,
            MftSource = new MftIndexSource(ProduceMft, includeFreed: includeFreed),
            Diagnostics = diagnostics.Add
        }.WithSyntheticRoot(SyntheticRoot.Create(new VolumeKey('T'), RootA, 123));

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
            writer.Complete(Moment, null, cancellationToken: CancellationToken.None);
            return Task.FromResult(new MftBlockProduceResult(block, 0));
        }
        catch
        {
            block.Dispose();
            throw;
        }
    }

    [TestMethod]
    [DataRow(true, DisplayName = "IncludeFreed")]
    public async Task ChangingAnMftOption_RecordsWrongScanShapeAndColdScans(bool includeFreed)
    {
        var diagnostics = new List<string>();
        await using (var first = await FileIndex.OpenAsync(MftOptions(false, diagnostics), TestContext.CancellationToken))
        {
            Assert.AreEqual(RootState.Ready, first.Status.State);
        }

        await using var reopened = await FileIndex.OpenAsync(MftOptions(includeFreed, diagnostics),
            TestContext.CancellationToken);

        Assert.IsTrue(RecordedWrongScanShape(diagnostics));
        Assert.AreEqual(BlockOrigin.ProducedByScan, reopened.Status.Block?.Origin);
        Assert.AreEqual(2, _mftProductions);
    }

    [TestMethod]
    public async Task MftRoots_IgnoreTheWalkOptions()
    {
        var diagnostics = new List<string>();
        await using (var first = await FileIndex.OpenAsync(MftOptions(false, diagnostics), TestContext.CancellationToken))
        {
            Assert.AreEqual(RootState.Ready, first.Status.State);
        }

        await using var reopened = await FileIndex.OpenAsync(MftOptions(false, diagnostics) with
        {
            DirectoryWalk = new DirectoryWalkOptions
            {
                PruneDotDirectories = true,
                PrunedDirectoryNames = ["build"],
                PrunedDirectoryNamePatterns = [new Regex(@"^\.git$", RegexOptions.IgnoreCase)]
            }
        }, TestContext.CancellationToken);

        Assert.IsFalse(RecordedWrongScanShape(diagnostics));
        Assert.AreEqual(BlockOrigin.WarmStartedFromCache, reopened.Status.Block?.Origin);
        Assert.AreEqual(1, _mftProductions);
    }

    [TestMethod]
    public async Task AProducerReturningAnotherShape_FailsTheRoot()
    {
        var diagnostics = new List<string>();
        var options = MftOptions(false, diagnostics) with
        {
            MftSource = new MftIndexSource((request, token) => ProduceMft(request with { ScanShape = request.ScanShape + 1 }, token))
        };

        await using var index = await FileIndex.OpenAsync(options, TestContext.CancellationToken);

        var status = index.Status;
        Assert.AreEqual(RootFailureKind.ProducerFailed, status.Failure?.Kind);
        Assert.Contains("scan shape", status.Failure?.Message!);
    }
}
