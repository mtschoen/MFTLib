using MFTLib.Index;

namespace Benchmark;

static partial class NameAccessorSpike
{
    static int Outstanding(FileIndex index)
    {
        using var borrow = index.BorrowCurrentSnapshot();
        return borrow.Snapshot.ReleaseState.OutstandingBorrowCount - 1; // minus this probe's own borrow
    }

    public static async Task<int> LifetimeAsync(string directory)
    {
        var candidate = CacheDirectory.InspectCached(directory).Single();
        await using var index = await FileIndex.OpenAsync(new FileIndexOptions
        {
            CacheDirectory = directory,
            InitialOpenCacheOnly = true,
            CacheTag = candidate.CacheTag!.Value,
            Drives = [new IndexedDrive(candidate.File.DriveLetter, candidate.RootDirectory!, candidate.File.VolumeSerial)]
        }, CancellationToken.None);
        var query = new SearchQuery(null, Directories: false);
        Console.WriteLine($"baseline outstanding borrows: {Outstanding(index)}");

        var rows = index.EnumerateRows(query);
        Console.WriteLine($"after EnumerateRows (before foreach): {Outstanding(index)}");
        foreach (var row in rows) { if (row.Name.IsEmpty) continue; }
        Console.WriteLine($"after full foreach: {Outstanding(index)}");

        foreach (var row in index.EnumerateRows(query)) { _ = row.Name.Length; break; }
        Console.WriteLine($"after break: {Outstanding(index)}");

        try { foreach (var row in index.EnumerateRows(query)) { throw new InvalidOperationException("boom"); } }
        catch (InvalidOperationException) { }
        Console.WriteLine($"after exception in body: {Outstanding(index)}");

        using var cancellation = new CancellationTokenSource();
        try
        {
            foreach (var row in index.EnumerateRows(query, cancellation.Token)) { cancellation.Cancel(); }
        }
        catch (OperationCanceledException) { Console.WriteLine("cancelled mid-scan: OperationCanceledException thrown"); }
        Console.WriteLine($"after cancellation: {Outstanding(index)}");

        var abandoned = index.EnumerateRows(query).GetEnumerator();
        abandoned.MoveNext();
        Console.WriteLine($"abandoned enumerator (no Dispose): {Outstanding(index)}");
        abandoned.Dispose();
        Console.WriteLine($"after late manual Dispose: {Outstanding(index)}");
        return 0;
    }
}
