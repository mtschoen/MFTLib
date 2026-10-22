namespace MFTLib.Tests.TestSupport;

sealed class TempDirectory : IDisposable
{
    public string Path { get; }

    public TempDirectory(string prefix = "mftlib-temp", bool create = true)
    {
        Path = System.IO.Path.Combine(System.IO.Path.GetTempPath(), $"{prefix}-{Guid.NewGuid():N}");
        if (create)
        {
            Directory.CreateDirectory(Path);
        }
    }

    public void Dispose()
    {
        for (var attempt = 0; attempt < 5; attempt++)
        {
            try
            {
                Directory.Delete(Path, recursive: true);
                return;
            }
            catch (DirectoryNotFoundException)
            {
                return;
            }
            catch (IOException) when (attempt < 4)
            {
                Thread.Sleep(25);
            }
            catch (IOException) when (OperatingSystem.IsWindows())
            {
                // A just-unmapped block file can stay locked briefly on Windows.
                return;
            }
        }
    }
}
