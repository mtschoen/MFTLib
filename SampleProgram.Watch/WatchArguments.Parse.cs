using System.Diagnostics.CodeAnalysis;
using MFTLib;

namespace SampleProgram.Watch;

// The command-line parser: the mode name, then the drives and the mode's flags.
internal sealed partial record WatchArguments
{
    internal static bool TryParse(string[] arguments, out WatchArguments parsed,
        [NotNullWhen(false)] out string? error)
    {
        var mode = ProgramMode.ScanDrive;
        var start = 0;
        if (arguments.Length > 0 && ProgramModes.Names.TryGetValue(arguments[0], out var named))
        {
            mode = named;
            start = 1;
        }

        var reader = new ArgumentReader(arguments.Skip(start));
        var keep = reader.Text("--keep-name");
        var profile = reader.Text("--profile");
        var seconds = reader.Number("--seconds");
        var candidate = new WatchArguments(mode, [])
        {
            KeepNames = keep?.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries),
            Profile = profile?.ToLowerInvariant() switch { "directory-index" => BrokerScanProfile.DirectoryIndex, _ => BrokerScanProfile.Full },
            Seconds = seconds is >= 1 and <= MaximumSeconds ? (int)seconds : DefaultSeconds,
            MaximumSize = reader.Number("--maximum-size"),
            AllocationDelta = reader.Number("--allocation-delta"),
            CacheDirectory = reader.Text("--cache-directory"),
            Clear = reader.Flag("--clear")
        };
        var drives = reader.Positionals();
        error = reader.Error ?? Validate(candidate, profile, seconds) ?? NotApplicable(candidate.Mode, reader.Supplied);
        if (error is not null)
        {
            parsed = new WatchArguments(ProgramMode.ScanDrive, [DefaultDrive]);
            return false;
        }

        var listsDrives = mode is ProgramMode.Cache or ProgramMode.ElevationStatus;
        parsed = candidate with { Drives = drives.Count == 0 && !listsDrives ? [DefaultDrive] : drives };
        return true;
    }

    // The first flag on the command line that the mode never reads, named with the mode as the user knows it.
    static string? NotApplicable(ProgramMode mode, IEnumerable<string> supplied)
    {
        var stray = supplied.FirstOrDefault(name => !ModeFlags[mode].Contains(name));
        return stray is null ? null : $"Option {stray} does not apply to {ProgramModes.Names.First(pair => pair.Value == mode).Key}.";
    }

    static string? Validate(WatchArguments candidate, string? profile, long? seconds)
    {
        return candidate switch
        {
            _ when profile is not null && !profile.Equals("full", StringComparison.OrdinalIgnoreCase)
                && !profile.Equals("directory-index", StringComparison.OrdinalIgnoreCase) => $"Unknown profile {profile}.",
            _ when seconds is < 1 or > MaximumSeconds => $"Option --seconds must be from 1 to {MaximumSeconds}.",
            { Mode: ProgramMode.Journal } when candidate.MaximumSize.HasValue != candidate.AllocationDelta.HasValue
                => "journal needs --maximum-size and --allocation-delta together.",
            _ => null
        };
    }
}
