using System.Globalization;
using MFTLib.Index;

namespace Benchmark;

public partial class BenchmarkRunner
{
    async Task<int> RunIndexPipelineAsync(string[] arguments)
    {
        if (!TryParsePipelineArguments(arguments, out var rows, out var distinctNames, out var iterations))
        {
            _output.WriteLine("Usage: Benchmark.exe index-pipeline --synthetic N [--distinct-names N] [--iterations K]");
            return 1;
        }

        var directory = Directory.CreateTempSubdirectory("mftlib-index-pipeline-");
        try
        {
            LogSystemInfo(_output.WriteLine);
            _output.WriteLine($"Rows: {rows}; Distinct names: {distinctNames}; Iterations: {iterations}");
            _output.WriteLine("ASCII names, sequential slots; after the vocabulary is written, names use a multiplicative permutation.");
            _output.WriteLine("One fresh-block warmup, then fresh blocks; completion includes posting construction and flush.");
            _output.WriteLine("Row passes warm their pages once; name pass consumes every character. Allocation and disposal are not timed.");
            double[][] samples = [new double[iterations], new double[iterations], new double[iterations], new double[iterations]];

            long metadataChecksum = 0;
            long nameChecksum = 0;
            await using var capture = new ScenarioMemoryCapture();
            for (var iteration = -1; iteration < iterations; iteration++)
            {
                using var block = CreatePipelineBlock(directory.FullName, rows, distinctNames);
                var writer = new BlockWriter(block);
                for (uint row = 0; row < rows; row++)
                {
                    var identifier = row < distinctNames ? row : (uint)((ulong)row * 2654435761 % distinctNames);
                    var name = string.Create(CultureInfo.InvariantCulture, $"file-{identifier:D8}");
                    if (!writer.TryWriteRow(row, name, new RowColumns(0, RowFlags.InUse, 0, 1, 0, 0)))
                    {
                        throw new InvalidOperationException("Synthetic pipeline input did not fit its block.");
                    }
                }

                var started = _timeProvider.GetTimestamp();
                writer.Complete(DateTime.UnixEpoch, null);
                RecordPipelineSample(samples[0], iteration, started);
                started = _timeProvider.GetTimestamp();
                using (block.TakeAccess())
                {
                    PostingBuilder.Build(block, null, CancellationToken.None);
                }
                RecordPipelineSample(samples[1], iteration, started);

                var root = new RootBlock(new VolumeKey('T'), block);
                ReadPipelineRows(root, readNames: false);
                ReadPipelineRows(root, readNames: true);
                started = _timeProvider.GetTimestamp();
                metadataChecksum = ReadPipelineRows(root, readNames: false);
                RecordPipelineSample(samples[2], iteration, started);
                started = _timeProvider.GetTimestamp();
                nameChecksum = ReadPipelineRows(root, readNames: true);
                RecordPipelineSample(samples[3], iteration, started);
                capture.Sample();
            }

            string[] labels = ["block-completion", "posting-rebuild", "rows-metadata", "rows-names"];
            foreach (var (label, timings) in labels.Zip(samples))
            {
                _output.WriteLine($"--- {label} ---");
                var median = Median(timings, static (left, right) => (left + right) / 2);
                _output.WriteLine(string.Create(CultureInfo.InvariantCulture, $"  Median: {median:F3} ms"));
            }
            _output.WriteLine($"Metadata Checksum: {metadataChecksum}; Name Checksum: {nameChecksum}");
            RunPipelinePaths(directory.FullName, rows, iterations);
            _output.WriteLine("Memory scope: whole pipeline, including synthetic input generation, warmup and all iterations; not per-scenario.");
            PrintMemory(await capture.FinishAsync().ConfigureAwait(false), _output.WriteLine);
            return 0;
        }
        finally
        {
            directory.Delete(recursive: true);
        }
    }

    static BlockFile CreatePipelineBlock(string directory, uint rows, uint distinctNames) => BlockFile.Create(new BlockFileCreateOptions
    {
        Path = Path.Combine(directory, "T-00000000.mlix"),
        Root = new VolumeKey('T'),
        VolumeSerial = 0,
        ProducerKind = ProducerKind.Enumeration,
        SlotCapacity = rows,
        NameEntryCapacity = distinctNames,
        NamePoolCapacity = checked(distinctNames * 13),
        DeltaCapacity = BlockLayout.ComputeDeltaCapacity(rows),
        DeleteOnClose = true
    });

    void RecordPipelineSample(double[] samples, int iteration, long started)
    {
        var elapsed = _timeProvider.GetElapsedTime(started).TotalMilliseconds;
        if (iteration >= 0)
        {
            samples[iteration] = elapsed;
        }
    }

    static long ReadPipelineRows(RootBlock root, bool readNames)
    {
        using var borrow = new SnapshotBorrow(root);
        using var enumerator = new IndexRowEnumerator(new FileIndex.QueryScope(borrow, CancellationToken.None), new SearchQuery(null));
        long checksum = 0;
        while (enumerator.MoveNext())
        {
            var row = enumerator.Current;
            if (readNames)
            {
                var name = row.Name;
                // Consume every character, as the duplicate-name hash passes do, without allocating strings.
                foreach (var character in name)
                {
                    checksum = unchecked(checksum * 31 + character);
                }
            }
            else
            {
                checksum += row.IsDirectory ? 0 : row.Size;
            }
        }
        return checksum;
    }

    static bool TryParsePipelineArguments(string[] arguments, out uint rows, out uint distinctNames, out int iterations)
    {
        rows = 0;
        distinctNames = 0;
        iterations = 5;
        if (arguments.Length < 2 || arguments.Length % 2 != 0 || arguments[0] != "--synthetic" ||
            !uint.TryParse(arguments[1], CultureInfo.InvariantCulture, out rows) || rows is 0 or > 100_000_000)
        {
            return false;
        }
        distinctNames = rows;
        var seen = new HashSet<string>(StringComparer.Ordinal);
        for (var argument = 2; argument < arguments.Length; argument += 2)
        {
            if (!seen.Add(arguments[argument]))
            {
                return false;
            }
            switch (arguments[argument])
            {
                case "--distinct-names" when uint.TryParse(arguments[argument + 1], CultureInfo.InvariantCulture, out distinctNames):
                    break;
                case "--iterations" when int.TryParse(arguments[argument + 1], CultureInfo.InvariantCulture, out iterations):
                    break;
                default:
                    return false;
            }
        }
        return distinctNames > 0 && distinctNames <= rows && iterations > 0;
    }
}
