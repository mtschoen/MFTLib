using MFTLib;

namespace TestProgram;

// The stream-records mode: one drive parsed into a native MftResult, with progress, a thread
// allowance and a cancellation token, then read back every way the result offers. Shares its
// result report with the offline parse-file --stream variant.
partial class DriveScanner
{
    internal Func<MftVolume, string?, MatchFlags, IProgress<MftScanProgress>?, ParseThreadAllowance?, CancellationToken,
        MftResult> _streamRecords = (volume, filter, matchFlags, progress, parseThreads, cancellationToken) =>
        volume.StreamRecords(filter, matchFlags, progress, parseThreads, cancellationToken);

    internal void StreamRecords(string drive, ModeOptions options)
    {
        RunOnVolume(drive, options, volume =>
        {
            var parseThreads = options.ParseThreads is { } count ? new ParseThreadAllowance(count) : null;
            using var cancellation = options.TimeoutSeconds is { } seconds
                ? _createTimedCancellation(TimeSpan.FromSeconds(seconds))
                : new CancellationTokenSource();
            var progress = new ScanProgressReporter(_writeLine);

            MftRecord? retained;
            try
            {
                using var result = _streamRecords(volume, options.Name, options.ToMatchFlags(), progress, parseThreads,
                    cancellation.Token);
                progress.Complete();
                if (parseThreads is not null)
                {
                    _writeLine($"Parse thread allowance: {parseThreads.Count}");
                }

                retained = ReportResult(result, options);
            }
            catch (OperationCanceledException) when (cancellation.IsCancellationRequested)
            {
                progress.Complete();
                _writeLine("Scan cancelled before it finished.");
                return;
            }

            PrintRetained(retained);
        });
    }

    // Reads one result every way it offers and returns the first record, materialized so it
    // outlives the result the caller is about to dispose.
    MftRecord? ReportResult(MftResult result, ModeOptions options)
    {
        _writeLine($"Parsed {result.TotalRecords} records, {result.UsedRecords} kept, " +
                   $"{result.NativeCompactBytes} native bytes");
        _writeLine($"  {FormatTimings(result.Timings)}");

        MftRecord? retained = null;
        var directories = 0;
        foreach (var record in result)
        {
            retained ??= record.Materialize();
            directories += record.IsDirectory ? 1 : 0;
        }

        _writeLine($"Enumerated in place: {directories} directories");

        var batches = 0;
        var materialized = 0;
        var batchSource = options.BatchSize is { } batchSize
            ? result.MaterializeBatches(batchSize)
            : result.MaterializeBatches();
        foreach (var batch in batchSource)
        {
            batches++;
            materialized += batch.Length;
        }

        _writeLine($"Materialized {materialized} records in {batches} batches");

        var all = result.ToArray();
        _writeLine($"ToArray holds {all.Length} records");
        PrintRecords(all);
        return retained;
    }

    void PrintRetained(MftRecord? retained)
    {
        if (retained is not { } record)
        {
            _writeLine("No record to retain.");
            return;
        }

        _writeLine("Retained after the result was disposed:");
        _writeLine(FormatRecord(record));
    }

    // Reports the last sample of each scan phase, on the reporting thread, so the output keeps its order.
    sealed class ScanProgressReporter(Action<string> writeLine) : IProgress<MftScanProgress>
    {
        MftScanProgress? _last;

        public void Report(MftScanProgress value)
        {
            if (_last is { } previous && previous.Phase != value.Phase)
            {
                Print(previous);
            }

            _last = value;
        }

        public void Complete()
        {
            if (_last is { } last)
            {
                Print(last);
                _last = null;
            }
        }

        void Print(MftScanProgress sample)
        {
            writeLine($"  {sample.Phase}: {sample.RecordsScanned} of {sample.TotalRecords} records " +
                      $"after {sample.Elapsed.TotalMilliseconds:F0}ms");
        }
    }
}
