using MFTLib;

namespace SampleProgram.Watch;

partial class SampleHost
{
    readonly bool? _interactive;

    internal SampleHost(TextWriter output, TextReader input, TimeProvider? timeProvider = null, bool? interactive = null)
    {
        ArgumentNullException.ThrowIfNull(output);
        ArgumentNullException.ThrowIfNull(input);
        _output = TextWriter.Synchronized(output);
        _input = input;
        _timeProvider = timeProvider ?? TimeProvider.System;
        _interactive = interactive;
    }

    /// <summary>Runs the command line, reports an escaping failure as one readable line, then holds an attended, self-owned console window open until a key press.</summary>
    internal int RunAndPause(string[] arguments)
    {
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

        PauseBeforeExit(OwnsConsoleWindowNative());
        return exitCode;
    }

    internal int Run(string[] arguments)
    {
        if (!WatchArguments.TryParse(arguments, out var parsed, out var error))
        {
            _output.WriteLine(error);
            _output.WriteLine(WatchArguments.Usage);
            return 2;
        }

        if (!TryChooseDrives(ref parsed, EnumerateDrives()))
        {
            return 1;
        }

        if (parsed.Verb is not (WatchVerb.Scan or WatchVerb.ElevationStatus))
        {
            try
            {
                var profileDirectory = parsed.CacheDirectory is null
                    ? Environment.GetFolderPath(Environment.SpecialFolder.UserProfile)
                    : null;
                parsed = parsed with { CacheDirectory = ResolveCacheDirectory(parsed, profileDirectory) };
            }
            catch (ArgumentException exception)
            {
                _output.WriteLine(exception.Message);
                return 2;
            }
        }

        return RunWithElevation(arguments, parsed.Need, () => RunVerb(parsed));
    }

    int RunVerb(WatchArguments parsed) => parsed.Verb switch
    {
        WatchVerb.Cache => RunCache(parsed),
        WatchVerb.ElevationStatus => WriteElevationStatus(),
        _ => RunBroker(parsed)
    };

    int RunBroker(WatchArguments parsed)
    {
        BrokerSession session;
        try
        {
            session = CreateBrokerSessionNative();
        }
        catch (Exception exception)
        {
            _output.WriteLine($"Error creating broker session: {exception.Message}");
            return 1;
        }

        // The console entry point has no synchronization context, so blocking here cannot deadlock.
        var exitCode = RunThroughBrokerAsync(parsed, session, ResolveDriveNative, CancellationToken.None).GetAwaiter().GetResult();
        if (exitCode == 0)
        {
            _output.WriteLine($"Completed at {_timeProvider.GetUtcNow():u}");
        }

        return exitCode;
    }
}
