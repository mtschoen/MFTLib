namespace MFTLib.Tests;

/// <summary>
///     Test-side array views of the two streaming entry points. The library returns native
///     results; most tests want the records as values and sometimes the native timings and the
///     count of records examined.
/// </summary>
internal static class DirectParse
{
    internal static MftRecord[] ParseFile(string path, out MftParseTimings timings)
    {
        return ParseFile(path, null, MatchFlags.None, out timings);
    }

    internal static MftRecord[] ParseFile(string path, out MftParseTimings timings, out ulong totalRecords)
    {
        return ParseFile(path, null, MatchFlags.None, out timings, out totalRecords);
    }

    internal static MftRecord[] ParseFile(string path, string? filter, MatchFlags matchFlags,
        out MftParseTimings timings, uint bufferSizeRecords = MftVolume.DefaultBufferSizeRecords)
    {
        return ParseFile(path, filter, matchFlags, out timings, out _, bufferSizeRecords);
    }

    internal static MftRecord[] ParseFile(string path, string? filter, MatchFlags matchFlags,
        out MftParseTimings timings, out ulong totalRecords,
        uint bufferSizeRecords = MftVolume.DefaultBufferSizeRecords)
    {
        using var result = MftVolume.StreamMftFromFile(
            path, filter, matchFlags, new(BufferSizeRecords: bufferSizeRecords));
        timings = result.Timings;
        totalRecords = result.TotalRecords;
        return result.ToArray();
    }

    internal static MftRecord[] ReadAll(this MftVolume volume, bool resolvePaths = false)
    {
        return ReadAll(volume, resolvePaths, out _, out _);
    }

    internal static MftRecord[] ReadAll(this MftVolume volume, out MftParseTimings timings, out ulong totalRecords)
    {
        return ReadAll(volume, false, out timings, out totalRecords);
    }

    internal static MftRecord[] ReadAll(this MftVolume volume, bool resolvePaths, out MftParseTimings timings,
        out ulong totalRecords)
    {
        return FindName(volume, null, resolvePaths ? MatchFlags.ResolvePaths : MatchFlags.None, out timings,
            out totalRecords);
    }

    internal static MftRecord[] FindName(this MftVolume volume, string name,
        MatchFlags matchFlags = MatchFlags.ExactMatch)
    {
        return FindName(volume, name, matchFlags, out _, out _);
    }

    internal static MftRecord[] FindName(this MftVolume volume, string? name, MatchFlags matchFlags,
        out MftParseTimings timings, out ulong totalRecords)
    {
        using var result = volume.StreamRecords(name, matchFlags, null, null, CancellationToken.None);
        timings = result.Timings;
        totalRecords = result.TotalRecords;
        return result.ToArray();
    }
}
