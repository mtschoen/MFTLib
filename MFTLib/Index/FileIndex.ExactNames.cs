namespace MFTLib.Index;

public sealed partial class FileIndex
{
    /// <summary>
    ///     One exact-name query per requested name over one borrow of the block: result <c>i</c> belongs to
    ///     <paramref name="names" />[i] and equals <see cref="Search" /> for that name with
    ///     <see cref="NameMatchMode.Exact" />, same rows in ascending row order.
    /// </summary>
    /// <param name="names">The names to find. Duplicates are answered independently.</param>
    /// <param name="filter">Every predicate except the name; null means no other predicate.</param>
    /// <param name="cancellationToken">Stops the query, as described on <see cref="Find" />.</param>
    /// <exception cref="ArgumentNullException"><paramref name="names" /> is null.</exception>
    /// <exception cref="ArgumentException">
    ///     An element of <paramref name="names" /> is null, <paramref name="filter" /> carries a name pattern, or
    ///     its <see cref="SearchQuery.Subtree" /> is the default <see cref="FileEntry" />.
    /// </exception>
    internal List<FileEntry>[] SearchExactNames(IReadOnlyList<string> names, SearchQuery? filter,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(names);
        for (var index = 0; index < names.Count; index++)
        {
            if (names[index] is null)
            {
                throw new ArgumentException($"The name at index {index} is null.", nameof(names));
            }
        }

        filter ??= new SearchQuery(null);
        if (filter.NamePattern is not null)
        {
            throw new ArgumentException(
                "The filter of an exact multi-name search carries no name pattern; the names are the pattern.",
                nameof(filter));
        }

        SubtreeAnchor.ThrowIfDefault(filter);
        cancellationToken.ThrowIfCancellationRequested();
        if (names.Count == 0)
        {
            return [];
        }

        using var scope = BeginQuery(cancellationToken, current => SubtreeAnchor.SelectBlock(current, filter));
        return SearchEngine.SearchExactNames(scope.RootBlock, names, filter, scope.ViewRow, scope.CancellationToken);
    }
}
