using MFTLib.Index;

namespace MFTLib.Tests.TestSupport;

/// <summary>
///     Two-drive tree fixture seeded with documents and pictures for query parity and row-view tests.
/// </summary>
public sealed class TwoDriveQueryFixture : IDisposable
{
    readonly IndexTreeFixture _fixture = new(seedDocuments: false);

    public string FirstRoot => Path.Combine(_fixture.TreeRoot, "first");

    public string SecondRoot => Path.Combine(_fixture.TreeRoot, "second");

    public async Task<FileIndex> OpenAsync()
    {
        Directory.CreateDirectory(Path.Combine(FirstRoot, "Documents"));
        Directory.CreateDirectory(Path.Combine(FirstRoot, "Pictures"));
        Directory.CreateDirectory(Path.Combine(SecondRoot, "Pictures"));
        await File.WriteAllTextAsync(Path.Combine(FirstRoot, "Documents", "readme.md"), "hello");
        await File.WriteAllTextAsync(Path.Combine(FirstRoot, "Documents", "report.pdf"), new string('x', 100));
        await File.WriteAllTextAsync(Path.Combine(FirstRoot, "Pictures", "holiday.jpg"), new string('y', 5000));
        await File.WriteAllTextAsync(Path.Combine(SecondRoot, "notes.md"), new string('z', 300));
        await File.WriteAllTextAsync(Path.Combine(SecondRoot, "Pictures", "readme.md"), "world");

        return await FileIndex.OpenAsync(_fixture.Options() with
        {
            Drives =
            [
                new IndexedDrive('T', FirstRoot, 1, ProducerKind.Enumeration),
                new IndexedDrive('U', SecondRoot, 2, ProducerKind.Enumeration)
            ]
        }, CancellationToken.None);
    }

    public void Dispose() => _fixture.Dispose();
}
