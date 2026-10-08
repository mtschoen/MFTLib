namespace MFTLib.Index;

/// <summary>Receives each matching row of <see cref="FileIndex.ForEachRow(SearchQuery, IIndexRowVisitor, CancellationToken)" />.</summary>
public interface IIndexRowVisitor
{
    /// <summary>Called once per matching row. The row and its name span are valid only until this call returns.</summary>
    /// <param name="row">The current row.</param>
    void Visit(in IndexRow row);
}
