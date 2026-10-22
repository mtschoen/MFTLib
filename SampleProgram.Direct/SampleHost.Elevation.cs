namespace SampleProgram.Direct;

internal enum ElevationNeed { None, SelfElevate }

partial class SampleHost
{
    const string SelfElevationReason = "SampleProgram relaunches itself elevated.";

    internal Func<IReadOnlyList<string>, TimeSpan, bool> _tryRunElevated = Elevation.TryRunElevated;

    // An elevated relaunch writes to output.log and its window closes at once; the unelevated parent holds the window.
    bool _outputRedirectedToFile;

    int RunWithOutputLog(Func<int> run)
    {
        var previous = _output;
        using var log = new StreamWriter(OutputLogPath, append: false);
        _output = TextWriter.Synchronized(log);
        _outputRedirectedToFile = true;
        try
        {
            return run();
        }
        catch (Exception exception)
        {
            WriteFailure("Error: ", exception);
            return 1;
        }
        finally
        {
            _output = previous;
        }
    }

    internal int RunWithElevation(string[] arguments, ElevationNeed need, Func<int> run)
    {
        if (need is ElevationNeed.None)
        {
            return run();
        }

        if (_isElevated())
        {
            return RunWithOutputLog(run);
        }

        if (IsUnattended())
        {
            return SkipElevationUnattended(arguments);
        }

        _output.WriteLine("Not running as administrator. Attempting to self-elevate...");
        if (_canSelfElevate() && ConfirmElevation(arguments, SelfElevationReason))
        {
            _output.WriteLine($"The elevated run writes its output to {OutputLogPath}");
            if (_tryRunElevated(arguments, Timeout.InfiniteTimeSpan))
            {
                return 0;
            }
            PrintElevatedRunIncomplete();
            return 1;
        }

        PrintElevationFailure(arguments);
        return 1;
    }

    void PrintElevatedRunIncomplete()
    {
        _output.WriteLine("------------------------------------------------------------------");
        _output.WriteLine("THE ELEVATED RUN DID NOT COMPLETE SUCCESSFULLY.");
        _output.WriteLine($"If the elevated run started, its output is in {OutputLogPath}");
        _output.WriteLine("------------------------------------------------------------------");
    }
}
