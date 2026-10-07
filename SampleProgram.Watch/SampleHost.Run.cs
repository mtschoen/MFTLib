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

        return RunWithElevation(arguments, _elevationNeed(parsed), () => RunMode(parsed));
    }

    int RunMode(WatchArguments parsed)
    {
        switch (parsed.Mode)
        {
            case ProgramMode.Cache:
                return RunCache(parsed);
            case ProgramMode.ElevationStatus:
                return WriteElevationStatus();
            default:
                // The console entry point has no synchronization context, so blocking here cannot deadlock.
                RunThroughBrokerAsync(parsed, CancellationToken.None).GetAwaiter().GetResult();
                _writeLine($"Completed at {DateTime.Now}");
                return 0;
        }
    }
}
