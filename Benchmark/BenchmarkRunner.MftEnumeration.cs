using System.ComponentModel;
using System.Globalization;
using MFTLib;
using MFTLib.Index;

namespace Benchmark;

public partial class BenchmarkRunner
{
    const string MftEnumerationUsage =
        "Usage: Benchmark.exe mft-enumeration [--synthetic N|--dump path] [--iterations K] [--out report.txt]";

    const uint DefaultMftEnumerationRecords = 1_000_000;
    internal const string MftEnumerationExactName = ".git";
    internal const string MftEnumerationSubstringName = "config";
    internal const string MftEnumerationScan = "mft-enumeration-scan";
    internal const string MftEnumerationRows = "mft-enumeration-rows";
    internal const string MftEnumerationExact = "mft-enumeration-exact-name";
    internal const string MftEnumerationSubstring = "mft-enumeration-substring-name";

    internal static readonly string[] MftEnumerationScenarios =
        [MftEnumerationScan, MftEnumerationRows, MftEnumerationExact, MftEnumerationSubstring];

    static readonly SearchQuery MftEnumerationAllRows = new(null);
    static readonly SearchQuery MftEnumerationExactQuery = new(MftEnumerationExactName, NameMatchMode.Exact);
    static readonly SearchQuery MftEnumerationSubstringQuery = new(MftEnumerationSubstringName);

    async Task<int> RunMftEnumerationAsync(string[] arguments)
    {
        if (!TryParseMftEnumerationArguments(arguments, out var records, out var dumpPath, out var iterations,
                out var outputPath))
        {
            await _output.WriteLineAsync(MftEnumerationUsage).ConfigureAwait(false);
            return 1;
        }

        var report = new ReportWriter(_output);
        DirectoryInfo? temporary = null;
        try
        {
            var inputDescription = "saved dump";
            if (dumpPath is null)
            {
                temporary = Directory.CreateTempSubdirectory("mftlib-mft-enumeration-");
                var syntheticPath = Path.Combine(temporary.FullName, "synthetic.mft");
                await Task.Run(() => MftVolume.GenerateSyntheticMFT(syntheticPath, records)).ConfigureAwait(false);
                dumpPath = syntheticPath;
                inputDescription = "synthetic dump from the native bulk generator";
            }

            LogSystemInfo(report.Log);
            await MeasureMftEnumerationAsync(Path.GetFullPath(dumpPath), inputDescription, iterations, report.Log)
                .ConfigureAwait(false);
            await report.SaveAsync(outputPath).ConfigureAwait(false);
            return 0;
        }
        catch (Exception exception) when (exception is IOException or InvalidDataException or ArgumentException
                                              or InvalidOperationException or UnauthorizedAccessException
                                              or PlatformNotSupportedException or Win32Exception)
        {
            await _output.WriteLineAsync($"Error: {exception.Message}").ConfigureAwait(false);
            return 1;
        }
        finally
        {
            temporary?.Delete(recursive: true);
        }
    }

    static bool TryParseMftEnumerationArguments(string[] arguments, out uint records, out string? dumpPath,
        out int iterations, out string? outputPath)
    {
        records = DefaultMftEnumerationRecords;
        dumpPath = null;
        iterations = 5;
        outputPath = null;
        if (arguments.Length % 2 != 0)
        {
            return false;
        }

        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        for (var index = 0; index < arguments.Length; index += 2)
        {
            var name = arguments[index].ToLowerInvariant();
            var value = arguments[index + 1];
            if (!seen.Add(name))
            {
                return false;
            }

            var valid = name switch
            {
                "--synthetic" => uint.TryParse(value, CultureInfo.InvariantCulture, out records) &&
                                 records is >= MinimumSyntheticRecords and <= 10_000_000,
                "--iterations" => int.TryParse(value, CultureInfo.InvariantCulture, out iterations) && iterations > 0,
                "--dump" => !string.IsNullOrWhiteSpace(dumpPath = value),
                "--out" => !string.IsNullOrWhiteSpace(outputPath = value),
                _ => false
            };
            if (!valid)
            {
                return false;
            }
        }

        // A run reads exactly one input: the generated synthetic dump or the caller's saved dump.
        return !(seen.Contains("--synthetic") && seen.Contains("--dump"));
    }

