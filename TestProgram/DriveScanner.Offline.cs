using System.Diagnostics;
using MFTLib;

namespace TestProgram;

// The parse-file mode: a saved MFT image parsed without a volume, a journal or elevation, which is
// how forensics reads an image taken from another machine. Without --stream the records are copied
// into an array; with it the native result is kept and read back like stream-records.
partial class DriveScanner
{
    internal void ParseFile(ModeOptions options)
    {
        var path = options.RequiredFilePath;
        _writeLine($"=== File {path} ===");
        try
        {
            if (options.Stream)
            {
                StreamFile(path, options);
            }
            else
            {
                ParseFileIntoArray(path, options);
            }

            _writeLine($"=== File {path}: done ===");
        }
        catch (Exception exception)
        {
            _writeLine($"Error on file {path}: {exception.Message}");
        }

        _writeLine(string.Empty);
    }

    void ParseFileIntoArray(string path, ModeOptions options)
    {
        var stopwatch = Stopwatch.StartNew();
        var records = ParseFileOverload(path, options, out var timings);
        stopwatch.Stop();

        _writeLine($"Parsed {records.Length} records in {stopwatch.Elapsed}");
        _writeLine($"  {timings}");
        PrintRecords(records);
    }

    // Keeps the shortest overload that says what the options ask for.
    static MftRecord[] ParseFileOverload(string path, ModeOptions options, out MftParseTimings timings)
    {
        var matchFlags = options.ToMatchFlags();
        if (options.Name is null && matchFlags == MatchFlags.None && options.BufferSizeRecords is null)
        {
            return MftVolume.ParseMFTFromFile(path, out timings);
        }

        return options.BufferSizeRecords is { } bufferSizeRecords
            ? MftVolume.ParseMFTFromFile(path, options.Name, matchFlags, out timings, bufferSizeRecords)
            : MftVolume.ParseMFTFromFile(path, options.Name, matchFlags, out timings);
    }

    void StreamFile(string path, ModeOptions options)
    {
        var matchFlags = options.ToMatchFlags();
        MftRecord? retained;
        using (var result = options.BufferSizeRecords is { } bufferSizeRecords
                   ? MftVolume.StreamMFTFromFile(path, options.Name, matchFlags, bufferSizeRecords)
                   : MftVolume.StreamMFTFromFile(path, options.Name, matchFlags))
        {
            retained = ReportResult(result, options);
        }

        PrintRetained(retained);
    }
}
