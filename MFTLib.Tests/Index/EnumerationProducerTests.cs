using MFTLib.Index;
using MFTLib.Tests.TestSupport;
using MFTLibTestExtensions;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace MFTLib.Tests.Index;

// The partial fixture keeps the producer's content and concurrency contracts together with shared block ownership.
[TestClass]
[DoNotParallelize]
public sealed partial class EnumerationProducerTests : IDisposable
{
    IndexTreeFixture _fixture = null!;
    string _treeRoot = null!;
    string BlockDirectoryPath => _fixture.CacheDirectory;

    [TestInitialize]
    public void Initialize()
    {
        _fixture = new IndexTreeFixture();
        _treeRoot = _fixture.TreeRoot;
        Directory.CreateDirectory(BlockDirectoryPath);

        Directory.CreateDirectory(Path.Combine(_treeRoot, "Documents", "Projects"));
        Directory.CreateDirectory(Path.Combine(_treeRoot, "Pictures"));
        File.WriteAllText(Path.Combine(_treeRoot, "Documents", "Projects", "report.pdf"), new string('x', 100));
        File.WriteAllText(Path.Combine(_treeRoot, "Pictures", "holiday.jpg"), new string('y', 50));
    }

    public void Dispose()
    {
        _fixture.Dispose();
    }

    int _blockId;

    (BlockFile Block, BlockWriter Writer) CreateBlock(uint slotCapacity = 4096, uint namePoolCapacity = 1048576)
    {
        var block = BlockFile.Create(new BlockFileCreateOptions
        {
            Path = Path.Combine(BlockDirectoryPath, $"T-{Interlocked.Increment(ref _blockId):D8}.mlix"),
            VolumeSerial = 1,
            ProducerKind = ProducerKind.Enumeration,
            SlotCapacity = slotCapacity,
            NamePoolCapacity = namePoolCapacity
        });

        return (block, new BlockWriter(block));
    }

    DriveBlock[] Produce(out BlockFile block, out EnumerationResult result,
        IReadOnlyCollection<string>? retainedFileNames = null, DirectoryWalkOptions? directoryWalk = null,
        string? rootDirectory = null, int maximumWorkers = int.MaxValue)
    {
        var root = rootDirectory ?? _treeRoot;
        var created = CreateBlock();
        block = created.Block;
        var producer = new EnumerationProducer(new EnumerationProducerOptions
        {
            RootDirectory = root,
            Drive = 'T',
            RetainedFileNames = retainedFileNames,
            DirectoryWalk = DirectoryWalkOptions.Admit(directoryWalk ?? new DirectoryWalkOptions())
        });

        using var lease = EnumerationWalkLimit.EnterAsync(CancellationToken.None).AsTask().GetAwaiter().GetResult();
        result = producer.Produce(created.Writer, progress: null, lease, CancellationToken.None, maximumWorkers);
        created.Writer.Complete(new DateTime(2026, 9, 2, 0, 0, 0, DateTimeKind.Utc), null);
        return [new DriveBlock('T', 0, block, rootDirectoryPath: root)];
    }

    [TestMethod]
    public async Task Produce_WritesARootPlusEveryDirectoryAndFile()
    {
        var snapshot = Produce(out var block, out var result);
        try
        {
            // Root, Documents, Projects, Pictures, readme.md, report.pdf, holiday.jpg.
            Assert.AreEqual(7u, result.RowCount);
            Assert.AreEqual(7u, block.Header.RowCount);
            Assert.AreEqual(0, result.AccessDeniedSubtreeCount);
            Assert.IsFalse(result.CapacityExhausted);
        }
        finally
        {
            await snapshot.ReleaseNowAsync();
        }
    }

    [TestMethod]
    public async Task Produce_NullRetainedFileNames_WritesEveryFile()
    {
        var snapshot = Produce(out _, out var result, retainedFileNames: null);
        try
        {
            Assert.AreEqual(7u, result.RowCount);
            Assert.IsTrue(EnumerationLookupTestAccess.FindByPath(snapshot, Path.Combine(_treeRoot, "Documents", "readme.md")).HasValue);
            Assert.IsTrue(EnumerationLookupTestAccess.FindByPath(snapshot, Path.Combine(_treeRoot, "Pictures", "holiday.jpg")).HasValue);
        }
        finally
        {
            await snapshot.ReleaseNowAsync();
        }
    }

    [TestMethod]
    public async Task Produce_EmptyRetainedFileNames_WritesDirectoriesOnly()
    {
        var snapshot = Produce(out _, out var result, retainedFileNames: []);
        try
        {
            // Root, Documents, Projects, Pictures.
            Assert.AreEqual(4u, result.RowCount);
            Assert.IsTrue(EnumerationLookupTestAccess.FindByPath(snapshot, Path.Combine(_treeRoot, "Documents", "Projects")).HasValue);
            Assert.IsTrue(EnumerationLookupTestAccess.FindByPath(snapshot, Path.Combine(_treeRoot, "Pictures")).HasValue);
            Assert.IsFalse(EnumerationLookupTestAccess.FindByPath(snapshot, Path.Combine(_treeRoot, "Documents", "readme.md")).HasValue);
            Assert.IsFalse(EnumerationLookupTestAccess.FindByPath(snapshot, Path.Combine(_treeRoot, "Documents", "Projects", "report.pdf")).HasValue);
        }
        finally
        {
            await snapshot.ReleaseNowAsync();
        }
    }

