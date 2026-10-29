using System.Globalization;
using MFTLib;
using MFTLib.Index;

namespace Benchmark;

public partial class BenchmarkRunner
{
    async Task<int> RunIndexStorageAsync(string[] arguments)
    {
        if (!TryParseIndexStorageArguments(arguments, out var records))
        {
            await _output.WriteLineAsync("Usage: Benchmark.exe index-storage --synthetic N").ConfigureAwait(false);
            return 1;
        }

        var directory = Directory.CreateTempSubdirectory("mftlib-index-storage-");
        try
        {
            LogSystemInfo(_output.WriteLine);
            _output.WriteLine($"Input records: {records}");
            _output.WriteLine("Names: seed 42; random 20-character ASCII; 10% directories; .git and HEAD once per 100 records; root at 5");
            _output.WriteLine("Timing: managed input generation and production row writes; excludes block allocation and completion");
            var (block, timings) = ProduceStorageBlock(records, directory.FullName);
            using (block)
            {
                if (timings.SkippedRecordCount != 0 || block.Header.IsCapacityExhausted)
                {
                    throw new InvalidOperationException("The synthetic input did not fit its planned block.");
                }

                WriteStorageReport(block, timings);
            }

            return 0;
        }
        catch (Exception exception) when (exception is IOException or ArgumentException or InvalidOperationException)
        {
            await _output.WriteLineAsync($"Error: {exception.Message}").ConfigureAwait(false);
            return 1;
        }
        finally
        {
            directory.Delete(recursive: true);
        }
    }

    (BlockFile Block, StorageTimings Timings) ProduceStorageBlock(uint records, string directoryPath)
    {
        var geometry = new NtfsVolumeInformation(records * 1024L, 1024);
        var identity = new BlockContentIdentity(default, ScanShape.ForMft(includeFreed: false));
        var location = new MftBlockCapacity.FileLocation(new VolumeKey('T'),
            Path.Combine(directoryPath, "T-00000000.mlix"), 0, DeleteOnClose: true);
        var request = new MftBlockProduceRequest
        {
            Volume = location.Volume,
            VolumeSerial = location.VolumeSerial,
            BlockPath = location.Path,
            DeleteOnClose = true,
            CacheTag = identity.CacheTag,
            ScanShape = identity.ScanShape
        };
        var elapsed = 0.0;
        var skippedRecordCount = 0;
        var completeMilliseconds = 0.0;
        // The scan reruns into a larger block when the planned name entries run out, as every producer does.
        var block = MftBlockGrowth.ProduceUntilFit(request, attempt =>
        {
            // aislop-ignore-next-line dotnet/IDISP001 -- the produce result owns the block and ProduceUntilFit returns or disposes it
            var attemptBlock = BlockFile.Create(MftBlockCapacity.CreateOptions(geometry, location, identity,
                attempt.MinimumCapacities));
            var writer = new BlockWriter(attemptBlock);
            var started = _timeProvider.GetTimestamp();
            var attemptResult = MftBlockRowWriter.WriteBatches(writer, StorageBatches(records), new MftScanOptions(),
                null, CancellationToken.None);
            elapsed = _timeProvider.GetElapsedTime(started).TotalMilliseconds;
            var completeStarted = _timeProvider.GetTimestamp();
            writer.Complete(DateTime.UtcNow, null);
            completeMilliseconds = _timeProvider.GetElapsedTime(completeStarted).TotalMilliseconds;
            skippedRecordCount = (int)attemptResult.SkippedRecordCount;
            return new MftBlockProduceResult(attemptBlock, skippedRecordCount);
        }).Block;
        var buildStarted = _timeProvider.GetTimestamp();
        PostingBuilder.Build(block, null, CancellationToken.None);
        var buildMilliseconds = _timeProvider.GetElapsedTime(buildStarted).TotalMilliseconds;
        return (block, new StorageTimings(elapsed, completeMilliseconds, buildMilliseconds, skippedRecordCount));
    }

