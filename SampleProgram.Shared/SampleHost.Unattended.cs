#if SAMPLE_WATCH
namespace SampleProgram.Watch;
#else
namespace SampleProgram.Direct;
#endif

// The unattended switch: an agent working while nobody is at the desktop sets it once for the whole session, and
// the sample then neither shows the heads-up dialog nor requests elevation. It lives here, not in MFTLib, so a
// consumer's runtime flow gains no new behavior.
partial class SampleHost
{
    internal const string UnattendedVariableName = "MFTLIB_SAMPLE_UNATTENDED";

    internal Func<string, string?> _getEnvironmentVariable = Environment.GetEnvironmentVariable;

    bool IsUnattended()
    {
        return _getEnvironmentVariable(UnattendedVariableName) == "1";
    }

    int SkipElevationUnattended(string[] arguments)
    {
        _writeLine($"Running unattended ({UnattendedVariableName}=1): elevation skipped, no prompt shown.");
        PrintElevationFailure(arguments);
        return 1;
    }
}