    [TestMethod]
    public async Task Produce_NonemptyRetainedFileNames_WritesDirectoriesAndMatchingFilesIgnoringCase()
    {
        var snapshot = Produce(out _, out var result, retainedFileNames: ["REPORT.PDF", "no-such-file.txt"]);
        try
        {
            // Root, Documents, Projects, Pictures, report.pdf.
            Assert.AreEqual(5u, result.RowCount);
            Assert.IsTrue(EnumerationLookupTestAccess.FindByPath(snapshot, Path.Combine(_treeRoot, "Documents", "Projects", "report.pdf")).HasValue);
            Assert.IsFalse(EnumerationLookupTestAccess.FindByPath(snapshot, Path.Combine(_treeRoot, "Pictures", "holiday.jpg")).HasValue);
            Assert.IsFalse(EnumerationLookupTestAccess.FindByPath(snapshot, Path.Combine(_treeRoot, "Documents", "readme.md")).HasValue);
        }
        finally
        {
            await snapshot.ReleaseNowAsync();
        }
    }

    [TestMethod]
    public async Task Produce_RetainedFileNamesNeverMatchADirectory_AndTheRootRowIsAlwaysKept()
    {
        // A directory is kept whether or not its name is in the set, so the set naming one changes nothing.
        var snapshot = Produce(out var block, out var result, retainedFileNames: ["Pictures"]);
        try
        {
            Assert.AreEqual(4u, result.RowCount);
            Assert.AreEqual(_treeRoot, new string(NamePool.ReadRowName(block, 0)));
            Assert.IsTrue(block.Rows[0].IsDirectory);
            Assert.IsTrue(EnumerationLookupTestAccess.FindByPath(snapshot, Path.Combine(_treeRoot, "Documents")).HasValue);
        }
        finally
        {
            await snapshot.ReleaseNowAsync();
        }
    }

    [TestMethod]
    public void CreateDirectoryEnumerationOptions_CarriesTheMeasuredFetchBufferSize()
    {
        var options = EnumerationProducer.CreateDirectoryEnumerationOptions();

        Assert.AreEqual(0, options.BufferSize);
    }

    [TestMethod]
    public async Task Produce_BuildsPathsThatMatchTheRealTree()
    {
        var snapshot = Produce(out _, out _);
        try
        {
            var entry = EnumerationLookupTestAccess.FindByPath(snapshot, Path.Combine(_treeRoot, "Documents", "Projects", "report.pdf"));
            Assert.IsTrue(entry.HasValue);
            var entryVal = entry.GetValueOrDefault();
            Assert.AreEqual(100L, entryVal.Size);
            Assert.IsFalse(entryVal.IsDirectory);
        }
        finally
        {
            await snapshot.ReleaseNowAsync();
        }
    }

    [TestMethod]
    public async Task Produce_MarksDirectoriesAsDirectoriesWithZeroSize()
    {
        var snapshot = Produce(out _, out _);
        try
        {
            var documents = EnumerationLookupTestAccess.FindByPath(snapshot, Path.Combine(_treeRoot, "Documents"));
            Assert.IsTrue(documents.HasValue);
            var docVal = documents.GetValueOrDefault();
            Assert.IsTrue(docVal.IsDirectory);
            Assert.AreEqual(0L, docVal.Size);
        }
        finally
        {
            await snapshot.ReleaseNowAsync();
        }
    }

    [TestMethod]
    public async Task Produce_RootRowIsRowZeroAndParentsItself()
    {
        var snapshot = Produce(out var block, out _);
        try
        {
            Assert.AreEqual(0u, block.Header.RootRow);
            Assert.AreEqual(0u, block.Rows[0].ParentRow);
            Assert.IsTrue(block.Rows[0].IsDirectory);
            Assert.AreEqual(_treeRoot.Length, block.Rows[0].NameLengthUnits);
            Assert.AreEqual(_treeRoot, new string(NamePool.ReadRowName(block, 0)));

            for (uint row = 1; row < block.Header.RowCount; row++)
            {
                Assert.IsLessThan(row, block.Rows[(int)row].ParentRow);
                Assert.IsTrue(block.Rows[(int)block.Rows[(int)row].ParentRow].IsDirectory);
                Assert.AreEqual((ushort)0, block.SequenceNumbers[(int)row]);
            }
        }
        finally
        {
            await snapshot.ReleaseNowAsync();
        }
    }

    [TestMethod]
    public async Task Produce_RootRowCarriesTheRootDirectoriesRealLastWriteTimeAndAttributes()
    {
        var rootMoment = new DateTime(2021, 3, 4, 5, 6, 7, DateTimeKind.Utc);
        Directory.SetLastWriteTimeUtc(_treeRoot, rootMoment);
        var snapshot = Produce(out var block, out _);
        try
        {
            ref readonly var root = ref block.Rows[0];
            Assert.IsTrue(root.LastWriteTimeKnown);
            Assert.AreEqual(rootMoment, root.ModifiedUtc, "The root row must not be stamped with the scan clock.");
            Assert.AreEqual((uint)(new DirectoryInfo(_treeRoot).Attributes | FileAttributes.Directory), root.Attributes);
        }
        finally
        {
            await snapshot.ReleaseNowAsync();
        }
    }

    [TestMethod]
    public async Task Produce_UnreadableRoot_MarksTheRootLastWriteTimeUnknownInsteadOfStampingTheClock()
    {
        var existingRoot = _treeRoot;
        _treeRoot = Path.Combine(existingRoot, "missing");
        var snapshot = Produce(out var block, out _);
        try
        {
            ref readonly var root = ref block.Rows[0];
            Assert.IsFalse(root.LastWriteTimeKnown);
            Assert.AreEqual(0L, root.ModifiedTicks);
            Assert.IsTrue(root.IsDirectory);
        }
        finally
        {
            _treeRoot = existingRoot;
            await snapshot.ReleaseNowAsync();
        }
    }