    async Task MeasureMftEnumerationAsync(string dumpPath, string inputDescription, int iterations,
        Action<string> log)
    {
        ulong examined;
        uint recordSize;
        using (var input = MftDumpInput.Open(dumpPath))
        {
            recordSize = input.RecordSize;
            examined = (ulong)input.LengthBytes / recordSize;
        }

        log($"Input: {inputDescription} '{Path.GetFileName(dumpPath)}': {examined} records of {recordSize} bytes");
        log($"Queries: exact \"{MftEnumerationExactName}\", substring \"{MftEnumerationSubstringName}\" (case-insensitive, live rows)");
        log($"Runs: one untimed warm-up, then the median of {iterations}; each run opens a fresh index over the dump source (never cached)");
        log("Timing: scan is FileIndex.OpenAsync until the root is Ready; row passes run on that open index; disposal is not timed");
        log("Records per second: MFT records examined for the scan; index slots covered for row passes (exact matching reads name postings)");

        double[][] samples = [new double[iterations], new double[iterations], new double[iterations], new double[iterations]];
        var results = new long[MftEnumerationScenarios.Length];
        uint slots = 0;
        uint liveRows = 0;
        for (var iteration = -1; iteration < iterations; iteration++)
        {
            var started = _timeProvider.GetTimestamp();
            await using var index = await OpenMftEnumerationIndexAsync(dumpPath).ConfigureAwait(false);
            RecordPipelineSample(samples[0], iteration, started);
            (slots, liveRows) = MftEnumerationRowCounts(index);
            for (var scenario = 1; scenario < MftEnumerationScenarios.Length; scenario++)
            {
                started = _timeProvider.GetTimestamp();
                results[scenario] = RunMftEnumerationPass(index, MftEnumerationScenarios[scenario]);
                RecordPipelineSample(samples[scenario], iteration, started);
            }
        }

        var memory = new List<ScenarioMemoryResult>(MftEnumerationScenarios.Length);
        foreach (var scenario in MftEnumerationScenarios)
        {
            memory.Add(await _measureMemory(new ScenarioMemoryRequest(scenario, dumpPath, iterations))
                .ConfigureAwait(false));
        }

        for (var scenario = 0; scenario < MftEnumerationScenarios.Length; scenario++)
        {
            var median = Median(samples[scenario], static (left, right) => (left + right) / 2);
            var covered = scenario == 0 ? examined : slots;
            log($"--- {MftEnumerationScenarios[scenario]} ---");
            var suffix = scenario switch
            {
                0 => $"; Slots: {slots}; Live rows: {liveRows}",
                1 => $"; Name checksum: {results[scenario]}",
                _ => $"; Matches: {results[scenario]}"
            };
            log(string.Create(CultureInfo.InvariantCulture, $"  Median: {median:F3} ms; Records: {covered}{suffix}"));
            log(RecordsPerSecond(covered, median));
            PrintMemory(memory[scenario], log);
        }
    }

    static string RecordsPerSecond(ulong records, double medianMilliseconds) => medianMilliseconds > 0
        ? string.Create(CultureInfo.InvariantCulture, $"  Records per second: {records / (medianMilliseconds / 1000):F0}")
        : "  Records per second: unavailable (median is zero)";

    /// <summary>
    ///     Opens the index a consumer gets for a saved volume: a dump source, which the library scans in full and
    ///     never caches. A root that does not reach Ready fails the run with the library's reason.
    /// </summary>
    static async Task<FileIndex> OpenMftEnumerationIndexAsync(string dumpPath)
    {
        var index = await FileIndex.OpenAsync(new FileIndexOptions
        {
            MftSource = MftIndexSources.FromMftDumpFile(dumpPath, 'T')
        }, CancellationToken.None).ConfigureAwait(false);
        var status = index.Status;
        if (status.State == RootState.Ready)
        {
            return index;
        }

        await index.DisposeAsync().ConfigureAwait(false);
        var reason = status.Failure is { } failure ? $" ({failure.Message})" : "";
        throw new InvalidOperationException($"Root {status.Root}: {status.State}{reason}.");
    }

    /// <summary>The index's row slots (record numbers up to the highest one in use) and how many hold live rows.</summary>
    static (uint Slots, uint LiveRows) MftEnumerationRowCounts(FileIndex index)
    {
        using var borrow = index.BorrowCurrentSnapshot();
        var header = borrow.RootBlock?.Block.Header
                     ?? throw new InvalidOperationException("The scanned index has no snapshot.");
        return (header.RowCount, header.LiveRowCount);
    }

    /// <summary>
    ///     Runs one consumer-shaped row pass: every live row with its name consumed, or a name-filtered search.
    ///     Returns the name checksum or the match count, so the work cannot be skipped.
    /// </summary>
    static long RunMftEnumerationPass(FileIndex index, string scenario)
    {
        switch (scenario)
        {
            case MftEnumerationRows:
                long checksum = 0;
                foreach (var row in index.EnumerateRows(MftEnumerationAllRows))
                {
                    foreach (var character in row.Name)
                    {
                        checksum = unchecked(checksum * 31 + character);
                    }
                }
                return checksum;
            case MftEnumerationExact:
                return index.Search(MftEnumerationExactQuery).Count;
            case MftEnumerationSubstring:
                return index.Search(MftEnumerationSubstringQuery).Count;
            default:
                throw new ArgumentException($"Unknown MFT enumeration row pass '{scenario}'.");
        }
    }

    internal static async Task<ScenarioMemoryResult> MeasureMftEnumerationMemoryAsync(
        ScenarioMemoryRequest request, ScenarioMemoryCapture capture)
    {
        if (!MftEnumerationScenarios.Contains(request.Scenario) || request.Index is not null || request.Rows != 0 ||
            !File.Exists(request.InputPath))
        {
            throw new ArgumentException("Invalid MFT enumeration memory request.");
        }

        if (request.Scenario == MftEnumerationScan)
        {
            for (long iteration = 0; iteration <= request.Iterations; iteration++)
            {
                await using var scanned = await OpenMftEnumerationIndexAsync(request.InputPath).ConfigureAwait(false);
                capture.Sample();
            }

            return await capture.FinishAsync().ConfigureAwait(false);
        }

        await using var index = await OpenMftEnumerationIndexAsync(request.InputPath).ConfigureAwait(false);
        for (long iteration = 0; iteration <= request.Iterations; iteration++)
        {
            RunMftEnumerationPass(index, request.Scenario);
            capture.Sample();
        }

        return await capture.FinishAsync().ConfigureAwait(false);
    }
}
