namespace SampleProgram.Watch;

partial class SampleHost
{
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

        if (!IsUnattended() && _ownsConsoleWindow())
        {
            _writeLine(PausePrompt);
            _readKey();
        }

        return exitCode;
    }

    internal int Run(string[] arguments)
    {
        if (!WatchArguments.TryParse(arguments, out var parsed, out var error))
        {
            _writeLine(error);
            _writeLine(WatchArguments.Usage);
            return 2;
        }

        if (!TryChooseDrives(ref parsed))
        {
            return 1;
        }

        if (parsed.Verb is not (WatchVerb.Scan or WatchVerb.ElevationStatus))
        {
            try
            {
                parsed = parsed with { CacheDirectory = ResolveCacheDirectory(parsed) };
            }
            catch (ArgumentException exception)
            {
                _writeLine(exception.Message);
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
        // The console entry point has no synchronization context, so blocking here cannot deadlock.
        var exitCode = RunThroughBrokerAsync(parsed, CancellationToken.None).GetAwaiter().GetResult();
        if (exitCode == 0)
        {
            _writeLine($"Completed at {_timeProvider.GetUtcNow():u}");
        }

        return exitCode;
    }
}
