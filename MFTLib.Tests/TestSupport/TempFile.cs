namespace MFTLib.Tests.TestSupport;

sealed class TempFile : IDisposable
{
    readonly TempDirectory _directory;

    public string Path { get; }

    public TempFile(string prefix = "mftlib-file", bool create = true)
    {
        _directory = new TempDirectory(prefix);
        try
        {
            Path = System.IO.Path.Combine(_directory.Path,
                System.IO.Path.GetFileName(_directory.Path) + ".tmp");
            if (create)
            {
                using var stream = File.Create(Path);
            }
        }
        catch
        {
            _directory.Dispose();
            throw;
        }
    }

    public void Dispose() => _directory.Dispose();
}
