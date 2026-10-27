using MFTLib;
using MFTLib.Index;

namespace SampleProgram.Direct;

// The Direct sample reads an MFT in this process, with no broker and no watch: the live volume of an elevated run
// or a saved $MFT file. It never caches, so every verb scans.
partial class SampleHost
{
    TextWriter _output;

    internal SampleHost(TextWriter output, TextReader input, string outputDirectory, TimeProvider? timeProvider = null)
    {
        ArgumentNullException.ThrowIfNull(output);
        ArgumentNullException.ThrowIfNull(input);
        _output = TextWriter.Synchronized(output);
        _input = input;
        _timeProvider = timeProvider ?? TimeProvider.System;
        OutputLogPath = Path.Combine(Path.GetFullPath(outputDirectory), "output.log");
    }

    internal string OutputLogPath { get; }

    /// <summary>
    ///     Runs the command line, reports an escaping failure as one readable line, then holds an attended console window
    ///     open until a key press when <paramref name="ownsWindow" /> says this process alone owns it.
    /// </summary>
    internal int RunAndPause(string[] arguments, bool ownsWindow)
    {
        _outputRedirectedToFile = false;
        int exitCode;
        try
        {
            exitCode = Run(arguments);
        }
        catch (Exception exception)
        {
            WriteFailure("Error: ", exception);
            exitCode = 1;
        }

        if (!_outputRedirectedToFile)
        {
            PauseBeforeExit(ownsWindow);
        }

        return exitCode;
    }

    internal int Run(string[] arguments)
    {
        if (IsExcludeDefender(arguments))
        {
            return RunExcludeDefender(arguments);
        }

        if (!DirectArguments.TryParse(arguments, out var parsed, out var error))
        {
            _output.WriteLine(error);
            _output.WriteLine(DirectArguments.Usage);
            return 2;
        }

        var need = parsed.RequiresElevation && OperatingSystem.IsWindows() ? ElevationNeed.SelfElevate : ElevationNeed.None;
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

    /// <summary>The scan options a local source gets: freed records are kept only when the query will show them.</summary>
    internal static MftScanOptions ScanOptions(DirectArguments parsed) => new() { IncludeFreed = parsed.IncludeFreed };

    static MftIndexSource CreateSourceNative(DirectArguments parsed)
    {
        return parsed.Source is SourceKind.Dump
            ? MftIndexSources.FromMftDumpFile(parsed.DumpFile ?? throw new ArgumentException("A dump source needs a dump file."), char.ToUpperInvariant(parsed.Drive[0]))
            : MftIndexSources.FromLocalVolumes(ScanOptions(parsed));
    }

    async Task<FileIndex> OpenIndexAsync(MftIndexSource source, IndexedRoot? drive, CancellationToken cancellationToken)
    {
        var options = new FileIndexOptions
        {
            MftSource = source,
            ScanProgress = new PhaseReporter(_output)
        };
        if (drive is not null)
        {
            await _output.WriteLineAsync($"Indexing {drive.Drive}: root {drive.RootDirectory}, serial {drive.VolumeSerial}, producer {drive.Producer}.").ConfigureAwait(false);
            options = options with
            {
                Root = drive,
                CacheMode = CacheMode.Disabled
            };
        }

        var index = await FileIndex.OpenAsync(options, cancellationToken).ConfigureAwait(false);
        try
        {
            RequireReadyBlock(index.Status);
            return index;
        }
        catch
        {
            await index.DisposeAsync().ConfigureAwait(false);
            throw;
        }
    }
}
