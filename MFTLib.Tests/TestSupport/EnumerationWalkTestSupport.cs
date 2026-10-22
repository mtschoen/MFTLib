using System.Collections.Concurrent;
using MFTLib.Index;
using MFTLibTestExtensions;

namespace MFTLib.Tests.TestSupport;

static class EnumerationWalkTestSupport
{
    internal static Task<EnumerationResult> ProduceAsync(EnumerationProducer producer, BlockWriter writer,
        IProgress<IndexScanProgress>? progress, CancellationToken cancellationToken,
        int maximumWorkers = int.MaxValue) => Task.Run(async () =>
    {
        using var lease = await EnumerationWalkLimit.EnterAsync(cancellationToken);
        return producer.Produce(writer, progress, lease, cancellationToken, maximumWorkers);
    }, cancellationToken);

    internal static void GenerateBranchingTree(string root, int branches, int depth, int filesPerDirectory)
    {
        const int fixedYear = 2026;
        const int fixedHour = 12;
        var fixedTime = new DateTime(fixedYear, 1, 1, fixedHour, 0, 0, DateTimeKind.Utc);

        void Populate(string currentPath, int currentDepth)
        {
            for (var fileIndex = 0; fileIndex < filesPerDirectory; fileIndex++)
            {
                var filePath = Path.Combine(currentPath, $"file-{fileIndex:D3}.txt");
                File.WriteAllBytes(filePath, new byte[fileIndex + 1]);
                File.SetLastWriteTimeUtc(filePath, fixedTime);
            }

            if (currentDepth >= depth)
            {
                Directory.SetLastWriteTimeUtc(currentPath, fixedTime);
                return;
            }

            for (var branchIndex = 0; branchIndex < branches; branchIndex++)
            {
                var dirPath = Path.Combine(currentPath, $"directory-{branchIndex:D3}");
                Directory.CreateDirectory(dirPath);
                Populate(dirPath, currentDepth + 1);
            }

            Directory.SetLastWriteTimeUtc(currentPath, fixedTime);
        }

        Directory.CreateDirectory(root);
        Populate(root, 1);
        Directory.SetLastWriteTimeUtc(root, fixedTime);
    }

    internal static HashSet<(string Path, string ParentPath, string Name, uint Attributes, long Size, long ModifiedTicks, RowFlags Flags, ushort SequenceNumber)>
        Inventory(DriveBlock drive)
    {
        var result = new HashSet<(string Path, string ParentPath, string Name, uint Attributes, long Size, long ModifiedTicks, RowFlags Flags, ushort SequenceNumber)>();
        var rowCount = drive.Block.Header.RowCount;
        var rootPath = FileEntry.Create(drive, 0).Path;

        for (uint row = 0; row < rowCount; row++)
        {
            var entry = FileEntry.Create(drive, row);
            var path = entry.Path;
            var fileRow = drive.Block.Rows[(int)row];
            var parentPath = row == 0 ? rootPath : FileEntry.Create(drive, fileRow.ParentRow).Path;
            var name = new string(NamePool.ReadRowName(drive.Block, row));
            var attributes = fileRow.Attributes;
            var size = fileRow.Size;
            var modifiedTicks = fileRow.ModifiedTicks;
            var flags = fileRow.Flags;
            var sequenceNumber = drive.Block.SequenceNumbers[(int)row];

            result.Add((path, parentPath, name, attributes, size, modifiedTicks, flags, sequenceNumber));
        }

        return result;
    }

    internal static IEnumerable<(string Path, string ParentPath, string Name, uint Attributes, long Size, long ModifiedTicks, RowFlags Flags, ushort SequenceNumber)>
        FileSystemInventory(string root)
    {
        var rootInformation = new DirectoryInfo(root);
        yield return (root, root, root, (uint)rootInformation.Attributes, 0,
            rootInformation.LastWriteTimeUtc.Ticks, RowFlags.InUse | RowFlags.Directory, 0);
        foreach (var entry in rootInformation.EnumerateFileSystemInfos("*", SearchOption.AllDirectories))
        {
            var directory = (entry.Attributes & FileAttributes.Directory) != 0;
            yield return (entry.FullName, Path.GetDirectoryName(entry.FullName)!, entry.Name,
                (uint)entry.Attributes, directory ? 0 : ((FileInfo)entry).Length,
                entry.LastWriteTimeUtc.Ticks, RowFlags.InUse | (directory ? RowFlags.Directory : RowFlags.None), 0);
        }
    }

    internal static async Task<IDisposable> ReserveSlotsAsync(int available, CancellationToken token)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(available, 0);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(available, Environment.ProcessorCount);

        var needed = Environment.ProcessorCount - available;
        var leases = new List<EnumerationWalkLimit.Lease>(needed);
        try
        {
            for (var i = 0; i < needed; i++)
            {
                leases.Add(await EnumerationWalkLimit.EnterAsync(token));
            }

            return new Reservation(leases);
        }
        catch
        {
            foreach (var lease in leases)
            {
                lease.Dispose();
            }

            throw;
        }
    }

    sealed class Reservation(List<EnumerationWalkLimit.Lease> leases) : IDisposable
    {
        int _disposed;

        public void Dispose()
        {
            if (Interlocked.Exchange(ref _disposed, 1) == 0)
            {
                foreach (var lease in leases)
                {
                    lease.Dispose();
                }
            }
        }
    }
}

sealed class EnumerationReadObservation
{
    int _active;
    int _peak;
    readonly ConcurrentDictionary<string, int> _visits = new(StringComparer.OrdinalIgnoreCase);
    readonly ConcurrentDictionary<string, TestGate> _gates = new(StringComparer.OrdinalIgnoreCase);

    public int Active => Volatile.Read(ref _active);
    public int Peak => Volatile.Read(ref _peak);
    public ConcurrentDictionary<string, int> Visits => _visits;

    public TestGate GateFor(string path) => _gates.GetOrAdd(path, _ => new TestGate());

    public void ReleaseAll()
    {
        foreach (var gate in _gates.Values)
        {
            gate.Release();
        }
    }

    public void Enter(DriveKey drive, string path, CancellationToken token)
    {
        var active = Interlocked.Increment(ref _active);
        while (true)
        {
            var currentPeak = Volatile.Read(ref _peak);
            if (active <= currentPeak || Interlocked.CompareExchange(ref _peak, active, currentPeak) == currentPeak)
            {
                break;
            }
        }

        _visits.AddOrUpdate(path, 1, (_, count) => count + 1);

        if (_gates.TryGetValue(path, out var gate))
        {
            gate.MarkEntered();
            gate.WaitForRelease();
        }
    }

    public void Exit(DriveKey drive, string path)
    {
        Interlocked.Decrement(ref _active);
    }
}
