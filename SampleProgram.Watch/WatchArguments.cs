namespace SampleProgram.Watch;

/// <summary>What one run does to each drive.</summary>
internal enum ProgramMode
{
    /// <summary>Opens each drive as a NoCache FileIndex over the elevated broker and reports how it settled.</summary>
    ScanDrive
}

/// <summary>
///     The parsed command line: an optional mode name, then drive letters. A first argument that names no
///     mode is a drive letter and selects <see cref="ProgramMode.ScanDrive" />.
/// </summary>
internal sealed partial record WatchArguments(ProgramMode Mode, IReadOnlyList<string> Drives)
{
    internal const string DefaultDrive = "G";

    internal static string Usage =>
        "Usage: SampleProgram.Watch [mode] [drive ...]" + Environment.NewLine +
        "  modes: " + string.Join(", ", ProgramModes.Names.Keys) + " (default scan-drive)" + Environment.NewLine +
        $"  drive defaults to {DefaultDrive}";

    /// <summary>
    ///     Scanning through the broker runs unelevated because the broker is the elevated process and
    ///     asks for elevation itself.
    /// </summary>
    internal bool RequiresElevation => Mode is not ProgramMode.ScanDrive;
}

internal static class ProgramModes
{
    internal static readonly Dictionary<string, ProgramMode> Names = new(StringComparer.OrdinalIgnoreCase)
    {
        ["scan-drive"] = ProgramMode.ScanDrive
    };
}
