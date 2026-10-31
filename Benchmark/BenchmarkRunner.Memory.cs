using System.ComponentModel;
using System.Globalization;
using System.Text.Json;
using MFTLib;
using MFTLib.Index;

namespace Benchmark;

public partial class BenchmarkRunner
{
    internal static async Task<int> RunMemoryWorkerAsync(TextReader input, TextWriter output, TextWriter error)
    {
        ArgumentNullException.ThrowIfNull(input);
        ArgumentNullException.ThrowIfNull(output);
        ArgumentNullException.ThrowIfNull(error);

        try
        {
            var text = await input.ReadToEndAsync().ConfigureAwait(false);
            if (string.IsNullOrWhiteSpace(text))
            {
                throw new ArgumentException("Memory worker requires a JSON request.");
            }

            var request = JsonSerializer.Deserialize<ScenarioMemoryRequest>(text)
                ?? throw new ArgumentException("Memory worker request cannot be null.");
            var result = await MeasureMemoryAsync(request).ConfigureAwait(false);
            await output.WriteLineAsync(JsonSerializer.Serialize(result)).ConfigureAwait(false);
            return 0;
        }
        catch (Exception exception) when (exception is JsonException or ArgumentException or IOException
                                              or InvalidDataException or InvalidOperationException
                                              or UnauthorizedAccessException or PlatformNotSupportedException
                                              or DllNotFoundException or BadImageFormatException or Win32Exception)
        {
            await error.WriteLineAsync($"Error: {exception.Message}").ConfigureAwait(false);
            return 1;
        }
    }

    internal static async Task<ScenarioMemoryResult> MeasureMemoryAsync(ScenarioMemoryRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);
        if (string.IsNullOrWhiteSpace(request.InputPath) || request.Iterations <= 0)
        {
            throw new ArgumentException("Memory worker requires a nonblank input path and positive iteration count.");
        }

        await using var capture = new ScenarioMemoryCapture();
        return request.Scenario switch
        {
            "exact-search" or "substring-search" => await MeasureIndexMemoryAsync(request, capture).ConfigureAwait(false),
            "native-parse-timings" => await MeasureNativeMemoryAsync(request, capture).ConfigureAwait(false),
            "enumeration-walk-sequential" or "enumeration-walk-parallel" =>
                await MeasureWalkMemoryAsync(request, capture).ConfigureAwait(false),
            _ => throw new ArgumentException($"Unknown benchmark memory scenario '{request.Scenario}'.")
        };
    }

    internal static async Task<ScenarioMemoryResult> MeasureIndexMemoryAsync(
        ScenarioMemoryRequest request, ScenarioMemoryCapture capture)
    {
        if (request.Scenario is not ("exact-search" or "substring-search") || request.Rows != 0)
        {
            throw new ArgumentException($"Invalid index memory scenario '{request.Scenario}'.");
        }

        var input = request.Index ?? throw new ArgumentException("Index memory request requires root metadata.");
        if (string.IsNullOrWhiteSpace(request.InputPath) || string.IsNullOrWhiteSpace(input.RootDirectory) ||
            !RootKey.TryParse(input.Drive, out var rootKey) || !Enum.IsDefined(input.Producer) ||
            !CacheTag.IsValidStorage(input.PackedFourCc))
        {
            throw new ArgumentException("Invalid index memory identity.");
        }

        var expectedShape = input.Producer == ProducerKind.Enumeration
            ? ScanShape.ForEnumeration(new DirectoryWalkOptions(), rootKey, input.RootDirectory)
            : ScanShape.ForMft(includeFreed: false);
        if (input.ScanShape != expectedShape)
        {
            throw new ArgumentException("Unsupported index memory scan shape.");
        }

        var tag = CacheTag.FromStorage(input.PackedFourCc, input.TagVersion);
        await using var index = await FileIndex.OpenAsync(CachedBlockOptions(rootKey, input.RootDirectory,
            input.VolumeSerial, input.Producer) with
        {
            CacheDirectory = request.InputPath,
            CacheMode = CacheMode.ExistingOnly,
            CacheTag = tag
        }, CancellationToken.None).ConfigureAwait(false);
        using var borrow = index.BorrowCurrentSnapshot();
        var root = borrow.RootBlock;
        if (root is null || root.Block.Header.ScanShape != input.ScanShape)
        {
            throw new InvalidOperationException($"Root {input.Drive}: unavailable or replaced during memory measurement.");
        }

        var query = request.Scenario == "exact-search"
            ? new SearchQuery(IndexQueryName, NameMatchMode.Exact)
            : new SearchQuery(IndexQueryName);
        for (long iteration = 0; iteration <= request.Iterations; iteration++)
        {
            var matches = index.Search(query);
            capture.Sample();
            GC.KeepAlive(matches);
        }

        return await capture.FinishAsync().ConfigureAwait(false);
    }

    internal static async Task<ScenarioMemoryResult> MeasureNativeMemoryAsync(
        ScenarioMemoryRequest request, ScenarioMemoryCapture capture)
    {
        if (request.Scenario != "native-parse-timings" || request.Index is not null || request.Rows != 0)
        {
            throw new ArgumentException("Invalid native memory request.");
        }

        using var input = MftDumpInput.Open(request.InputPath);
        for (var iteration = 0; iteration < request.Iterations; iteration++)
        {
            using var result = input.Parse();
            capture.Sample();
            GC.KeepAlive(result);
        }

        return await capture.FinishAsync().ConfigureAwait(false);
    }

    internal static async Task<ScenarioMemoryResult> MeasureWalkMemoryAsync(
        ScenarioMemoryRequest request, ScenarioMemoryCapture capture)
    {
        if (request.Scenario is not ("enumeration-walk-sequential" or "enumeration-walk-parallel") ||
            request.Index is not null || request.Rows is 0 or > 10_000_000 ||
            !Directory.Exists(request.InputPath))
        {
            throw new ArgumentException("Invalid enumeration walk memory request.");
        }

        var temporary = Directory.CreateTempSubdirectory("mftlib-walk-memory-");
        try
        {
            var runner = new BenchmarkRunner(TextWriter.Null);
            var sequential = request.Scenario == "enumeration-walk-sequential";
            for (long iteration = 0; iteration <= request.Iterations; iteration++)
            {
                await runner.MeasureEnumerationWalkAsync(request.InputPath,
                    Path.Combine(temporary.FullName, "walk.mlix"), request.Rows,
                    sequential, capture.Sample).ConfigureAwait(false);
            }

            return await capture.FinishAsync().ConfigureAwait(false);
        }
        finally
        {
            temporary.Delete(recursive: true);
        }
    }

    internal static void PrintMemory(ScenarioMemoryResult result, Action<string> log)
    {
        log(string.Create(CultureInfo.InvariantCulture, $"  Peak private bytes: {result.PeakPrivateBytes}"));
        log(string.Create(CultureInfo.InvariantCulture, $"  Peak working set: {result.PeakWorkingSetBytes}"));
        log($"  Memory measurement: {result.Method}");
    }
}