    void WriteStorageReport(BlockFile block, StorageTimings timings)
    {
        _output.WriteLine(string.Create(CultureInfo.InvariantCulture, $"Scan write time: {timings.ScanMilliseconds:F3} ms"));
        _output.WriteLine($"Live rows: {block.Header.LiveRowCount}");
        _output.WriteLine($"Skipped records: {timings.SkippedRecordCount}");
        _output.WriteLine($"Slot capacity: {block.SlotCapacity}");
        _output.WriteLine($"Name entry capacity: {block.NameEntryCapacity}");
        _output.WriteLine($"Name entries: {block.NameEntryCount}");
        _output.WriteLine($"Name-pool used bytes: {block.NamePoolUsed}");
        _output.WriteLine($"Name-pool capacity bytes: {block.NamePoolCapacity}");
        _output.WriteLine($"Name-entry used bytes: {(long)block.NameEntryCount * NameEntry.Bytes}");
        _output.WriteLine($"Name-entry reserved bytes: {BlockLayout.NameEntryRegionBytes(block.NameEntryCapacity)}");
        _output.WriteLine($"Bucket bytes: {BlockLayout.BucketRegionBytes(block.NameEntryCapacity)}");
        _output.WriteLine(string.Create(CultureInfo.InvariantCulture,
            $"Complete time (posting build, flag and flush): {timings.CompleteMilliseconds:F3} ms"));
        _output.WriteLine(string.Create(CultureInfo.InvariantCulture,
            $"Posting build time (rebuild over the resident block): {timings.BuildMilliseconds:F3} ms"));
        _output.WriteLine($"Posting base names: {block.PostingBaseNameCount}");
        _output.WriteLine($"Posting base rows: {block.PostingBaseRowCount}");
        _output.WriteLine($"Posting offset bytes: {BlockLayout.PostingOffsetRegionBytes(block.NameEntryCapacity)}");
        _output.WriteLine($"Posting row bytes: {BlockLayout.PostingRowRegionBytes(block.SlotCapacity)}");
        _output.WriteLine($"Delta capacity: {block.DeltaCapacity}");
        _output.WriteLine($"Delta head bytes: {BlockLayout.DeltaHeadRegionBytes(block.NameEntryCapacity)}");
        _output.WriteLine($"Delta entry bytes: {BlockLayout.DeltaEntryRegionBytes(block.DeltaCapacity)}");
        _output.WriteLine($"File bytes: {block.Length}");
    }

    readonly record struct StorageTimings(double ScanMilliseconds, double CompleteMilliseconds, double BuildMilliseconds, int SkippedRecordCount);

    static bool TryParseIndexStorageArguments(string[] arguments, out uint records)
    {
        records = 0;
        return arguments.Length == 2 &&
               arguments[0].Equals("--synthetic", StringComparison.OrdinalIgnoreCase) &&
               uint.TryParse(arguments[1], CultureInfo.InvariantCulture, out records) && records is >= 6 and <= 100_000_000;
    }

    static IEnumerable<IReadOnlyList<MftRecord>> StorageBatches(uint records)
    {
        var random = new Random(42);
        const int batchSize = 4096;
        for (uint start = 0; start < records;)
        {
            var batch = new MftRecord[(int)Math.Min(batchSize, records - start)];
            foreach (ref var record in batch.AsSpan())
            {
                var row = start++;
                var name = row == 5 ? "." : (row % 100) switch
                {
                    0 => ".git",
                    1 => "HEAD",
                    _ => string.Create(20, random, static (characters, generator) =>
                    {
                        foreach (ref var character in characters)
                        {
                            character = (char)generator.Next('a', 'z' + 1);
                        }
                    })
                };
                var directory = row == 5 || row % 10 == 0;
                record = new MftRecord(row, 5, new MftRecordFields((ushort)(directory ? 3 : 1)), name);
            }
            yield return batch;
        }
    }
}