    [TestMethod]
    public async Task Produce_ProducerKindOnTheBlockIsEnumerationSoIdsCarryThatKind()
    {
        var snapshot = Produce(out _, out _);
        try
        {
            var entry = EnumerationLookupTestAccess.FindByPath(snapshot, Path.Combine(_treeRoot, "Pictures", "holiday.jpg"));
            Assert.AreEqual(ProducerKind.Enumeration, entry!.Value.RecordKey.ProducerKind);
        }
        finally
        {
            await snapshot.ReleaseNowAsync();
        }
    }

    [TestMethod]
    public async Task Produce_ReportsProgress()
    {
        var created = CreateBlock();
        using var block = created.Block;

        var observed = new List<IndexScanProgress>();
        var reportingThreadIds = new HashSet<int>();
        var callingThreadId = Environment.CurrentManagedThreadId;
        var producer = new EnumerationProducer(new EnumerationProducerOptions
        {
            RootDirectory = _treeRoot,
            Drive = 'T'
        });

        using var lease = await EnumerationWalkLimit.EnterAsync(CancellationToken.None);
        producer.Produce(created.Writer, new SynchronousProgress<IndexScanProgress>(report =>
        {
            reportingThreadIds.Add(Environment.CurrentManagedThreadId);
            observed.Add(report);
        }), lease, CancellationToken.None);

        Assert.AreEqual(7u, created.Writer.RowCount);
        Assert.IsNotEmpty(observed, "the walk visited at least one directory");
        Assert.HasCount(1, reportingThreadIds, "all progress reports came from one thread");
        Assert.AreEqual(callingThreadId, reportingThreadIds.Single(), "all progress reports came from the calling thread");
        Assert.AreEqual(IndexScanPhase.Enumerating, observed[0].Phase);
        Assert.AreEqual('T', observed[0].Drive);
        Assert.AreAllDistinct(observed.Select(report => report.CurrentDirectory!).ToArray());
        for (var i = 1; i < observed.Count; i++)
        {
            Assert.IsGreaterThanOrEqualTo(observed[i - 1].WrittenRowCount, observed[i].WrittenRowCount, "counts are nondecreasing");
        }
        Assert.AreEqual(7u, observed[^1].WrittenRowCount);
    }

    [TestMethod]
    public async Task Produce_ExhaustedSlotCapacity_SetsCapacityExhaustedAndDoesNotThrow()
    {
        var created = CreateBlock(slotCapacity: 3);
        using var block = created.Block;
        var producer = new EnumerationProducer(new EnumerationProducerOptions
        {
            RootDirectory = _treeRoot,
            Drive = 'T'
        });
        var progress = new RecordingProgress();

        using var lease = await EnumerationWalkLimit.EnterAsync(CancellationToken.None);
        var result = producer.Produce(created.Writer, progress, lease, CancellationToken.None, maximumWorkers: 1);

        Assert.IsTrue(result.CapacityExhausted);
        Assert.IsTrue(created.Writer.CapacityExhausted);

        // Root (Documents, Pictures both fit in the 3-row capacity) is always walked, then
        // exactly one of Documents or Pictures fails to write its first entry and trips
        // capacity exhausted. The walk must stop there instead of also opening the other sibling
        // directory, so at most two directories are ever visited.
        Assert.HasCount(2, progress.Directories);
    }

    [TestMethod]
    public async Task Produce_DirectorySymbolicLink_RecordsRowButDoesNotFollowIt()
    {
        var linkPath = Path.Combine(_treeRoot, "RootLink");
        Exception? linkFailure = null;
        try
        {
            Directory.CreateSymbolicLink(linkPath, _treeRoot);
        }
        catch (Exception exception)
        {
            linkFailure = exception;
        }
        if (linkFailure is not null)
        {
            Assert.Inconclusive($"Could not create a directory symbolic link: {linkFailure.Message}");
        }

        var snapshot = Produce(out var block, out var result);
        try
        {
            // Root, Documents, Projects, Pictures, readme.md, report.pdf, holiday.jpg, RootLink.
            Assert.AreEqual(8u, result.RowCount);
            Assert.AreEqual(8u, block.Header.RowCount);
            Assert.IsFalse(result.CapacityExhausted);

            var link = EnumerationLookupTestAccess.FindByPath(snapshot, Path.Combine(_treeRoot, "RootLink"));
            Assert.IsTrue(link.HasValue);
        }
        finally
        {
            await snapshot.ReleaseNowAsync();
        }
    }

    [TestMethod]
    public async Task Produce_IndexesHiddenAndSystemEntries()
    {
        // Hidden on Windows is an attribute bit; on Linux and macOS it is a leading-dot naming
        // convention. Giving the file both makes the assertion meaningful on every platform.
        const string hiddenName = ".hidden.txt";
        var hiddenPath = Path.Combine(_treeRoot, hiddenName);
        await File.WriteAllTextAsync(hiddenPath, "secret", TestContext.CancellationToken);
        if (OperatingSystem.IsWindows())
        {
            File.SetAttributes(hiddenPath, File.GetAttributes(hiddenPath) | FileAttributes.Hidden);
        }

        var snapshot = Produce(out _, out _);
        try
        {
            var entry = EnumerationLookupTestAccess.FindByPath(snapshot, Path.Combine(_treeRoot, hiddenName));
            Assert.IsTrue(entry.HasValue);
        }
        finally
        {
            await snapshot.ReleaseNowAsync();
        }
    }

