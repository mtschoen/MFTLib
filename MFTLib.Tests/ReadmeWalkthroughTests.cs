using System.Globalization;
using System.Runtime.Versioning;
using MFTLib.Index;
using MFTLib.Tests.TestSupport;
using MFTLibTestExtensions;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace MFTLib.Tests;

/// <summary>
///     Compiles and executes the C# query walkthroughs from <c>README.md</c>: the saved-dump
///     walkthrough over <see cref="MftDumpFixture" /> and the one-shot local walkthrough over
///     the public <see cref="MFTLibTestExtensions" /> synthetic source seam.
/// </summary>
[TestClass]
[DoNotParallelize]
public sealed class ReadmeWalkthroughTests : IDisposable
{
    static readonly DateTime ObservedTimestamp = new(2026, 10, 14, 12, 0, 0, DateTimeKind.Utc);

    TempDirectory _tempDirectory = null!;
    string _directory => _tempDirectory.Path;
    string _dumpFile = null!;

    public TestContext TestContext { get; set; } = null!;

    [TestInitialize]
    public void Initialize()
    {
        _tempDirectory = new TempDirectory("mftlib-dump", create: false);
        _dumpFile = MftDumpFixture.WriteFile(_directory, MftDumpFixture.Standard());
    }

    public void Dispose()
    {
        NativeTestHooks.NativeResetTestState();
        IndexedDrive.Dependencies.ResetToDefaults();
        _tempDirectory.Dispose();
    }

    [TestMethod]
    public async Task DumpWalkthrough_ExactNameSearchOverStandardDump_ReturnsNotesTxtAndDisposesBlock()
    {
        var filePath = _dumpFile;
        var cancellationToken = TestContext.CancellationTokenSource.Token;
        var options = new FileIndexOptions
        {
            MftSource = MftIndexSources.FromMftDumpFile(filePath, 'D')
        };

        var lines = new List<string>();
        FileEntry retainedEntry = default;
        await using (var index = await FileIndex.OpenAsync(options, cancellationToken))
        {
            var status = index.Drives.Single();
            if (status.State != DriveState.Ready)
            {
                throw new InvalidOperationException(
                    $"Dump scan failed ({status.Failure?.Kind}): {status.Failure?.Message}");
            }

            Assert.IsNull(status.Failure);
            Assert.IsFalse(status.Watch.IsSupported);
            Assert.IsNull(status.Block!.CacheSlot);

            var query = new SearchQuery("Notes.txt", NameMatchMode.Exact);
            foreach (var entry in index.Enumerate(query, cancellationToken))
            {
                retainedEntry = entry;
                var observedSize = entry.IsSizeKnown ? entry.Size.ToString(CultureInfo.InvariantCulture) : "?";
                lines.Add($"{entry.Path}  {observedSize}");
            }
        }

        CollectionAssert.AreEqual(new[] { "dump:/D/documents/Notes.txt  11" }, lines);
        Assert.IsTrue(retainedEntry.IsValid);
        Assert.IsTrue(retainedEntry.IsDisposed);
        Assert.ThrowsException<ObjectDisposedException>(() => _ = retainedEntry.Path);
    }

    [TestMethod]
    public async Task DumpWalkthrough_MissingOrEmptyDump_ReportsFailedDriveInsteadOfEmptySuccess()
    {
        var emptyDumpFile = MftDumpFixture.WriteFile(_directory, [], "empty.mft");
        var missingDumpFile = Path.Combine(_directory, "missing.mft");

        foreach (var filePath in new[] { missingDumpFile, emptyDumpFile })
        {
            var cancellationToken = TestContext.CancellationTokenSource.Token;
            var options = new FileIndexOptions
            {
                MftSource = MftIndexSources.FromMftDumpFile(filePath, 'D')
            };

            await using var index = await FileIndex.OpenAsync(options, cancellationToken);
            var status = index.Drives.Single();

            Assert.AreEqual(DriveState.Failed, status.State, filePath);
            Assert.IsNull(status.Block, filePath);
            Assert.AreEqual(DriveFailureKind.ProducerFailed, status.Failure?.Kind, filePath);
            Assert.IsFalse(string.IsNullOrWhiteSpace(status.Failure?.Message), filePath);

            var failure = Assert.ThrowsException<InvalidOperationException>(() =>
            {
                if (status.State != DriveState.Ready)
                {
                    throw new InvalidOperationException(
                        $"Dump scan failed ({status.Failure?.Kind}): {status.Failure?.Message}");
                }
            });
            StringAssert.Contains(failure.Message, "Dump scan failed (ProducerFailed):");
        }
    }

