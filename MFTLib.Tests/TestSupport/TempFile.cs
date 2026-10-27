namespace MFTLib.Tests.TestSupport;

sealed class TempFile : IDisposable
{
    public string Path { get; } = System.IO.Path.GetTempFileName();

    public void Dispose()
    {
        File.Delete(Path);
    }
}
