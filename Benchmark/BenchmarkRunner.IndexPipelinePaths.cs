using System.Globalization;
using MFTLib.Index;

namespace Benchmark;

public partial class BenchmarkRunner
{
    void RunPipelinePaths(string directory, uint rows, int iterations)
    {
        using var block = BlockFile.Create(new BlockFileCreateOptions
        {
            Path = Path.Combine(directory, "paths.mlix"),
            Root = new VolumeKey('T'),
            VolumeSerial = 0,
            ProducerKind = ProducerKind.Enumeration,
            SlotCapacity = rows,
            NameEntryCapacity = rows,
            NamePoolCapacity = checked(rows * 32),
            DeltaCapacity = BlockLayout.ComputeDeltaCapacity(rows),
            DeleteOnClose = true
        });
        var writer = new BlockWriter(block);
        for (uint row = 0; row < rows; row++)
        {
            var slot = row % 128;
            var first = row - slot;
            var parent = row == 0 ? 0 : slot is >= 1 and <= 12 ? row - 1 : first + Math.Min(row - first, 6 + row % 7);
            // Each group has a twelve-directory spine; leaves are six to twelve levels down.
            if (slot <= 1)
            {
                parent = 0;
            }
            var name = string.Create(CultureInfo.InvariantCulture,
                $"{(row % 3 == 0 ? "資料" : "node")}-{row:D8}");
            var flags = RowFlags.InUse | (slot <= 12 ? RowFlags.Directory : 0);
            if (!writer.TryWriteRow(row, name, new RowColumns(parent, flags, 0, 1, 0, 0)))
            {
                throw new InvalidOperationException("Synthetic path input did not fit its block.");
            }
        }
        writer.Complete(DateTime.UnixEpoch, null);
        var root = new RootBlock(new VolumeKey('T'), block, rootDirectoryPath: Path.GetPathRoot(directory));
        var samples = new double[iterations];
        var allocations = new long[iterations];
        long checksum = 0;
        for (var iteration = -1; iteration < iterations; iteration++)
        {
            var allocated = GC.GetAllocatedBytesForCurrentThread();
            var started = _timeProvider.GetTimestamp();
            checksum = ReadPipelinePaths(root);
            RecordPipelineSample(samples, iteration, started);
            if (iteration >= 0)
            {
                allocations[iteration] = GC.GetAllocatedBytesForCurrentThread() - allocated;
            }
        }
        _output.WriteLine("--- rows-paths ---");
        _output.WriteLine("  Separate nested tree, directory depths 6-12, mixed ASCII/UTF-16; one warmup then repeated full row passes.");
        _output.WriteLine(string.Create(CultureInfo.InvariantCulture,
            $"  Median: {Median(samples, static (left, right) => (left + right) / 2):F3} ms"));
        _output.WriteLine($"Path allocated bytes: {Median(allocations, static (left, right) => (left + right) / 2)}; Path Checksum: {checksum}");
    }

    static long ReadPipelinePaths(RootBlock root)
    {
        using var borrow = new SnapshotBorrow(root);
        long checksum = 0;
        for (uint row = 0; row < root.Block.Header.RowCount; row++)
        {
            var path = FileEntry.Create(root, row).Path;
            foreach (var character in path)
            {
                checksum = unchecked(checksum * 31 + character);
            }
        }
        return checksum;
    }
}
