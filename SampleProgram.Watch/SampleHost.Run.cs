namespace SampleProgram.Watch;

partial class SampleHost
{
    // What a run needs from the process. The broker is the elevated process, so no mode self-elevates; the seam
    // keeps the self-elevation path testable.
    internal Func<WatchArguments, ElevationNeed> _elevationNeed = parsed => parsed.Need;

    internal int Run(string[] arguments)
    {
        if (!WatchArguments.TryParse(arguments, out var parsed, out var error))
        {
            _writeLine(error);
            _writeLine(WatchArguments.Usage);
            return 2;
        }

        if (parsed.Mode is not (ProgramMode.ScanDrive or ProgramMode.ElevationStatus))
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

        return RunWithElevation(arguments, _elevationNeed(parsed), () => RunMode(parsed));
    }

    int RunMode(WatchArguments parsed) => parsed.Mode switch
    {
        ProgramMode.Cache => RunCache(parsed),
        ProgramMode.ElevationStatus => WriteElevationStatus(),
        _ => RunBroker(parsed)
    };

    int RunBroker(WatchArguments parsed)
    {
        // The console entry point has no synchronization context, so blocking here cannot deadlock.
        if (!RunThroughBrokerAsync(parsed, CancellationToken.None).GetAwaiter().GetResult())
        {
            return 1;
        }

        _writeLine($"Completed at {DateTime.Now}");
        return 0;
    }
}
