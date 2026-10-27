using MFTLib.Index;

namespace MFTLib.Tests.TestSupport;

/// <summary>
///     Tree fixture seeded with documents and pictures for query parity and row-view tests.
/// </summary>
public sealed class TwoRootQueryFixture : IDisposable
{
    readonly IndexTreeFixture _fixture = new(seedDocuments: false);
    FileIndex? _first;
    FileIndex? _second;

    public string FirstRoot => Path.Combine(_fixture.TreeRoot, "first");

    public string SecondRoot => Path.Combine(_fixture.TreeRoot, "second");

    public async Task<FileIndex> OpenAsync()
    {
        await SeedRootAsync();
        return await FileIndex.OpenAsync(_fixture.Options() with
        {
            Root = MFTLibTestExtensions.SyntheticIndexedRoot.Create(new VolumeKey('T'), FirstRoot, 1, ProducerKind.Enumeration)
        }, CancellationToken.None);
    }

    public async Task<FileIndex> OpenSecondAsync()
    {
        await SeedRootAsync();
        return await FileIndex.OpenAsync(_fixture.Options() with
        {
            Root = MFTLibTestExtensions.SyntheticIndexedRoot.Create(new VolumeKey('U'), SecondRoot, 2, ProducerKind.Enumeration)
        }, CancellationToken.None);
    }

    public async Task<FileIndexSet> OpenSetAsync()
    {
        _first = await OpenAsync();
        _second = await OpenSecondAsync();
        return new FileIndexSet([_first, _second]);
    }

    async Task SeedRootAsync()
    {
        Directory.CreateDirectory(Path.Combine(FirstRoot, "Documents"));
        Directory.CreateDirectory(Path.Combine(FirstRoot, "Pictures"));
        Directory.CreateDirectory(Path.Combine(SecondRoot, "Pictures"));
        await File.WriteAllTextAsync(Path.Combine(FirstRoot, "Documents", "readme.md"), "hello");
        await File.WriteAllTextAsync(Path.Combine(FirstRoot, "Documents", "report.pdf"), new string('x', 100));
        await File.WriteAllTextAsync(Path.Combine(FirstRoot, "Pictures", "holiday.jpg"), new string('y', 5000));
        await File.WriteAllTextAsync(Path.Combine(FirstRoot, "Pictures", "readme.md"), "world");
        await File.WriteAllTextAsync(Path.Combine(SecondRoot, "notes.md"), new string('z', 300));
        await File.WriteAllTextAsync(Path.Combine(SecondRoot, "Pictures", "readme.md"), "world");
    }

    public void Dispose()
    {
        _first?.DisposeAsync().AsTask().GetAwaiter().GetResult();
        _second?.DisposeAsync().AsTask().GetAwaiter().GetResult();
        _fixture.Dispose();
    }
}
