using MFTLib;
using MFTLib.Index;

namespace SampleProgram.Watch;

partial class SampleHost
{
    readonly TextWriter _output;
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

    /// <summary>
    ///     Runs the command line, reports an escaping failure as one readable line, then holds an attended console window
    ///     open until a key press when <paramref name="ownsWindow" /> says this process alone owns it.
    /// </summary>
    internal int RunAndPause(string[] arguments, bool ownsWindow)
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

        PauseBeforeExit(ownsWindow);
        return exitCode;
    }

    internal int Run(string[] arguments) => Run(arguments, EnumerateDrives(), RunVerb);

    // Parse, choose drives, resolve the cache directory, pass the elevation gate, then hand the finished request to
    // runVerb. The machine's drive list is read only when no drive is named, and runVerb runs only past the gate.
    internal int Run(string[] arguments, IEnumerable<WindowsVolume> availableDrives, Func<WatchArguments, int> runVerb)
    {
        if (!WatchArguments.TryParse(arguments, out var parsed, out var error))
        {
            _output.WriteLine(error);
            _output.WriteLine(WatchArguments.Usage);
            return 2;
        }

        if (!TryChooseDrives(ref parsed, availableDrives))
        {
            return 1;
        }

        if (parsed.Verb is not (WatchVerb.Scan or WatchVerb.ElevationStatus))
        {
            try
            {
                if (parsed.CacheDirectory is null)
                {
                    var profileDirectory = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
                    parsed = parsed with { CacheDirectory = CacheDirectoryFromProfile(profileDirectory) };
                }
            }
            catch (ArgumentException exception)
            {
                _output.WriteLine(exception.Message);
                return 2;
            }
        }

        return RunWithElevation(arguments, parsed.Need, () => runVerb(parsed));
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
            session = CreateBrokerSessionNative(parsed.ConnectTimeoutSeconds);
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
