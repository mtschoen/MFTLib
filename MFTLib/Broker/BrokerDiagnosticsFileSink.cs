using System.Text;

namespace MFTLib;

/// <summary>Owns a newly created broker log for one diagnostics drain.</summary>
sealed class BrokerDiagnosticsFileSink(string path) : IDisposable
{
    StreamWriter? _writer;

    public void AppendLine(string line)
    {
        _writer ??= OpenWriter();
        _writer.WriteLine(line);
    }

    StreamWriter OpenWriter()
    {
        try
        {
            if ((File.GetAttributes(path) & FileAttributes.ReparsePoint) != 0)
            {
                throw new IOException("The broker diagnostics path is a reparse point.");
            }
        }
        catch (FileNotFoundException)
        {
            // Absence is expected. CreateNew below, not this observation, arbitrates ownership.
        }

        var stream = new FileStream(path, FileMode.CreateNew, FileAccess.Write, FileShare.Read);
        return new StreamWriter(stream, new UTF8Encoding(false)) { AutoFlush = true };
    }

    public void Dispose() => _writer?.Dispose();
}
