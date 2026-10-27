using System.Buffers.Binary;
using System.IO.MemoryMappedFiles;
using System.Text;

namespace MFTLib.Index;

sealed unsafe partial class BlockFile
{
    /// <summary>Byte offset of the root key's UTF-16 code-unit count, immediately after the declared header fields.</summary>
    internal const int RootKeyOffset = BlockLayout.HeaderFieldBytes;

    /// <summary>
    ///     The root this block indexes, as stored in page 0. Default for a block opened over a client-created
    ///     broker section, which is structurally validated and never named by the cache.
    /// </summary>
    internal RootKey Root { get; }

    /// <summary>Stores <paramref name="root" /> after the header: a ushort code-unit count, then the code units.</summary>
    void WriteRootKey(RootKey root)
    {
        var text = root.Validated().ToString();
        var destination = _base + RootKeyOffset;
        *(ushort*)destination = (ushort)text.Length;
        text.AsSpan().CopyTo(new Span<char>(destination + sizeof(ushort), text.Length));
    }

    /// <summary>
    ///     Reads and validates the stored key text from a copy of page 0: the count must fit the page and the text must
    ///     parse back to a key whose own text is exactly the stored text.
    /// </summary>
    static bool TryParseRootKey(ReadOnlySpan<byte> page, out RootKey root)
    {
        root = default;
        if (page.Length < RootKeyOffset + sizeof(ushort))
        {
            return false;
        }

        var count = (int)BinaryPrimitives.ReadUInt16LittleEndian(page[RootKeyOffset..]);
        var textStart = RootKeyOffset + sizeof(ushort);
        if (count == 0 || count > RootKey.MaximumTextUnits || textStart + (count * sizeof(char)) > page.Length)
        {
            return false;
        }

        var text = Encoding.Unicode.GetString(page.Slice(textStart, count * sizeof(char)));
        if (!RootKey.TryParse(text, out var parsed) || !string.Equals(parsed.ToString(), text, StringComparison.Ordinal))
        {
            return false;
        }

        root = parsed;
        return true;
    }

    static bool TryReadRootKey(MemoryMappedViewAccessor view, out RootKey root)
    {
        var page = new byte[BlockLayout.HeaderRegionBytes];
        view.ReadArray(0, page, 0, page.Length);
        return TryParseRootKey(page, out root);
    }

    /// <summary>
    ///     Reads the stored root key of the block file at <paramref name="path" /> without mapping it or taking its
    ///     owner lock. The key text is written once when the block is created and never changes, so a concurrent
    ///     writer cannot make this observe a half-updated key; a file still being created reads as unreadable.
    /// </summary>
    internal static bool TryReadStoredRootKey(string path, out RootKey root)
    {
        root = default;
        try
        {
            using var stream = new FileStream(path, FileMode.Open, FileAccess.Read,
                FileShare.ReadWrite | FileShare.Delete, bufferSize: 1);
            var page = new byte[BlockLayout.HeaderRegionBytes];
            return stream.ReadAtLeast(page, page.Length, throwOnEndOfStream: false) == page.Length &&
                   TryParseRootKey(page, out root);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            return false;
        }
    }

    /// <summary>
    ///     Opens a block at its canonical cache path: everything <see cref="Open" /> checks, plus that the file name is
    ///     exactly the name the stored key and the header's volume serial derive.
    /// </summary>
    internal static BlockFile? OpenCanonical(string path, uint expectedVolumeSerial,
        out BlockValidationResult? validation)
    {
        var block = Open(path, expectedVolumeSerial, out validation);
        if (block is null)
        {
            return null;
        }

        var expectedName = CacheDirectory.BlockFileName(block.Root, block.Header.VolumeSerial);
        if (string.Equals(System.IO.Path.GetFileName(path), expectedName, StringComparison.Ordinal))
        {
            return block;
        }

        block.Dispose();
        validation = BlockValidationResult.WrongRootDirectory;
        return null;
    }
}
