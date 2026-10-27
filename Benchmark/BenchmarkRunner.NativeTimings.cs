using System.Globalization;
using MFTLib;

namespace Benchmark;

public partial class BenchmarkRunner
{
    const string NativeTimingsUsage =
        "Usage: Benchmark.exe native-timings <--synthetic N|--dump path> [--iterations K]";

    const uint MinimumSyntheticRecords = 16;

    async Task<int> RunNativeTimingsAsync(string[] arguments)
    {
        if (!TryParseNativeTimingsArguments(arguments, out var records, out var dumpPath, out var iterations))
        {
            await _output.WriteLineAsync(NativeTimingsUsage).ConfigureAwait(false);
            return 1;
        }

        string? directory = null;
        try
        {
            if (dumpPath is null)
            {
                directory = Path.Combine(Path.GetTempPath(), $"mftlib-native-timings-{Guid.NewGuid():N}");
                Directory.CreateDirectory(directory);
                dumpPath = Path.Combine(directory, "synthetic.mft");
                var syntheticPath = dumpPath;
                await Task.Run(() => MftVolume.GenerateSyntheticMFT(syntheticPath, records)).ConfigureAwait(false);
            }

            PrintNativeTimings(dumpPath, iterations, _output.WriteLine);
            return 0;
        }
        catch (Exception exception) when (exception is IOException or InvalidDataException or ArgumentException
                                              or InvalidOperationException or UnauthorizedAccessException)
        {
            await _output.WriteLineAsync($"Error: {exception.Message}").ConfigureAwait(false);
            return 1;
        }
        finally
        {
            if (directory is not null)
            {
                Directory.Delete(directory, recursive: true);
            }
        }
    }

    static bool TryParseNativeTimingsArguments(string[] arguments, out uint records, out string? dumpPath,
        out int iterations)
    {
        records = 0;
        dumpPath = null;
        iterations = 3;
        if (arguments.Length != 2 && arguments.Length != 4)
        {
            return false;
        }

        if (arguments.Length == 4 &&
            (!arguments[2].Equals("--iterations", StringComparison.OrdinalIgnoreCase) ||
             !int.TryParse(arguments[3], CultureInfo.InvariantCulture, out iterations) || iterations <= 0))
        {
            return false;
        }

        if (arguments[0].Equals("--dump", StringComparison.OrdinalIgnoreCase))
        {
            dumpPath = arguments[1];
            return !string.IsNullOrWhiteSpace(dumpPath);
        }

        return arguments[0].Equals("--synthetic", StringComparison.OrdinalIgnoreCase) &&
               uint.TryParse(arguments[1], CultureInfo.InvariantCulture, out records) &&
               records >= MinimumSyntheticRecords && records <= 10_000_000;
    }

    // The native parse reads the next chunk while it fixes up and parses the current one, so the phases overlap:
    // the total bounds each phase and is shorter than their sum.
    static void PrintNativeTimings(string dumpPath, int iterations, Action<string> log)
    {
        using var input = MftDumpInput.Open(dumpPath);
        var samples = new List<MftParseTimings>(iterations);
        ulong totalRecords = 0;
        ulong usedRecords = 0;
        for (var iteration = 0; iteration < iterations; iteration++)
        {
            using var result = input.Parse();
            samples.Add(result.Timings);
            totalRecords = result.TotalRecords;
            usedRecords = result.UsedRecords;
        }

        var io = Median(samples.Select(timings => timings.NativeIo), static (left, right) => (left + right) / 2);
        var fixup = Median(samples.Select(timings => timings.NativeFixup), static (left, right) => (left + right) / 2);
        var parse = Median(samples.Select(timings => timings.NativeParse), static (left, right) => (left + right) / 2);
        var total = Median(samples.Select(timings => timings.NativeTotal), static (left, right) => (left + right) / 2);
        log("--- native-parse-timings ---");
        log($"  Records: {totalRecords} examined, {usedRecords} returned (median of {iterations} runs)");
        log(Phase("Native I/O", io, total));
        log(Phase("Native fixup", fixup, total));
        log(Phase("Native parse", parse, total));
        log(Phase("Native total", total, total));
        log($"  Bound: {(io > fixup + parse ? "the read (I/O) stage dominates" : "the parse (CPU) stage dominates")}");
        log("  Note: the read is a buffered read, so a warm page cache makes it faster than the disk.");
    }

    static string Phase(string label, TimeSpan duration, TimeSpan total)
    {
        var share = total > TimeSpan.Zero ? 100.0 * duration / total : 0.0;
        return string.Create(CultureInfo.InvariantCulture,
            $"  {label + ":",-14}{duration.TotalMilliseconds,10:F3} ms ({share,5:F1}% of total)");
    }
}
