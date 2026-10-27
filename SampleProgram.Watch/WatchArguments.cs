using MFTLib.Index;

namespace SampleProgram.Watch;

enum WatchVerb { Scan, Watch, Rescan, Journal, Cache, ElevationStatus }

/// <summary>
///     The parsed command line: an optional verb name, then drive letters and the verb's flags. A first argument
///     that names no verb is a drive letter and selects <see cref="WatchVerb.Watch" />. An index verb that names
///     no drive leaves <see cref="Drives" /> empty for the host to fill from the machine.
/// </summary>
sealed partial record WatchArguments(WatchVerb Verb, IReadOnlyList<string> Drives)
{
    /// <summary>How long an unattended watch runs when <c>--seconds</c> is not given; an attended one runs until a key press.</summary>
    internal const int DefaultSeconds = 10;

    // Task.Delay takes at most int.MaxValue milliseconds, and the connect timeout is held to the same bound.
    const int MaximumSeconds = int.MaxValue / 1000;

    internal static string Usage =>
        "Usage: SampleProgram.Watch [verb] [drive ...] [--directories-only | --keep-name A,B] [--connect-timeout N]" + Environment.NewLine +
        "  verbs: " + string.Join(", ", WatchVerbs.Names.Keys) + " (default watch); with no drive, every ready NTFS fixed drive" + Environment.NewLine +
        "  watch [--seconds N] [--cache-directory D]  (without --seconds an attended watch runs until a key press, an unattended elevated one for " + DefaultSeconds + " seconds; an unattended run that is not elevated exits 1 without watching)" + Environment.NewLine +
        "  rescan [--cache-directory D]  journal [--cache-directory D] [--maximum-size B --allocation-delta B]  cache [--cache-directory D] [--clear]" + Environment.NewLine +
        "  cache exits 0 when listing; --clear attempts every selected block and exits 1 if any is InUse or Failed, otherwise 0" + Environment.NewLine +
        "  --connect-timeout N waits N seconds (default 30) for the elevated broker to connect; one cache directory D serves every drive and scan shape; scan never caches and refuses --cache-directory";

    // The flags each verb reads; any other flag on the command line is a usage error rather than silently ignored.
    static readonly Dictionary<WatchVerb, string[]> VerbFlags = new()
    {
        [WatchVerb.Scan] = ["--keep-name", "--directories-only", "--connect-timeout"],
        [WatchVerb.Watch] = ["--keep-name", "--directories-only", "--cache-directory", "--seconds", "--connect-timeout"],
        [WatchVerb.Rescan] = ["--keep-name", "--directories-only", "--cache-directory", "--connect-timeout"],
        [WatchVerb.Journal] = ["--keep-name", "--directories-only", "--cache-directory", "--maximum-size", "--allocation-delta", "--connect-timeout"],
        [WatchVerb.Cache] = ["--cache-directory", "--clear"],
        [WatchVerb.ElevationStatus] = []
    };

    internal IReadOnlyList<string>? RetainedFileNames { get; private init; }
    /// <summary>The <c>--seconds</c> a watch runs for; null waits for a key press when attended.</summary>
    internal int? Seconds { get; init; }
    /// <summary>The <c>--connect-timeout</c> in seconds the broker session waits for the elevated broker; null waits the library default.</summary>
    internal int? ConnectTimeoutSeconds { get; init; }
    internal long? MaximumSize { get; init; }
    internal long? AllocationDelta { get; init; }
    internal string? CacheDirectory { get; init; }
    internal bool Clear { get; private init; }

    /// <summary>Whether the verb opens an index, so it needs the drives it works on.</summary>
    internal bool NeedsDrives => Verb is not (WatchVerb.Cache or WatchVerb.ElevationStatus);

    // Verbs that open an index launch the broker, which asks for elevation itself; cache and elevation-status touch no volume.
    internal ElevationNeed Need => Verb is WatchVerb.Cache or WatchVerb.ElevationStatus ? ElevationNeed.None : ElevationNeed.BrokerLaunch;

    /// <summary>The consumer tag; the library compares the retention scan shape itself and rebuilds a block whose shape differs.</summary>
    internal static CacheTag CacheTag => new("SMPW", 2);
}

static class WatchVerbs
{
    internal static readonly Dictionary<string, WatchVerb> Names = new(StringComparer.OrdinalIgnoreCase)
    {
        ["scan"] = WatchVerb.Scan,
        ["watch"] = WatchVerb.Watch,
        ["rescan"] = WatchVerb.Rescan,
        ["journal"] = WatchVerb.Journal,
        ["cache"] = WatchVerb.Cache,
        ["elevation-status"] = WatchVerb.ElevationStatus
    };
}
