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
        using var result = StreamFileResult(path, options);
        var records = result.ToArray();
        stopwatch.Stop();

        _writeLine($"Parsed {records.Length} records in {stopwatch.Elapsed}");
        _writeLine($"  {result.Timings}");
        PrintRecords(records);
    }

    static MftResult StreamFileResult(string path, ModeOptions options)
    {
        return MftVolume.StreamMftFromFile(
            path, options.Name, options.ToMatchFlags(),
            new MftFileScanOptions(BufferSizeRecords: options.BufferSizeRecords ?? MftVolume.DefaultBufferSizeRecords));
    }

    void StreamFile(string path, ModeOptions options)
    {
        MftRecord? retained;
        using (var result = StreamFileResult(path, options))
        {
            retained = ReportResult(result, options);
        }

        PrintRetained(retained);
    }
}
