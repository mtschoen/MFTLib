using System.Security.Cryptography;
using System.Text;
using MFTLib;
using MFTLib.Index;

namespace SampleProgram.Watch;

internal enum ProgramMode { ScanDrive, Watch, Rescan, Journal, Cache, ElevationStatus }

/// <summary>
///     The parsed command line: an optional mode name, then drive letters and the mode's flags. A first argument
///     that names no mode is a drive letter and selects <see cref="ProgramMode.Watch" />. An index mode that names
///     no drive leaves <see cref="Drives" /> empty for the host to fill from the machine.
/// </summary>
internal sealed partial record WatchArguments(ProgramMode Mode, IReadOnlyList<string> Drives)
{
    /// <summary>How long an unattended watch runs when <c>--seconds</c> is not given; an attended one runs until a key press.</summary>
    internal const int DefaultSeconds = 10;

    // Task.Delay takes at most int.MaxValue milliseconds.
    internal const int MaximumSeconds = int.MaxValue / 1000;

    internal static string Usage =>
        "Usage: SampleProgram.Watch [mode] [drive ...] [--directories-only | --keep-name A,B]" + Environment.NewLine +
        "  modes: " + string.Join(", ", ProgramModes.Names.Keys) + " (default watch); with no drive, every ready NTFS fixed drive" + Environment.NewLine +
        "  watch [--seconds N]  (without --seconds an attended watch runs until a key press, an unattended one for " + DefaultSeconds + " seconds)" + Environment.NewLine +
        "  journal [--maximum-size B --allocation-delta B]  cache [--cache-directory D] [--clear]  (each scan policy caches under D/policy-DIGEST)";

    // The flags each mode reads; any other flag on the command line is a usage error rather than silently ignored.
    static readonly Dictionary<ProgramMode, string[]> ModeFlags = new()
    {
        [ProgramMode.ScanDrive] = ["--keep-name", "--directories-only", "--cache-directory"],
        [ProgramMode.Watch] = ["--keep-name", "--directories-only", "--cache-directory", "--seconds"],
        [ProgramMode.Rescan] = ["--keep-name", "--directories-only", "--cache-directory"],
        [ProgramMode.Journal] = ["--keep-name", "--directories-only", "--cache-directory", "--maximum-size", "--allocation-delta"],
        [ProgramMode.Cache] = ["--cache-directory", "--clear"],
        [ProgramMode.ElevationStatus] = []
    };

    internal IReadOnlyList<string>? DirectoryScanFileNames { get; init; }
    /// <summary>The <c>--seconds</c> a watch runs for; null waits for a key press when attended.</summary>
    internal int? Seconds { get; init; }
    internal long? MaximumSize { get; init; }
    internal long? AllocationDelta { get; init; }
    internal string? CacheDirectory { get; init; }
    internal bool Clear { get; init; }

    /// <summary>Whether the mode opens an index, so it needs the drives it works on.</summary>
    internal bool NeedsDrives => Mode is not (ProgramMode.Cache or ProgramMode.ElevationStatus);

    // Modes that open an index launch the broker, which asks for elevation itself; cache and elevation-status touch no volume.
    internal ElevationNeed Need => Mode is ProgramMode.Cache or ProgramMode.ElevationStatus ? ElevationNeed.None : ElevationNeed.BrokerLaunch;

    /// <summary>The scan options the broker source gets; null scans every record, as the library does by default.</summary>
    internal BrokerScanOptions? ScanOptions =>
        DirectoryScanFileNames is null ? null : new BrokerScanOptions { DirectoryScanFileNames = DirectoryScanFileNames };

    /// <summary>A cached block holds what its scan retained, so a different retention policy is a different identity.</summary>
    internal static CacheTag CacheTag => new("SMPW", 2);

    internal const string PolicyDirectoryPrefix = "policy-";

    /// <summary>
    ///     The folder under the cache directory that holds this scan policy's blocks. The name is the full SHA-256 of the
    ///     full/directory marker and sorted retained names (joined by NUL, which no file name contains), so two policies never share a block
    ///     file; the 32-bit cache tag cannot carry that identity.
    /// </summary>
    internal string PolicyDirectoryName
    {
        get
        {
            var marker = DirectoryScanFileNames is null ? "full" : "directories";
            var canonical = $"{marker}\0{string.Join('\0', (DirectoryScanFileNames ?? []).Order(StringComparer.Ordinal))}";
            return PolicyDirectoryPrefix + Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(canonical)));
        }
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
