namespace MFTLib.Tests;

/// <summary>
///     Test-side array views of the two streaming entry points. The library returns native
///     results; most tests want the records as values and sometimes the native timings and the
///     count of records examined.
/// </summary>
internal static class DirectParse
{
    internal static MftRecord[] ParseFile(string path, out MftParseTimings timings,
        uint bufferSizeRecords = MftVolume.DefaultBufferSizeRecords)
    {
        return ParseFile(path, out timings, out _, bufferSizeRecords);
    }

    // Opens the dump, parses it at bufferSizeRecords per chunk and closes it again.
    internal static MftRecord[] ParseFile(string path, out MftParseTimings timings, out ulong totalRecords,
        uint bufferSizeRecords = MftVolume.DefaultBufferSizeRecords)
    {
        using var input = MftDumpInput.Open(path);
        using var result = input.Parse(new MftFileScanOptions(BufferSizeRecords: bufferSizeRecords));
        timings = result.Timings;
        totalRecords = result.TotalRecords;
        return result.ToArray();
    }

    internal static MftRecord[] ReadAll(this MftVolume volume, bool includeFreed = false)
    {
        return ReadAll(volume, includeFreed, out _, out _);
    }

    internal static MftRecord[] ReadAll(this MftVolume volume, out MftParseTimings timings, out ulong totalRecords)
    {
        return ReadAll(volume, false, out timings, out totalRecords);
    }

    internal static MftRecord[] ReadAll(this MftVolume volume, bool includeFreed, out MftParseTimings timings,
        out ulong totalRecords)
    {
        using var result = volume.StreamRecords(includeFreed, null, null, CancellationToken.None);
        timings = result.Timings;
        totalRecords = result.TotalRecords;
        return result.ToArray();
    }
}
