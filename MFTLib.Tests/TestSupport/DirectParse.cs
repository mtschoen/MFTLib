namespace MFTLib.Tests;

/// <summary>
///     Test-side array views of the two scan entry points. The library yields batches of records;
///     most tests want the records as values and sometimes the
///     count of records examined.
/// </summary>
static class DirectParse
{
    // Opens the dump, parses it at bufferSizeRecords per chunk and closes it again.
    internal static MftRecord[] ParseFile(string path, out ulong totalRecords,
        uint bufferSizeRecords = MftVolume.DefaultBufferSizeRecords)
    {
        using var input = MftDumpInput.Open(path);
        using var result = input.Parse(new MftFileScanOptions(BufferSizeRecords: bufferSizeRecords));
        totalRecords = result.TotalRecords;
        return result.ToRecords();
    }

    internal static MftRecord[] ReadAll(this MftVolume volume, bool includeFreed = false)
    {
        return [.. volume.ReadRecordBatches(4096, null, null, CancellationToken.None,
            new MftScanOptions { IncludeFreed = includeFreed }).SelectMany(batch => batch)];
    }

    /// <summary>Copies every record of the result, which stay valid after it is disposed.</summary>
    internal static MftRecord[] ToRecords(this MftResult result)
    {
        return [.. result.ReadBatches(4096).SelectMany(batch => batch)];
    }
}
