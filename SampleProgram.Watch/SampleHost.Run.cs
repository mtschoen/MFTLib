namespace SampleProgram.Watch;

partial class SampleHost
{
    // Whether a run must be elevated itself. Scanning through the broker never must, because the broker is the
    // elevated process; the seam keeps the self-elevation path testable.
    internal Func<WatchArguments, bool> _requiresElevation = parsed => parsed.RequiresElevation;

    internal int Run(string[] arguments)
    {
        if (!WatchArguments.TryParse(arguments, out var parsed, out var error))
        {
            _writeLine(error);
            _writeLine(WatchArguments.Usage);
            return 2;
        }

        var need = _requiresElevation(parsed) ? ElevationNeed.SelfElevate : ElevationNeed.BrokerLaunch;
        return RunWithElevation(arguments, need, () => RunOnDrives(parsed));
    }

    void RunOnDrives(WatchArguments parsed)
    {
        // The console entry point has no synchronization context, so blocking here cannot deadlock.
        ScanDrivesThroughBrokerAsync(parsed.Drives, CancellationToken.None).GetAwaiter().GetResult();
        _writeLine($"Completed at {DateTime.Now}");
    }
}
