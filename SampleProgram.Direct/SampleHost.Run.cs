using MFTLib;
using MFTLib.Index;

namespace SampleProgram.Direct;

// The Direct sample reads an MFT in this process, with no broker and no watch: the live volume of an elevated run
// or a saved $MFT file. It never caches, so every verb scans.
partial class SampleHost
{
    internal Func<DirectArguments, MftIndexSource> _createSource = CreateSourceNative;

    // The elevation flow is Windows only (UAC and its dialog), so elsewhere the verb runs straight into its platform check.
    internal Func<bool> _isWindows = OperatingSystem.IsWindows;

    internal int Run(string[] arguments)
    {
        if (!DirectArguments.TryParse(arguments, out var parsed, out var error))
        {
            _writeLine(error);
            _writeLine(DirectArguments.Usage);
            return 2;
        }

        var need = parsed.RequiresElevation && _isWindows() ? ElevationNeed.SelfElevate : ElevationNeed.None;
        return RunWithElevation(arguments, need, () => RunVerb(parsed));
    }

    // The console entry point has no synchronization context, so blocking here cannot deadlock.
    int RunVerb(DirectArguments parsed)
    {
        try
        {
            // aislop-ignore-next-line csharp-sync-over-async -- the console entry point has no synchronization context, so blocking cannot deadlock
            RunVerbAsync(parsed, CancellationToken.None).GetAwaiter().GetResult();
            return 0;
        }
        catch (Exception exception)
        {
            WriteFailure("Error: ", exception);
            return 1;
        }
    }

    // The type and message of the failure and of everything it wraps, one line each; no stack trace.
    void WriteFailure(string prefix, Exception exception)
    {
        _writeLine($"{prefix}{exception.GetType().Name}: {exception.Message}");
        IEnumerable<Exception> inner = exception is AggregateException aggregate ? aggregate.InnerExceptions : exception.InnerException is { } single ? [single] : [];
        foreach (var cause in inner)
        {
            WriteFailure("  caused by ", cause);
        }
    }

    /// <summary>The scan options a local source gets: freed records are kept only when the query will show them.</summary>
    internal static BrokerScanOptions ScanOptions(DirectArguments parsed) => new() { IncludeFreed = parsed.IncludeFreed };

    static MftIndexSource CreateSourceNative(DirectArguments parsed)
    {
        return parsed.Source is SourceKind.Dump
            ? MftIndexSources.FromMftDumpFile(parsed.DumpFile ?? throw new ArgumentException("A dump source needs a dump file."), char.ToUpperInvariant(parsed.Drive[0]))
            : MftIndexSources.FromLocalVolumes(ScanOptions(parsed));
    }

    async Task<FileIndex> OpenIndexAsync(DirectArguments parsed, CancellationToken cancellationToken)
    {
        var letter = char.ToUpperInvariant(parsed.Drive[0]);
        var options = new FileIndexOptions
        {
            Drives = [parsed.Source is SourceKind.Dump ? new IndexedDrive(letter, "dump:/" + letter, 0) : _resolveDrive(parsed.Drive)],
            NoCache = true,
            CacheDirectory = _cacheDirectory,
            ProducerPolicy = ProducerPolicy.Mft,
            MftSource = _createSource(parsed),
            Progress = new PhaseReporter(_writeLine)
        };
        var index = await FileIndex.OpenAsync(options, cancellationToken).ConfigureAwait(false);
        try
        {
            ThrowIfNotReady(index.Drives.Single());
            return index;
        }
        catch
        {
            await index.DisposeAsync().ConfigureAwait(false);
            throw;
        }
    }
}
