using MFTLib;
using MFTLib.Index;

namespace SampleProgram.Direct;

// The Direct sample reads an MFT in this process, with no broker and no watch: the live volume of an elevated run
// or a saved $MFT file. It never caches, so every verb scans.
partial class SampleHost
{
    internal Func<DirectArguments, MftIndexSource> _createSource = CreateSourceNative;
    internal Func<string, IndexedDrive> _resolveDrive = ResolveDriveNative;

    // Where the index keeps its cache folder; the NoCache open still resolves one, so a test names its own.
    internal string? _cacheDirectory;

    internal int Run(string[] arguments)
    {
        if (!DirectArguments.TryParse(arguments, out var parsed, out var error))
        {
            _writeLine(error!);
            _writeLine(DirectArguments.Usage);
            return 2;
        }

        var need = parsed!.RequiresElevation ? ElevationNeed.SelfElevate : ElevationNeed.None;
        return RunWithElevation(arguments, need, () => RunVerb(parsed));
    }

    // The console entry point has no synchronization context, so blocking here cannot deadlock.
    int RunVerb(DirectArguments parsed)
    {
        try
        {
            RunVerbAsync(parsed, CancellationToken.None).GetAwaiter().GetResult();
            return 0;
        }
        catch (Exception exception)
        {
            _writeLine($"Error: {exception.Message}");
            return 1;
        }
    }

    /// <summary>The scan options a local source gets: freed records are kept only when the query will show them.</summary>
    internal static BrokerScanOptions ScanOptions(DirectArguments parsed) => new() { IncludeFreed = parsed.IncludeFreed };

    static MftIndexSource CreateSourceNative(DirectArguments parsed)
    {
        return parsed.Source is SourceKind.Dump
            ? MftIndexSources.FromMftDumpFile(parsed.DumpFile!, char.ToUpperInvariant(parsed.Drive[0]))
            : MftIndexSources.FromLocalVolumes(ScanOptions(parsed));
    }

    static IndexedDrive ResolveDriveNative(string letter)
    {
        return OperatingSystem.IsWindows()
            ? IndexedDrive.FromWindowsVolume(letter)
            : throw new PlatformNotSupportedException("Volume serials are read on Windows only.");
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
