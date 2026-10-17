using MFTLib;

#if SAMPLE_WATCH
namespace SampleProgram.Watch;
#else
namespace SampleProgram.Direct;
#endif

// The elevation members both samples share: the provider, state probes, and the manual-elevation failure notice.
// Each sample compiles this file as linked source.
partial class SampleHost
{
    // The provider is the library's injectable face of the same three elevation calls.
    static readonly IElevationProvider Elevation = ElevationUtilities.DefaultProvider;

    internal Func<bool> _canSelfElevate = Elevation.CanSelfElevate;
    internal Func<string?> _getProcessPath = () => Environment.ProcessPath;
    internal Func<bool> _isElevated = Elevation.IsElevated;
    internal Action<string> _writeLine = Console.WriteLine;

    /// <summary>
    ///     Prints the elevation failure notice. The arguments are listed as data, one per line and verbatim, never
    ///     as a command line: no quoting rule is correct for every shell, so the user re-enters them in their own.
    ///     An empty argument is shown as <c>&lt;empty&gt;</c>.
    /// </summary>
    void PrintElevationFailure(string[] arguments)
    {
        _writeLine("------------------------------------------------------------------");
        _writeLine("AUTOMATIC ELEVATION FAILED.");
        _writeLine("This program requires Administrative privileges to read the MFT.");
        _writeLine($"Run this program from an ELEVATED terminal: {_getProcessPath()}");
        _writeLine($"Arguments ({arguments.Length}), one per line exactly as received; <empty> marks an empty argument:");
        foreach (var line in ArgumentLines(arguments))
        {
            _writeLine(line);
        }

        _writeLine("------------------------------------------------------------------");
    }
}
