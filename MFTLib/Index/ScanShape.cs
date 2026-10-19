using System.Buffers;
using System.Buffers.Binary;
using System.Security.Cryptography;
using System.Text;

namespace MFTLib.Index;

/// <summary>
///     The 64-bit identity of the options that shaped a block's rows, stored at header offset 112 and compared
///     on warm start after the cache tag. It is the first eight bytes, read little-endian, of SHA-256 over a
///     canonical encoding that depends on the producer; <c>docs/index-format.md</c> specifies both forms.
///     Every integer is little-endian, and every string is its UTF-8 byte count as a u32 followed by the bytes.
/// </summary>
internal static class ScanShape
{
    /// <summary>The shape a drive's block must carry under the given admitted options.</summary>
    /// <param name="drive">The drive's configuration, which names its producer and root.</param>
    /// <param name="options">The admitted index options.</param>
    /// <returns>The expected scan shape.</returns>
    public static ulong For(IndexedDrive drive, FileIndexOptions options) =>
        drive.Producer == ProducerKind.Enumeration
            ? ForEnumeration(options.RetainedFileNames, options.DirectoryWalk, drive.RootDirectory)
            : ForMft(options.RetainedFileNames, options.MftSource?.IncludeFreed ?? false);

    /// <summary>The shape of an MFT block: retention, then freed-record inclusion.</summary>
    /// <param name="retainedFileNames">The retention set; null retains every row.</param>
    /// <param name="includeFreed">Whether freed records were written.</param>
    /// <returns>The scan shape.</returns>
    public static ulong ForMft(IReadOnlyCollection<string>? retainedFileNames, bool includeFreed)
    {
        var buffer = new ArrayBufferWriter<byte>();
        WriteUInt32(buffer, (uint)ProducerKind.Mft);
        WriteRetention(buffer, retainedFileNames);
        WriteFlag(buffer, includeFreed);
        return Digest(buffer);
    }

    /// <summary>
    ///     The shape of an enumeration block: retention, then the walk options, keeping only the pruned paths
    ///     that can match under <paramref name="rootDirectory" />.
    /// </summary>
    /// <param name="retainedFileNames">The retention set; null retains every row.</param>
    /// <param name="walk">The admitted walk options.</param>
    /// <param name="rootDirectory">The drive's root directory.</param>
    /// <returns>The scan shape.</returns>
    public static ulong ForEnumeration(IReadOnlyCollection<string>? retainedFileNames, DirectoryWalkOptions walk,
        string rootDirectory)
    {
        var buffer = new ArrayBufferWriter<byte>();
        WriteUInt32(buffer, (uint)ProducerKind.Enumeration);
        WriteRetention(buffer, retainedFileNames);
        WriteStrings(buffer, walk.PrunedDirectoryNames.Select(FoldForDrive));
        WriteUInt32(buffer, (uint)walk.PrunedDirectoryAttributes);
        WriteFlag(buffer, walk.PruneDotDirectories);
        WriteStrings(buffer, walk.PathsWithin(rootDirectory).Select(FoldForDrive));
        return Digest(buffer);
    }

    // Retention matches ordinally ignoring case on every producer, so upper-invariant is its canonical form.
    static void WriteRetention(ArrayBufferWriter<byte> buffer, IReadOnlyCollection<string>? retainedFileNames)
    {
        WriteFlag(buffer, retainedFileNames is not null);
        if (retainedFileNames is not null)
        {
            WriteStrings(buffer, retainedFileNames.Select(name => name.ToUpperInvariant()));
        }
    }

    static string FoldForDrive(string value) => OperatingSystem.IsWindows() ? value.ToUpperInvariant() : value;

    static void WriteStrings(ArrayBufferWriter<byte> buffer, IEnumerable<string> values)
    {
        var canonical = values.Distinct(StringComparer.Ordinal).Order(StringComparer.Ordinal).ToArray();
        WriteUInt32(buffer, (uint)canonical.Length);
        foreach (var value in canonical)
        {
            var bytes = Encoding.UTF8.GetBytes(value);
            WriteUInt32(buffer, (uint)bytes.Length);
            buffer.Write(bytes);
        }
    }

    static void WriteUInt32(ArrayBufferWriter<byte> buffer, uint value)
    {
        BinaryPrimitives.WriteUInt32LittleEndian(buffer.GetSpan(sizeof(uint)), value);
        buffer.Advance(sizeof(uint));
    }

    static void WriteFlag(ArrayBufferWriter<byte> buffer, bool value)
    {
        buffer.GetSpan(1)[0] = value ? (byte)1 : (byte)0;
        buffer.Advance(1);
    }

    static ulong Digest(ArrayBufferWriter<byte> buffer) =>
        BinaryPrimitives.ReadUInt64LittleEndian(SHA256.HashData(buffer.WrittenSpan));
}