    [TestMethod]
    public async Task Produce_MissingRootDirectory_CountsAnAccessDeniedSubtreeAndDoesNotThrow()
    {
        var created = CreateBlock();
        using var block = created.Block;
        var producer = new EnumerationProducer(new EnumerationProducerOptions
        {
            RootDirectory = Path.Combine(_treeRoot, "does-not-exist"),
            Drive = 'T'
        });

        using var lease = await EnumerationWalkLimit.EnterAsync(CancellationToken.None);
        var result = producer.Produce(created.Writer, progress: null, lease, CancellationToken.None);

        Assert.AreEqual(1u, result.RowCount);
        Assert.AreEqual(1, result.AccessDeniedSubtreeCount);
    }

    [TestMethod]
    public async Task Produce_HonoursCancellation()
    {
        var created = CreateBlock();
        using var block = created.Block;
        using var cancellation = new CancellationTokenSource();
        await cancellation.CancelAsync();
        var token = cancellation.Token;

        var producer = new EnumerationProducer(new EnumerationProducerOptions
        {
            RootDirectory = _treeRoot,
            Drive = 'T'
        });

        using var lease = await EnumerationWalkLimit.EnterAsync(CancellationToken.None);
        Assert.ThrowsExactly<OperationCanceledException>(
            () => producer.Produce(created.Writer, progress: null, lease, token));
    }

    [TestMethod]
    [DataRow("io")]
    [DataRow("unauthorized")]
    [DataRow("notfound")]
    public void DirectoryRead_FaultAfterOneEntryKeepsItsOwnedPrefix(string exceptionKind)
    {
        Exception CreateException() => exceptionKind switch
        {
            "io" => new IOException("scripted directory read failure"),
            "unauthorized" => new UnauthorizedAccessException("scripted directory access denial"),
            "notfound" => new DirectoryNotFoundException("scripted directory disappearance"),
            _ => throw new ArgumentOutOfRangeException(nameof(exceptionKind))
        };

        var observations = new List<int>();
        var options = new EnumerationProducerOptions { RootDirectory = _treeRoot, Drive = 'T' };
        var reader = new EnumerationDirectoryReader(options)
        {
            BeforeMoveNext = (_, observed) =>
            {
                observations.Add(observed);
                if (observed == 1)
                {
                    throw CreateException();
                }
            }
        };
        var result = reader.Read(new EnumerationDirectoryWork(_treeRoot, 0), CancellationToken.None);
        Assert.IsTrue(result.SubtreeSkipped);
        Assert.HasCount(1, result.Entries);
        Assert.IsGreaterThan(0, result.Name(result.Entries[0]).Length);
        Assert.AreEqual(0u, result.Entries[0].Columns.ParentRow);
        Assert.AreEqual((ushort)0, result.Entries[0].Columns.SequenceNumber);
        Assert.AreSequenceEqual([0, 1], observations);
    }

    [TestMethod]
    public void DirectoryRead_UnexpectedExceptionEscapesUnchanged()
    {
        var observations = new List<int>();
        var failure = new InvalidOperationException("unexpected reader fault");
        var options = new EnumerationProducerOptions { RootDirectory = _treeRoot, Drive = 'T' };
        var reader = new EnumerationDirectoryReader(options)
        {
            BeforeMoveNext = (_, observed) =>
            {
                observations.Add(observed);
                if (observed == 1)
                {
                    throw failure;
                }
            }
        };
        var thrown = Assert.ThrowsExactly<InvalidOperationException>(
            () => reader.Read(new EnumerationDirectoryWork(_treeRoot, 0), CancellationToken.None));
        Assert.AreSame(failure, thrown);
        Assert.AreSequenceEqual([0, 1], observations);
    }

    [TestMethod]
    [DataRow(0)]
    [DataRow(1)]
    [DataRow(2)]
    public void DirectoryRead_CancellationAfterOneEntryThrowsOperationCanceledException(int entryCount)
    {
        using var cancellation = new CancellationTokenSource();
        var token = cancellation.Token;
        Action cancel = cancellation.Cancel;
        var directory = Directory.CreateDirectory(Path.Combine(_treeRoot, "cancellation"));
        for (var entry = 0; entry < entryCount; entry++)
        {
            File.WriteAllText(Path.Combine(directory.FullName, $"entry-{entry}"), "content");
        }
        var options = new EnumerationProducerOptions { RootDirectory = directory.FullName, Drive = 'T' };
        var reader = new EnumerationDirectoryReader(options)
        {
            BeforeMoveNext = (_, observed) =>
            {
                if (observed == Math.Min(entryCount, 1))
                {
                    cancel();
                }
            }
        };
        var thrown = Assert.ThrowsExactly<OperationCanceledException>(
            () => reader.Read(new EnumerationDirectoryWork(directory.FullName, 0), token));
        Assert.AreEqual(token, thrown.CancellationToken);
    }

