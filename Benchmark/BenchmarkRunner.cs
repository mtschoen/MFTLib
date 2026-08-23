using System.Diagnostics;
using System.IO.MemoryMappedFiles;
using System.Runtime.InteropServices;
using MFTLib;

namespace Benchmark;

#pragma warning disable CA1416 // Validate platform compatibility - Benchmark is Windows-only

// One benchmark scenario: a display name plus the filter/match-flags it exercises.
readonly record struct BenchmarkScenario(string Name, string? Filter, MatchFlags MatchFlags);

sealed record ReportMetrics(
    double CompatThroughput,
    long CompatPeakPrivateBytes,
    long BoundedPeakPrivateBytes,
    long BrokerStreamPeakPrivateBytes);

public partial class BenchmarkRunner
{
    // File system seams
    internal Action<string> _deleteFile = File.Delete;
    internal Func<string, bool> _fileExists = File.Exists;

    // Synthetic generation seam
    internal Action<string, ulong, uint> _generateSynthetic = MftVolume.GenerateSyntheticMFT;
    internal Func<string, FileInfo> _getFileInfo = path => new FileInfo(path);

    internal Func<string> _getGitCommitHash = () =>
    {
        try
        {
            using var process = Process.Start(new ProcessStartInfo
            {
                FileName = "git",
                Arguments = "rev-parse HEAD",
                RedirectStandardOutput = true,
                UseShellExecute = false,
                CreateNoWindow = true
            });
            if (process != null)
            {
                var sha = process.StandardOutput.ReadToEnd().Trim();
                process.WaitForExit();
                if (sha.Length == 40)
                {
                    return sha;
                }
            }
        }
        catch
        {
            // aislop-ignore-next-line SwallowedException -- fallback when git is not available
        }

        return "0000000000000000000000000000000000000000";
    };

    internal Func<long> _getPeakPrivateBytes64 = () => Process.GetCurrentProcess().PrivateMemorySize64;
    internal Func<long> _getPeakWorkingSet64 = () => Process.GetCurrentProcess().PeakWorkingSet64;
    internal Func<Stopwatch, double> _getStopwatchElapsedMs = stopwatch => stopwatch.Elapsed.TotalMilliseconds;

    // Measurement seams
    internal Func<long> _getTotalAllocatedBytes = () => GC.GetTotalAllocatedBytes(precise: true);

    internal Func<string, int, (int RecordCount, ulong NativeCompactBytes)> _parseBounded = (path, batchSize) =>
    {
        using var result = MftVolume.StreamMFTFromFile(path);
        var count = 0;
        foreach (var batch in result.MaterializeBatches(batchSize))
        {
            count += batch.Length;
        }

        return (count, result.NativeCompactBytes);
    };

    // Mirrors the production broker transport: a page-file-backed named map written through
    // RealMmfWriter and read back through RealMmfReader. An earlier revision buffered the whole
    // payload in a MemoryStream, which measured the benchmark's own managed heap rather than the
    // broker path, and reported the broker scenario as using more memory than compat.
    internal Func<string, int, (int RecordCount, ulong NativeCompactBytes)> _parseBrokerStream = (path, batchSize) =>
    {
        using var result = MftVolume.StreamMFTFromFile(path);
        var nativeBytes = result.NativeCompactBytes;

        var scanBatches = result.MaterializeBatches(batchSize)
            .Select(batch => (IReadOnlyList<ScanRecord>)batch.Select(record => new ScanRecord(
                record.RecordNumber,
                record.ParentRecordNumber,
                0,
                0,
                (uint)record.FileAttributes,
                record.IsDirectory,
                record.FileName,
                record.FullPath ?? record.FileName)).ToArray());

        var mmfName = "mftlib-benchmark-" + Guid.NewGuid().ToString("N");
        var capacity = EstimateScanPayloadCapacity(result.UsedRecords);
        using var map = MemoryMappedFile.CreateNew(mmfName, capacity);

        var writeResult = new RealMmfWriter().Write(mmfName, scanBatches, CancellationToken.None);

        var count = 0;
        foreach (var batch in new RealMmfReader().ReadBatches(
                     mmfName, writeResult.ByteLength, batchSize, CancellationToken.None))
        {
            count += batch.Length;
        }

        return (count, nativeBytes);
    };

    internal Func<string, (int RecordCount, ulong NativeCompactBytes)> _parseCompat = path =>
    {
        using var result = MftVolume.StreamMFTFromFile(path);
        var records = result.ToArray();
        return (records.Length, result.NativeCompactBytes);
    };

    // Scenario execution seams (used by measure subcommand)
    internal Func<string, string?, MatchFlags, (MftRecord[] Records, MftParseTimings Timings)> _parseFromFile =
        (path, filter, flags) =>
        {
            var records = MftVolume.ParseMFTFromFile(path, filter, flags, out var timings);
            return (records, timings);
        };

    internal Func<string, string> _readAllText = File.ReadAllText;

    // Child process runner seam
    internal Func<string[], (int ExitCode, string Stdout, string Stderr)> _runChildProcess = arguments =>
    {
        var processPath = Environment.ProcessPath;
        var assemblyLocation = typeof(BenchmarkRunner).Assembly.Location;
        var isWindows = RuntimeInformation.IsOSPlatform(OSPlatform.Windows);
        var candidateExe = Path.ChangeExtension(assemblyLocation, isWindows ? ".exe" : null);

        var startInfo = new ProcessStartInfo
        {
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true
        };

        if (processPath != null && Path.GetFileNameWithoutExtension(processPath).Equals("Benchmark", StringComparison.OrdinalIgnoreCase))
        {
            startInfo.FileName = processPath;
        }
        else if (File.Exists(candidateExe))
        {
            startInfo.FileName = candidateExe;
        }
        else
        {
            var dotnetExe = processPath != null && Path.GetFileNameWithoutExtension(processPath).Equals("dotnet", StringComparison.OrdinalIgnoreCase)
                ? processPath
                : "dotnet";
            startInfo.FileName = dotnetExe;
            startInfo.ArgumentList.Add(assemblyLocation);
        }

        foreach (var argument in arguments)
        {
            startInfo.ArgumentList.Add(argument);
        }

        using var process = Process.Start(startInfo);
        if (process == null)
        {
            throw new InvalidOperationException("Failed to start child benchmark process");
        }

        var stdout = process.StandardOutput.ReadToEnd();
        var stderr = process.StandardError.ReadToEnd();
        process.WaitForExit();
        return (process.ExitCode, stdout, stderr);
    };

    // System info and Git seams
    internal SystemInfo _systemInfo = new();
    internal Action<string, string> _writeAllText = File.WriteAllText;

    // Console output seams
    internal Action<string> _writeLineToConsole = Console.WriteLine;
    internal Action<string> _writeToConsole = Console.Write;

    // The production client sizes the map from the record count before the scan is read back.
    static long EstimateScanPayloadCapacity(ulong recordCount)
    {
        const long bytesPerRecordEstimate = 512;
        const long minimumCapacity = 1L * 1024 * 1024;
        return Math.Max(minimumCapacity, ((long)recordCount + 1L) * bytesPerRecordEstimate);
    }

    public int Run(string[] arguments)
    {
        if (arguments.Length > 0 && arguments[0].Equals("measure", StringComparison.OrdinalIgnoreCase))
        {
            return RunMeasure(arguments.AsSpan(1).ToArray());
        }

        if (arguments.Length > 0 && arguments[0].Equals("compare", StringComparison.OrdinalIgnoreCase))
        {
            return RunCompare(arguments.AsSpan(1).ToArray());
        }

        return RunParent(arguments);
    }
}
