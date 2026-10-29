using MFTLib.Index;

namespace MFTLibTestExtensions;

/// <summary>Production forwarding for tests of packed name storage.</summary>
internal static class SyntheticNamePool
{
    internal const int MaximumNameLengthUnits = NamePool.MaximumNameLengthUnits;

    internal static bool TryGetOrAdd(BlockFile block, ReadOnlySpan<char> name, out uint nameId) =>
        NamePool.TryGetOrAdd(block, name, out nameId);

    internal static bool TryFindOrdinal(BlockFile block, ReadOnlySpan<char> name, out uint nameId) =>
        NamePool.TryFindOrdinal(block, name, out nameId);

    internal static NameView Read(BlockFile block, uint nameId) => NamePool.Read(block, nameId);

    internal static NameView ReadRowName(BlockFile block, uint rowIndex) => NamePool.ReadRowName(block, rowIndex);

    internal static string? CheckIntegrity(BlockFile block) => NamePoolIntegrity.Check(block);

    internal static string? CheckPostingIntegrity(BlockFile block) => PostingIntegrity.Check(block);

    internal static ulong Hash(ReadOnlySpan<char> name) => NameHash.Compute(name);

    /// <summary>Streams the rows a query matches through the borrowed row enumerator, with each row's name.</summary>
    internal static (uint Row, string Name)[] EnumerateRows(RootBlock rootBlock, SearchQuery query,
        CancellationToken cancellationToken)
    {
        var rows = new List<(uint Row, string Name)>();
        using var borrow = new SnapshotBorrow(rootBlock);
        using var scope = new FileIndex.QueryScope(borrow, cancellationToken);
        using var enumerator = new IndexRowEnumerator(scope, query);
        while (enumerator.MoveNext())
        {
            var row = enumerator.Current;
            rows.Add((row.ToEntry().RowIndex, row.Name.ToString()));
        }

        return [.. rows];
    }
}