    [TestMethod]
    public async Task Produce_ParallelAndSequentialHaveEquivalentGeneratedTreeContent()
    {
        EnumerationWalkTestSupport.GenerateBranchingTree(_treeRoot, 4, 3, 3);
        const int fixedYear = 2026;
        const int fixedHour = 12;
        var fixedTime = new DateTime(fixedYear, 1, 1, fixedHour, 0, 0, DateTimeKind.Utc);
        foreach (var dir in Directory.EnumerateDirectories(_treeRoot, "*", SearchOption.AllDirectories))
        {
            Directory.SetLastWriteTimeUtc(dir, fixedTime);
        }
        foreach (var file in Directory.EnumerateFiles(_treeRoot, "*", SearchOption.AllDirectories))
        {
            File.SetLastWriteTimeUtc(file, fixedTime);
        }
        var expected = EnumerationWalkTestSupport.FileSystemInventory(_treeRoot).ToHashSet();
        var sequential = Produce(out _, out var first, maximumWorkers: 1);
        try
        {
            var parallel = Produce(out _, out var second, maximumWorkers: 2);
            try
            {
                Assert.IsFalse(first.CapacityExhausted);
                Assert.IsFalse(second.CapacityExhausted);
                Assert.AreEqual(first.AccessDeniedSubtreeCount, second.AccessDeniedSubtreeCount);
                Assert.IsTrue(EnumerationWalkTestSupport.Inventory(sequential[0])
                    .SetEquals(EnumerationWalkTestSupport.Inventory(parallel[0])));
                Assert.IsTrue(expected.SetEquals(EnumerationWalkTestSupport.Inventory(sequential[0])));
                Assert.IsTrue(expected.SetEquals(EnumerationWalkTestSupport.Inventory(parallel[0])));
            }
            finally
            {
                await parallel.ReleaseNowAsync();
            }
        }
        finally
        {
            await sequential.ReleaseNowAsync();
        }
    }

    [TestMethod]
    public async Task Produce_OverlappingDirectoriesUseTwoAvailableSlots()
    {
        if (Environment.ProcessorCount < 2)
        {
            Assert.Inconclusive("Requires at least two processors to test overlapping directories.");
        }

        var leases = new List<EnumerationWalkLimit.Lease>();
        try
        {
            for (var i = 2; i < Environment.ProcessorCount; i++)
            {
                leases.Add(await EnumerationWalkLimit.EnterAsync(CancellationToken.None));
            }

            var documentsGate = new TestGate();
            var picturesGate = new TestGate();

            EnumerationWalkLimit.Hooks._directoryEntered = (_, path, _) =>
            {
                if (path.EndsWith("Documents", StringComparison.OrdinalIgnoreCase))
                {
                    documentsGate.MarkEntered();
                    documentsGate.WaitForRelease();
                }
                else if (path.EndsWith("Pictures", StringComparison.OrdinalIgnoreCase))
                {
                    picturesGate.MarkEntered();
                    picturesGate.WaitForRelease();
                }
            };

            var created = CreateBlock();
            using var block = created.Block;
            var producer = new EnumerationProducer(new EnumerationProducerOptions
            {
                RootDirectory = _treeRoot,
                Drive = 'T'
            });

            var task = EnumerationWalkTestSupport.ProduceAsync(producer, created.Writer, null, CancellationToken.None);
            try
            {
                await Task.WhenAll(documentsGate.Entered, picturesGate.Entered).WaitAsync(HangGuard, TestContext.CancellationToken);
            }
            finally
            {
                documentsGate.Release();
                picturesGate.Release();
                await task.WaitAsync(HangGuard, TestContext.CancellationToken);
            }
        }
        finally
        {
            EnumerationWalkLimit.Hooks.ResetToDefaults();
            foreach (var lease in leases)
            {
                lease.Dispose();
            }
        }
    }

