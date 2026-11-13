using System.Globalization;
using MFTLib;
using MFTLib.Index;

namespace Benchmark;

public partial class BenchmarkRunner
{
    async Task<int> RunIndexWriteAsync(string[] arguments)
    {
        if (!TryParseWriteArguments(arguments, out var records, out var iterations, out var uniqueNames))
        {
            _output.WriteLine("Usage: Benchmark.exe index-write --synthetic N --iterations N --unique-names N");
            return 1;
        }

        var directory = Directory.CreateTempSubdirectory("mftlib-index-write-");
        try
        {
            await using var memory = new ScenarioMemoryCapture();
            var batches = CreateWriteBatches(records, uniqueNames);
            var timings = Enumerable.Range(0, 5).Select(_ => new double[iterations]).ToArray();

            LogSystemInfo(_output.WriteLine);
            _output.WriteLine($"Input records: {records}; unique names: {uniqueNames}");
            _output.WriteLine("Names: deterministic 20-character ASCII; reused strings; 4096-row batches prepared outside timing");
            _output.WriteLine("Independent phases, not additive; fresh mappings; excludes allocation, native parse, completion and flush; one warmup");
            for (var iteration = -1; iteration < iterations; iteration++)
            {
                var measured = MeasureWritePhases(directory.FullName, records, uniqueNames, batches);
                if (iteration >= 0)
                {
                    foreach (var (values, elapsed) in timings.Zip(measured))
                    {
                        values[iteration] = elapsed;
                    }
                }
            }

            string[] labels = ["Production transfer", "Name interning", "Empty-name row write", "Access scope", "UTF-16 append"];
            foreach (var (label, values) in labels.Zip(timings))
            {
                Array.Sort(values);
                var median = (values[(iterations - 1) / 2] + values[iterations / 2]) / 2;
                _output.WriteLine(string.Create(CultureInfo.InvariantCulture,
                    $"{label} median: {median:F3} ms (minimum {values[0]:F3}; maximum {values[^1]:F3}; n={iterations})"));
            }

            _output.WriteLine($"Live rows: {records}; name entries: {uniqueNames}");
            var peaks = await memory.FinishAsync().ConfigureAwait(false);
            _output.WriteLine($"Peak private bytes: {peaks.PeakPrivateBytes}");
            _output.WriteLine($"Peak working set bytes: {peaks.PeakWorkingSetBytes}");
            _output.WriteLine($"Memory method: {peaks.Method}; whole process including input and all independent phases");
            return 0;
        }
        finally
        {
            directory.Delete(recursive: true);
        }
    }

    double[] MeasureWritePhases(string directory, uint records, uint uniqueNames, IReadOnlyList<MftRecord>[] batches)
    {
        var result = new double[5];
        using (var block = CreateWriteBlock(directory, records, uniqueNames))
        {
            var writer = new BlockWriter(block);
            // Packed before the clock starts, as a native parse hands the writer its slices already packed.
            using var source = new PackedSliceSource(batches.Select(MftParsedSlice.Pack).ToArray());
            var started = _timeProvider.GetTimestamp();
            var written = MftBlockRowWriter.Write(writer, source, new MftScanOptions(), null,
                CancellationToken.None);
            result[0] = _timeProvider.GetElapsedTime(started).TotalMilliseconds;
            if (written.SkippedRecordCount != 0 || block.Header.LiveRowCount != records || block.NameEntryCount != uniqueNames)
            {
                throw new InvalidOperationException("Production transfer did not preserve the synthetic inventory.");
            }
        }

        using (var block = CreateWriteBlock(directory, records, uniqueNames))
        {
            using var access = block.TakeAccess();
            var started = _timeProvider.GetTimestamp();
            foreach (var batch in batches)
            {
                foreach (var record in batch)
                {
                    if (!NamePool.TryGetOrAdd(block, record.FileName, out _))
                    {
                        throw new InvalidOperationException("The name interning input did not fit.");
                    }
                }
            }
            result[1] = _timeProvider.GetElapsedTime(started).TotalMilliseconds;
        }

        using (var block = CreateWriteBlock(directory, records, uniqueNames))
        {
            var writer = new BlockWriter(block);
            var columns = new RowColumns(5, RowFlags.InUse, 0, 0, 0, 0);
            var started = _timeProvider.GetTimestamp();
            for (uint row = 0; row < records; row++)
            {
                if (!writer.TryWriteRow(row, default, in columns))
                {
                    throw new InvalidOperationException("The row input did not fit.");
                }
            }
            result[2] = _timeProvider.GetElapsedTime(started).TotalMilliseconds;
            started = _timeProvider.GetTimestamp();
            for (uint row = 0; row < records; row++)
            {
                using var access = block.TakeAccess();
            }
            result[3] = _timeProvider.GetElapsedTime(started).TotalMilliseconds;
        }

        // The pre-dedup writer copied every spelling as UTF-16. This isolates that append algorithm,
        // not the former complete writer or format, and retains all copies to prevent dead-store removal.
        var characters = new char[checked((int)records * 20)];
        var appendStarted = _timeProvider.GetTimestamp();
        var offset = 0;
        foreach (var batch in batches)
        {
            foreach (var record in batch)
            {
                record.FileName.CopyTo(characters.AsSpan(offset));
                offset += record.FileName.Length;
            }
        }
        result[4] = _timeProvider.GetElapsedTime(appendStarted).TotalMilliseconds;
        GC.KeepAlive(characters);
        return result;
    }

    static BlockFile CreateWriteBlock(string directory, uint records, uint uniqueNames) => BlockFile.Create(new BlockFileCreateOptions
    {
        Path = Path.Combine(directory, "write.mlix"),
        Root = new VolumeKey('T'),
        VolumeSerial = 0,
        ProducerKind = ProducerKind.Mft,
        RootRow = 5,
        SlotCapacity = records,
        NameEntryCapacity = uniqueNames,
        NamePoolCapacity = checked(uniqueNames * 20),
        DeltaCapacity = records,
        DeleteOnClose = true
    });

    static IReadOnlyList<MftRecord>[] CreateWriteBatches(uint records, uint uniqueNames)
    {
        var names = new string[uniqueNames];
        for (uint index = 0; index < uniqueNames; index++)
        {
            names[index] = string.Create(CultureInfo.InvariantCulture, $"file-{index:D11}.dat");
        }

        var batches = new IReadOnlyList<MftRecord>[(records + 4095) / 4096];
        for (uint start = 0; start < records; start += 4096)
        {
            var batch = new MftRecord[Math.Min(4096, records - start)];
            for (uint offset = 0; offset < batch.Length; offset++)
            {
                var row = start + offset;
                batch[offset] = new MftRecord(row, 5, new MftRecordFields(1), names[row % uniqueNames]);
            }
            batches[start / 4096] = batch;
        }
        return batches;
    }

    static bool TryParseWriteArguments(string[] arguments, out uint records, out int iterations, out uint uniqueNames)
    {
        records = 0;
        iterations = 0;
        uniqueNames = 0;
        return arguments.Length == 6 && arguments[0] == "--synthetic" && arguments[2] == "--iterations" && arguments[4] == "--unique-names" &&
               uint.TryParse(arguments[1], CultureInfo.InvariantCulture, out records) && records is >= 6 and <= 20_000_000 &&
               int.TryParse(arguments[3], CultureInfo.InvariantCulture, out iterations) && iterations is >= 1 and <= 100 &&
               uint.TryParse(arguments[5], CultureInfo.InvariantCulture, out uniqueNames) && uniqueNames >= 1 && uniqueNames <= records;
    }
}
