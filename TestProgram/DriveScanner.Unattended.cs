namespace TestProgram;

// The unattended switch: an agent working while nobody is at the desktop sets it once for the whole session, and
// TestProgram then neither shows the heads-up dialog nor requests elevation. It lives here, not in MFTLib, so a
// consumer's runtime flow gains no new behavior.
partial class DriveScanner
{
    internal const string UnattendedVariableName = "MFTLIB_TESTPROGRAM_UNATTENDED";

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
