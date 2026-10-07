using MFTLib;
using MFTLib.Index;

namespace SampleProgram.Watch;

internal enum ProgramMode { ScanDrive, Watch, Rescan, Journal, Cache, ElevationStatus }

/// <summary>
///     The parsed command line: an optional mode name, then drive letters and the mode's flags. A first argument
///     that names no mode is a drive letter and selects <see cref="ProgramMode.ScanDrive" />.
/// </summary>
internal sealed partial record WatchArguments(ProgramMode Mode, IReadOnlyList<string> Drives)
{
    internal const string DefaultDrive = "G";
    internal const int DefaultSeconds = 10;

    internal static string Usage =>
        "Usage: SampleProgram.Watch [mode] [drive ...] [--keep-name A,B] [--profile full|directory-index]" + Environment.NewLine +
        "  modes: " + string.Join(", ", ProgramModes.Names.Keys) + " (default scan-drive); drive defaults to " + DefaultDrive + Environment.NewLine +
        "  watch [--seconds N]  journal [--maximum-size B --allocation-delta B]  cache [--cache-directory D] [--clear]";

    internal IReadOnlyList<string>? KeepNames { get; init; }
    internal BrokerScanProfile Profile { get; init; }
    internal int Seconds { get; init; } = DefaultSeconds;
    internal long? MaximumSize { get; init; }
    internal long? AllocationDelta { get; init; }
    internal string? CacheDirectory { get; init; }
    internal bool Clear { get; init; }

    // Modes that open an index launch the broker, which asks for elevation itself; cache and elevation-status touch no volume.
    internal ElevationNeed Need => Mode is ProgramMode.Cache or ProgramMode.ElevationStatus ? ElevationNeed.None : ElevationNeed.BrokerLaunch;

    /// <summary>The scan options the broker source gets; null scans every record, as the library does by default.</summary>
    internal BrokerScanOptions? ScanOptions =>
        KeepNames is null && Profile == BrokerScanProfile.Full ? null : new BrokerScanOptions { KeepFileNames = KeepNames, Profile = Profile };

    /// <summary>A cached block holds what its scan kept, so a different profile or keep list is a different identity.</summary>
    internal CacheTag CacheTag => new("SMPW", 1 + (uint)Profile + Fingerprint(KeepNames));

    static uint Fingerprint(IReadOnlyList<string>? names)
    {
        return names is null ? 0 : (uint)string.Join('\n', names).Aggregate(17, (hash, character) => hash * 31 + character);
    }
}

internal static class ProgramModes
{
    internal static readonly Dictionary<string, ProgramMode> Names = new(StringComparer.OrdinalIgnoreCase)
    {
        ["scan-drive"] = ProgramMode.ScanDrive,
        ["watch"] = ProgramMode.Watch,
        ["rescan"] = ProgramMode.Rescan,
        ["journal"] = ProgramMode.Journal,
        ["cache"] = ProgramMode.Cache,
        ["elevation-status"] = ProgramMode.ElevationStatus
    };
}