    [TestMethod]
    [SupportedOSPlatform("windows")]
    public async Task LocalOneShotWalkthrough_CompilesPublicRecipeAndRunsMultipleQueriesOverOneScan()
    {
        IndexedDrive.Dependencies.VolumeSerialReader = _ => 0x0BADF00Du;

        var cancellationToken = TestContext.CancellationTokenSource.Token;
        var drive = IndexedDrive.FromWindowsVolume('C');
        var productionOptions = new FileIndexOptions
        {
            Drives = [drive],
            CacheMode = CacheMode.Disabled,
            MftSource = MftIndexSources.FromLocalVolumes()
        };
        Assert.AreEqual('C', productionOptions.Drives.Single().Drive);
        Assert.AreEqual(CacheMode.Disabled, productionOptions.CacheMode);
        Assert.IsNotNull(productionOptions.MftSource);

        // The production setup above is compiled as shown; execution swaps in the owned directory so the
        // synthetic scan never depends on a real C:\ root.
        drive = drive with { RootDirectory = _directory };

        var scanCount = 0;
        var syntheticProducer = new SyntheticMftProducer(_ =>
        {
            Interlocked.Increment(ref scanCount);
            return
            [
                new SyntheticRow(5, ".", 5) { IsDirectory = true, LastWriteTime = ObservedTimestamp },
                new SyntheticRow(6, "logs", 5) { IsDirectory = true, LastWriteTime = ObservedTimestamp },
                new SyntheticRow(7, "app.log", 6) { Size = 42, LastWriteTime = ObservedTimestamp },
                new SyntheticRow(8, "pending.log", 6) { Size = null, LastWriteTime = ObservedTimestamp },
                new SyntheticRow(9, "archive.log", 5) { IsDirectory = true, LastWriteTime = ObservedTimestamp },
                new SyntheticRow(10, "Notes.txt", 5) { Size = 11, LastWriteTime = ObservedTimestamp }
            ];
        });

        var ignoredCachePath = Path.Combine(_directory, "never-created-cache");
        var options = productionOptions with
        {
            Drives = [drive],
            CacheDirectory = ignoredCachePath,
            MftSource = SyntheticIndexSource.Create(syntheticProducer)
        };

        var lines = new List<string>();
        FileEntry retainedEntry = default;
        await using (var index = await FileIndex.OpenAsync(options, cancellationToken))
        {
            var status = index.Drives.Single();
            if (status.State != DriveState.Ready)
            {
                throw new InvalidOperationException(
                    status.Failure is { } failure
                        ? $"Drive {status.Drive} failed ({failure.Kind}): {failure.Message}"
                        : $"Drive {status.Drive} is {status.State}.");
            }

            Assert.IsNull(status.Failure);
            Assert.IsNull(status.Block!.CacheSlot);
            Assert.IsFalse(Directory.Exists(ignoredCachePath));

            var query = new SearchQuery("*.log", NameMatchMode.Glob, IsDirectory: false);
            foreach (var entry in index.Enumerate(query, cancellationToken).Take(20))
            {
                retainedEntry = entry;
                var observedSize = entry.IsSizeKnown ? $"{entry.Size} bytes" : "size unknown";
                lines.Add($"{entry.Path} ({observedSize}, last write observed {entry.LastWriteTime:u})");
            }

            var exactMatches = index.Search(new SearchQuery("Notes.txt", NameMatchMode.Exact), cancellationToken);
            Assert.AreEqual(1, exactMatches.Count);
            Assert.AreEqual(11L, exactMatches[0].Size);
        }

        Assert.AreEqual(1, scanCount);
        CollectionAssert.AreEqual(
            new[]
            {
                $"{Path.Combine(_directory, "logs", "app.log")} (42 bytes, last write observed 2026-10-14 12:00:00Z)",
                $"{Path.Combine(_directory, "logs", "pending.log")} (size unknown, last write observed 2026-10-14 12:00:00Z)"
            },
            lines);
        Assert.IsTrue(retainedEntry.IsDisposed);
        Assert.ThrowsException<ObjectDisposedException>(() => _ = retainedEntry.Size);
    }

    [TestMethod]
    [SupportedOSPlatform("windows")]
    public async Task LocalOneShotWalkthrough_FailedAndCancelledOperations_SurfaceExpectedOutcomes()
    {
        IndexedDrive.Dependencies.VolumeSerialReader = _ => 0x0BADF00Du;
        var drive = IndexedDrive.FromWindowsVolume('C') with { RootDirectory = _directory };

        var failingOptions = new FileIndexOptions
        {
            Drives = [drive],
            CacheMode = CacheMode.Disabled,
            MftSource = SyntheticIndexSource.Create(
                new SyntheticMftProducer(_ => throw new InvalidOperationException("Access to the volume was denied.")))
        };

        await using (var failedIndex = await FileIndex.OpenAsync(failingOptions, TestContext.CancellationTokenSource.Token))
        {
            var status = failedIndex.Drives.Single();
            Assert.AreEqual(DriveState.Failed, status.State);
            Assert.AreEqual(DriveFailureKind.ProducerFailed, status.Failure?.Kind);

            var thrown = Assert.ThrowsException<InvalidOperationException>(() =>
            {
                if (status.State != DriveState.Ready)
                {
                    throw new InvalidOperationException(
                        status.Failure is { } failure
                            ? $"Drive {status.Drive} failed ({failure.Kind}): {failure.Message}"
                            : $"Drive {status.Drive} is {status.State}.");
                }
            });
            Assert.AreEqual("Drive C failed (ProducerFailed): Access to the volume was denied.", thrown.Message);
        }

        using var cancelledSource = new CancellationTokenSource();
        await cancelledSource.CancelAsync();
        await Assert.ThrowsExceptionAsync<OperationCanceledException>(
            () => FileIndex.OpenAsync(failingOptions, cancelledSource.Token));
    }
}