    [TestMethod]
    public async Task Produce_ParallelCompletionDoesNotWaitForAnEarlierExtraJob()
    {
        const int minimumProcessors = 2;
        if (Environment.ProcessorCount < minimumProcessors)
        {
            Assert.Inconclusive("Requires at least two processors.");
        }

        Directory.CreateDirectory(Path.Combine(_treeRoot, "Sub1"));
        Directory.CreateDirectory(Path.Combine(_treeRoot, "Sub2"));
        Directory.CreateDirectory(Path.Combine(_treeRoot, "Sub3"));
        await File.WriteAllTextAsync(Path.Combine(_treeRoot, "Sub1", "file1.txt"), "content1", TestContext.CancellationToken);
        await File.WriteAllTextAsync(Path.Combine(_treeRoot, "Sub2", "file2.txt"), "content2", TestContext.CancellationToken);
        await File.WriteAllTextAsync(Path.Combine(_treeRoot, "Sub3", "file3.txt"), "content3", TestContext.CancellationToken);

        var leases = new List<EnumerationWalkLimit.Lease>();
        try
        {
            for (var i = minimumProcessors; i < Environment.ProcessorCount; i++)
            {
                leases.Add(await EnumerationWalkLimit.EnterAsync(CancellationToken.None));
            }

            var firstExtraGate = new TestGate();
            var secondExtraGate = new TestGate();
            var extraCount = 0;
            var observedSecondWhileFirstHeld = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            string? secondExtraPath = null;
            var secondExtraFinished = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);


            EnumerationWalkLimit.Hooks._directoryEntered = (_, path, _) =>
            {
                if (string.Equals(path, _treeRoot, StringComparison.OrdinalIgnoreCase))
                {
                    return;
                }

                var count = Interlocked.Increment(ref extraCount);
                if (count == 1)
                {
                    firstExtraGate.MarkEntered();
                    firstExtraGate.WaitForRelease();
                }
                else if (count == 2)
                {
                    secondExtraPath = path;
                    secondExtraGate.MarkEntered();
                    secondExtraGate.WaitForRelease();
                }
            };

            EnumerationWalkLimit.Hooks._directoryExited = (_, path) =>
            {
                if (secondExtraPath is not null &&
                    string.Equals(path, secondExtraPath, StringComparison.OrdinalIgnoreCase))
                {
                    secondExtraFinished.TrySetResult();
                }
            };

            var created = CreateBlock();
            using var block = created.Block;
            var producer = new EnumerationProducer(new EnumerationProducerOptions
            {
                RootDirectory = _treeRoot,
                Drive = 'T'
            });

            var progress = new SynchronousProgress<IndexScanProgress>(report =>
            {
                if (secondExtraPath is not null &&
                    string.Equals(report.CurrentDirectory, secondExtraPath, StringComparison.OrdinalIgnoreCase))
                {
                    if (!firstExtraGate.IsReleased)
                    {
                        observedSecondWhileFirstHeld.TrySetResult();
                    }
                }
            });

            var task = EnumerationWalkTestSupport.ProduceAsync(producer, created.Writer, progress,
                CancellationToken.None);

            try
            {
                await firstExtraGate.Entered.WaitAsync(HangGuard, TestContext.CancellationToken);
                await secondExtraGate.Entered.WaitAsync(HangGuard, TestContext.CancellationToken);
                secondExtraGate.Release();
                await secondExtraFinished.Task.WaitAsync(HangGuard, TestContext.CancellationToken);
                await observedSecondWhileFirstHeld.Task.WaitAsync(HangGuard, TestContext.CancellationToken);
            }
            finally
            {
                firstExtraGate.Release();
                secondExtraGate.Release();
                await task.WaitAsync(HangGuard, TestContext.CancellationToken);
            }
        }
        finally
        {
            EnumerationWalkLimit.Hooks.ResetToDefaults();
            foreach (var lease in leases)
            {
                lease.Dispose();
            }
        }
    }

    [TestMethod]
    public void EstimateRowCount_IsAtLeastTheMinimumAndDoesNotThrowOnAMissingDirectory()
    {
        Assert.IsGreaterThan(0u, EnumerationProducer.EstimateRowCount(_treeRoot));
        Assert.IsGreaterThan(0u, EnumerationProducer.EstimateRowCount(Path.Combine(_treeRoot, "nope")));
    }

    [TestMethod]
    public void EstimateNamePoolBytes_ScalesWithRowCount()
    {
        Assert.IsGreaterThan(EnumerationProducer.EstimateNamePoolBytes(100), EnumerationProducer.EstimateNamePoolBytes(1000));
    }

    [TestMethod]
    public async Task Produce_HonoursCancellation_MidWalkDrainsAllWorkers()
    {
        EnumerationWalkTestSupport.GenerateBranchingTree(_treeRoot, 3, 2, 2);
        var created = CreateBlock();
        using var block = created.Block;
        using var cancellation = new CancellationTokenSource();
        var token = cancellation.Token;
        Action cancel = cancellation.Cancel;

        var producer = new EnumerationProducer(new EnumerationProducerOptions
        {
            RootDirectory = _treeRoot,
            Drive = 'T'
        });

        var observation = new EnumerationReadObservation();
        var exitGate = new TestGate();
        var childEntered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);

        EnumerationWalkLimit.Hooks._directoryEntered = (drive, path, readerToken) =>
        {
            observation.Enter(drive, path, readerToken);
            if (!string.Equals(path, _treeRoot, StringComparison.OrdinalIgnoreCase))
            {
                if (childEntered.TrySetResult())
                {
                    cancel();
                }
            }
        };

        EnumerationWalkLimit.Hooks._directoryExited = (drive, path) =>
        {
            try
            {
                if (!string.Equals(path, _treeRoot, StringComparison.OrdinalIgnoreCase))
                {
                    exitGate.MarkEntered();
                    exitGate.WaitForRelease();
                }
            }
            finally
            {
                observation.Exit(drive, path);
            }
        };

        Task? produceTask = null;
        {
            try
            {
                produceTask = EnumerationWalkTestSupport.ProduceAsync(producer, created.Writer, null, token);
                await childEntered.Task.WaitAsync(HangGuard, TestContext.CancellationToken);
                await exitGate.Entered.WaitAsync(HangGuard, TestContext.CancellationToken);
                Assert.IsFalse(produceTask.IsCompleted, "Produce must wait for workers to drain before completing");
                exitGate.Release();
                var thrown = await Assert.ThrowsExactlyAsync<OperationCanceledException>(() => produceTask);
                Assert.AreEqual(cancellation.Token, thrown.CancellationToken);
                Assert.AreEqual(0, observation.Active);
            }
            finally
            {
                exitGate.Release();
                if (produceTask is not null)
                {
                    try
                    {
                        await produceTask.WaitAsync(HangGuard, TestContext.CancellationToken);
                    }
                    catch (OperationCanceledException)
                    {
                        // Expected cancellation drain
                    }
                }
                EnumerationWalkLimit.Hooks.ResetToDefaults();
            }
        }

        using var verifyReservationCts = new CancellationTokenSource(HangGuard);
        using var verifyAll = await EnumerationWalkTestSupport.ReserveSlotsAsync(0, verifyReservationCts.Token);
    }

    [TestMethod]
    public async Task Produce_PartialReadFailure_RetainsPrefixAndSkipsDescendants()
    {
        var failingDir = Path.Combine(_treeRoot, "failing");
        var healthyDir = Path.Combine(_treeRoot, "healthy");
        var childOfFailing = Path.Combine(failingDir, "child");
        Directory.CreateDirectory(failingDir);
        Directory.CreateDirectory(healthyDir);
        Directory.CreateDirectory(childOfFailing);
        await File.WriteAllTextAsync(Path.Combine(childOfFailing, "subfile.txt"), "sub", TestContext.CancellationToken);
        await File.WriteAllTextAsync(Path.Combine(failingDir, "file1.txt"), "1", TestContext.CancellationToken);
        await File.WriteAllTextAsync(Path.Combine(healthyDir, "healthy.txt"), "h", TestContext.CancellationToken);

        var observation = new EnumerationReadObservation();
        EnumerationWalkLimit.Hooks._directoryEntered = observation.Enter;
        EnumerationWalkLimit.Hooks._directoryExited = observation.Exit;
        var failedDirectories = new List<string>();

        var producer = new EnumerationProducer(new EnumerationProducerOptions
        {
            RootDirectory = _treeRoot,
            Drive = 'T'
        })
        {
            BeforeMoveNext = (path, observed) =>
            {
                if (string.Equals(path, failingDir, StringComparison.OrdinalIgnoreCase) && observed == 2)
                {
                    failedDirectories.Add(path);
                    throw new IOException("partial read failure");
                }
            }
        };

        var created = CreateBlock();
        using var block = created.Block;
        try
        {
            using var lease = await EnumerationWalkLimit.EnterAsync(CancellationToken.None);
            var result = producer.Produce(created.Writer, progress: null, lease, CancellationToken.None, maximumWorkers: 2);

            Assert.IsFalse(result.CapacityExhausted);
            Assert.AreEqual(1, result.AccessDeniedSubtreeCount);
            Assert.AreSequenceEqual([failingDir], failedDirectories);
            Assert.IsFalse(observation.Visits.ContainsKey(childOfFailing), "Descendant of skipped directory must not be visited");
            Assert.IsTrue(observation.Visits.ContainsKey(healthyDir), "Healthy sibling directory must be visited");

            var snapshot = new DriveBlock('T', 0, block, rootDirectoryPath: _treeRoot);
            var healthyEntry = EnumerationLookupTestAccess.FindByPath([snapshot], Path.Combine(healthyDir, "healthy.txt"));
            Assert.IsNotNull(healthyEntry);
            Assert.IsNotNull(EnumerationLookupTestAccess.FindByPath([snapshot], childOfFailing));
            Assert.IsNotNull(EnumerationLookupTestAccess.FindByPath([snapshot], Path.Combine(failingDir, "file1.txt")));
        }
        finally
        {
            EnumerationWalkLimit.Hooks.ResetToDefaults();
        }
    }

    [TestMethod]
    public async Task Produce_UnexpectedReaderFault_PropagatesSentinelAndDrainsWorkers()
    {
        RequireParallelWorkers();
        CreateDrainTree(2);
        var sentinel = new InvalidOperationException("sentinel reader fault");
        using var observation = new EnumerationDrainObservation(_treeRoot);
        var producer = new EnumerationProducer(new EnumerationProducerOptions
        {
            RootDirectory = _treeRoot,
            Drive = 'T'
        })
        {
            BeforeMoveNext = observation.FailActiveRead(sentinel)
        };

        var created = CreateBlock();
        using var block = created.Block;
        Task? produceTask = null;
        {
            try
            {
                produceTask = EnumerationDrainObservation.Start(producer, created.Writer, null);
                await observation.Stopped.WaitAsync(HangGuard, TestContext.CancellationToken);
                Assert.IsFalse(produceTask.IsCompleted, "Caller cannot finish before sibling cleanup");
                observation.WorkerExit.Release();
                var thrown = await Assert.ThrowsExactlyAsync<InvalidOperationException>(() => produceTask.WaitAsync(HangGuard, TestContext.CancellationToken));
                Assert.AreSame(sentinel, thrown);
                Assert.Contains(observation.FaultedPath, observation.Visited);
                Assert.AreEqual(0, observation.Active);
            }
            finally
            {
                observation.WorkerExit.Release();
                if (produceTask is not null)
                {
                    try
                    {
                        await produceTask.WaitAsync(HangGuard, TestContext.CancellationToken);
                    }
                    catch (InvalidOperationException)
                    {
                        // Expected sentinel fault propagation
                    }
                }
            }
        }

        using var verifyReservationCts = new CancellationTokenSource(HangGuard);
        using var verifyAll = await EnumerationWalkTestSupport.ReserveSlotsAsync(0, verifyReservationCts.Token);
    }

    [TestMethod]
    [DataRow(true)]
    [DataRow(false)]
    public async Task Produce_WriterAndProgressFaults_PropagateAndDrain(bool failInWriter)
    {
        RequireParallelWorkers();
        CreateDrainTree(2);
        var sentinel = new IOException("sentinel writer or progress fault");
        using var observation = new EnumerationDrainObservation(_treeRoot);
        var failedDirectories = new List<string>();
        var progress = new SynchronousProgress<IndexScanProgress>(report =>
        {
            if (!failInWriter && report.CurrentDirectory != _treeRoot)
            {
                failedDirectories.Add(report.CurrentDirectory!);
                throw sentinel;
            }
        });

        var producer = new EnumerationProducer(new EnumerationProducerOptions
        {
            RootDirectory = _treeRoot,
            Drive = 'T'
        });

        var created = CreateBlock();
        using var block = created.Block;
        if (failInWriter)
        {
            created.Writer.Hooks._rowCaptured = () =>
            {
                if (created.Writer.RowCount >= 3)
                {
                    throw sentinel;
                }
            };
        }

        Task? produceTask = null;
        {
            try
            {
                produceTask = EnumerationDrainObservation.Start(producer, created.Writer, progress);
                await observation.Stopped.WaitAsync(HangGuard, TestContext.CancellationToken);
                Assert.IsFalse(produceTask.IsCompleted, "Caller cannot finish before sibling cleanup");
                observation.WorkerExit.Release();
                var thrown = await Assert.ThrowsExactlyAsync<IOException>(() => produceTask.WaitAsync(HangGuard, TestContext.CancellationToken));
                Assert.AreSame(sentinel, thrown);
                Assert.HasCount(failInWriter ? 0 : 1, failedDirectories);
                Assert.AreEqual(0, observation.Active);
                Assert.AreEqual(0u, created.Block.Header.AccessDeniedSubtreeCount);
            }
            finally
            {
                observation.WorkerExit.Release();
                if (produceTask is not null)
                {
                    try
                    {
                        await produceTask.WaitAsync(HangGuard, TestContext.CancellationToken);
                    }
                    catch (IOException)
                    {
                        // Expected sentinel fault propagation
                    }
                }
            }
        }

        using var verifyReservationCts = new CancellationTokenSource(HangGuard);
        using var verifyAll = await EnumerationWalkTestSupport.ReserveSlotsAsync(0, verifyReservationCts.Token);
    }

    [TestMethod]
    [DataRow(true)]
    [DataRow(false)]
    public async Task Produce_CapacityExhaustion_DrainsWorkersAndStopsDescent(bool testSlotCapacity)
    {
        RequireParallelWorkers();
        CreateDrainTree(2);
        foreach (var directory in Directory.GetDirectories(_treeRoot))
        {
            Directory.CreateDirectory(Path.Combine(directory, "deep"));
        }
        using var observation = new EnumerationDrainObservation(_treeRoot);
        var progress = new RecordingProgress();
        var producer = new EnumerationProducer(new EnumerationProducerOptions
        {
            RootDirectory = _treeRoot,
            Drive = 'T'
        });

        var created = testSlotCapacity
            ? CreateBlock(slotCapacity: 3, namePoolCapacity: 1048576)
            : CreateBlock(slotCapacity: 4096, namePoolCapacity: (uint)(_treeRoot.Length + (2 * "child-0".Length)) * sizeof(char));
        using var block = created.Block;
        Task<EnumerationResult>? produceTask = null;
        {
            try
            {
                produceTask = EnumerationDrainObservation.Start(producer, created.Writer, progress);
                await observation.Stopped.WaitAsync(HangGuard, TestContext.CancellationToken);
                Assert.IsFalse(produceTask.IsCompleted, "Exhaustion must drain the held reader");
                observation.WorkerExit.Release();
                var result = await produceTask.WaitAsync(HangGuard, TestContext.CancellationToken);
                Assert.IsTrue(result.CapacityExhausted);
                Assert.IsTrue(created.Writer.CapacityExhausted);
                Assert.AreEqual(0, result.AccessDeniedSubtreeCount);
                Assert.AreEqual(0, observation.Active);
                Assert.AreEqual(3u, result.RowCount);
                Assert.HasCount(2, progress.Directories);
                Assert.DoesNotContain(path => Path.GetFileName(path) == "deep", observation.Visited,
                    "Descendants of unwritten rows must never be scheduled");
            }
            finally
            {
                observation.WorkerExit.Release();
                if (produceTask is not null)
                {
                    try
                    {
                        await produceTask.WaitAsync(HangGuard, TestContext.CancellationToken);
                    }
                    catch (OperationCanceledException)
                    {
                        // Expected in failure cleanup
                    }
                }
            }
        }

        using var verifyReservationCts = new CancellationTokenSource(HangGuard);
        using var verifyAll = await EnumerationWalkTestSupport.ReserveSlotsAsync(0, verifyReservationCts.Token);
    }

    void CreateDrainTree(int children)
    {
        _treeRoot = Path.Combine(_treeRoot, "drain");
        for (var child = 0; child < children; child++)
        {
            var directory = Directory.CreateDirectory(Path.Combine(_treeRoot, $"child-{child}"));
            File.WriteAllText(Path.Combine(directory.FullName, "file.txt"), "content");
        }
    }

    static void RequireParallelWorkers()
    {
        if (Environment.ProcessorCount < 2)
        {
            Assert.Inconclusive("Requires at least two processors to hold a reader during writer teardown.");
        }
    }

    public TestContext TestContext { get; set; } = null!;
}

/// <summary>
///     A synchronous <see cref="IProgress{T}" /> that records every report immediately, unlike
///     <see cref="Progress{T}" />, which posts through a synchronization context and so cannot
///     be asserted on deterministically right after the call that triggered it.
/// </summary>
sealed class RecordingProgress : IProgress<IndexScanProgress>
{
    public List<string?> Directories { get; } = [];

    public void Report(IndexScanProgress value)
    {
        Directories.Add(value.CurrentDirectory);
    }
}

/// <summary>
///     Resolves an entry by full path without touching the lookup engine, which another task in
///     this same wave creates. Path building comes from an earlier wave, so this test file
///     depends only on committed work.
/// </summary>
static class EnumerationLookupTestAccess
{
    public static FileEntry? FindByPath(DriveBlock[] snapshot, string fullPath)
    {
        var block = snapshot[0].Block;
        for (var rowIndex = 0u; rowIndex < block.Header.RowCount; rowIndex++)
        {
            var entry = FileEntry.Create(snapshot[0], rowIndex);
            if (string.Equals(entry.Path, fullPath, StringComparison.OrdinalIgnoreCase))
            {
                return entry;
            }
        }

        return null;
    }
}
