using System.Diagnostics;
using System.Runtime.InteropServices;
using MFTLib.Index;
using MFTLib.Tests.TestSupport;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace MFTLib.Tests.Index;

[TestClass]
public sealed partial class FileIndexReleaseResidentPagesTests : IndexTreeTestBase, IAsyncDisposable
{
    FileIndex? _index;

    public TestContext TestContext { get; set; } = null!;

    [TestInitialize]
    public async Task Initialize()
    {
        for (var directory = 0; directory < 20; directory++)
        {
            Directory.CreateDirectory(Path.Combine(TreeRoot, $"d{directory}"));
            for (var file = 0; file < 20; file++)
            {
                await File.WriteAllTextAsync(Path.Combine(TreeRoot, $"d{directory}", $"f{file}.txt"), "x",
                    TestContext.CancellationToken);
            }
        }

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

    [StructLayout(LayoutKind.Sequential)]
    struct WorkingSetExInformation
    {
        public nuint VirtualAddress;
        public nuint VirtualAttributes;
    }

    [LibraryImport("psapi.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool QueryWorkingSetEx(nint process, ref WorkingSetExInformation information, int bytes);

    static unsafe int ResidentPages(byte* start, int pageCount)
    {
        using var process = Process.GetCurrentProcess();
        var resident = 0;
        for (var page = 0; page < pageCount; page++)
        {
            var information = new WorkingSetExInformation
            {
                VirtualAddress = (nuint)(start + (long)page * Environment.SystemPageSize)
            };
            Assert.IsTrue(QueryWorkingSetEx(process.Handle, ref information, Marshal.SizeOf<WorkingSetExInformation>()));
            resident += (int)(information.VirtualAttributes & 1);
        }

        return resident;
    }

    [TestMethod]
    public void ReleaseResidentPages_TrimsTheBlockOnceAndKeepsResults()
    {
        var block = _index!.CurrentBlock!.Block;
        var ranges = new List<(long Start, long End)>();
        block.Hooks._trimViewRange = (start, end) =>
        {
            ranges.Add((start, end));
            return 0;
        };
        var query = new SearchQuery("f1*", NameMatchMode.Glob);
        var before = _index.Search(query, TestContext.CancellationToken).Select(entry => entry.Path).Order().ToArray();

        Assert.IsTrue(_index.ReleaseResidentPages());

        var pageBytes = (long)Environment.SystemPageSize;
        Assert.AreSequenceEqual([(0L, block.Length / pageBytes * pageBytes)], ranges);
        Assert.AreSequenceEqual(before,
            _index.Search(query, TestContext.CancellationToken).Select(entry => entry.Path).Order().ToArray());
    }

    [TestMethod]
    public void ReleaseResidentPages_DisposedIndex_Throws()
    {
        _index!.DisposeAsync().AsTask().GetAwaiter().GetResult();

        Assert.ThrowsExactly<ObjectDisposedException>(() => _index.ReleaseResidentPages());
    }

    [TestMethod]
    public async Task ReleaseResidentPages_ReleasedBlock_DoesNotTrim()
    {
        var rootBlock = _index!.CurrentBlock!;
        var calls = 0;
        rootBlock.Block.Hooks._trimViewRange = (_, _) => ++calls;
        await rootBlock.ReleaseNowAsync();

        Assert.IsFalse(_index.ReleaseResidentPages());
        Assert.AreEqual(0, calls);
    }

    [TestMethod]
    public void ReleaseResidentPages_ConcurrentWithReaders_ResultsStayIdentical()
    {
        var query = new SearchQuery("f1*", NameMatchMode.Glob);
        var expected = _index!.Search(query, TestContext.CancellationToken).Select(entry => entry.Path).Order().ToArray();
        var failures = new List<string>();

        Parallel.For(0, 8, worker =>
        {
            for (var iteration = 0; iteration < 25; iteration++)
            {
                if (worker % 2 == 0)
                {
                    _ = _index.ReleaseResidentPages();
                    continue;
                }

                var paths = _index.Search(query, TestContext.CancellationToken).Select(entry => entry.Path).Order().ToArray();
                if (!paths.SequenceEqual(expected))
                {
                    lock (failures)
                    {
                        failures.Add($"{paths.Length} results, expected {expected.Length}");
                    }
                }
            }
        });

        Assert.IsEmpty(failures);
    }

    [TestMethod]
    [OSCondition(OperatingSystems.Windows)]
    public unsafe void ReleaseResidentPages_RemovesTheViewFromTheWorkingSet()
    {
        var block = _index!.CurrentBlock!.Block;
        var pageCount = (int)(block.Length / Environment.SystemPageSize);
        fixed (FileRow* first = block.Rows)
        {
            var start = (byte*)first - BlockLayout.RowRegionOffset;
            long checksum = 0;
            for (var page = 0; page < pageCount; page++)
            {
                checksum += start[(long)page * Environment.SystemPageSize];
            }

            Assert.AreEqual(pageCount, ResidentPages(start, pageCount), "Reading the view must make its pages resident.");

            Assert.IsTrue(_index.ReleaseResidentPages());

            Assert.AreEqual(0, ResidentPages(start, pageCount));
            Assert.AreEqual(checksum, Enumerable.Range(0, pageCount).Sum(page => (long)start[(long)page * Environment.SystemPageSize]),
                "The mapping stays valid and refaults the same bytes.");
        }
    }
}
