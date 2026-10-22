namespace MFTLib.Index;

readonly record struct EnumerationDirectoryWork(string Path, uint RowIndex);

readonly record struct EnumerationBufferedEntry(
    int NameOffset, int NameLength, RowColumns Columns, bool CanDescend);

// The result owns its List and ArrayBufferWriter; neither escapes before Read returns.
sealed class EnumerationDirectoryBuffer(EnumerationDirectoryWork directory)
{
    internal EnumerationDirectoryWork Directory { get; } = directory;
    internal List<EnumerationBufferedEntry> Entries { get; } = [];
    internal System.Buffers.ArrayBufferWriter<char> Names { get; } = new();
    internal bool SubtreeSkipped { get; set; }

    internal ReadOnlySpan<char> Name(in EnumerationBufferedEntry entry) =>
        Names.WrittenSpan.Slice(entry.NameOffset, entry.NameLength);
}
