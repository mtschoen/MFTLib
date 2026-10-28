using System.Runtime.InteropServices;
using System.Text;

namespace MFTLib.Index;

/// <summary>
///     A borrowed immutable name whose bytes are ASCII or little-endian UTF-16. Ignore-case matching goes through
///     <c>NamePattern</c> or <see cref="NameFolding" />; this type compares ordinally only.
/// </summary>
readonly ref struct NameView
{
    readonly ReadOnlySpan<byte> _bytes;

    internal NameView(ReadOnlySpan<byte> bytes, bool isUtf16)
    {
        _bytes = bytes;
        IsUtf16 = isUtf16;
    }

    public bool IsUtf16 { get; }
    public int Length => IsUtf16 ? _bytes.Length / sizeof(char) : _bytes.Length;
    public bool IsEmpty => _bytes.IsEmpty;

    public ReadOnlySpan<byte> AsciiBytes
    {
        get
        {
            if (IsUtf16)
            {
                throw new InvalidOperationException("The name is UTF-16.");
            }

            return _bytes;
        }
    }

    public ReadOnlySpan<char> Utf16Characters
    {
        get
        {
            if (!IsUtf16)
            {
                throw new InvalidOperationException("The name is ASCII.");
            }

            return MemoryMarshal.Cast<byte, char>(_bytes);
        }
    }

    internal char this[int index] => IsUtf16 ? Utf16Characters[index] : (char)_bytes[index];

    /// <summary>Whether the name's characters equal <paramref name="name" /> exactly.</summary>
    public bool EqualsOrdinal(ReadOnlySpan<char> name)
    {
        if (name.Length != Length)
        {
            return false;
        }

        return IsUtf16 ? Utf16Characters.SequenceEqual(name) : Ascii.Equals(_bytes, name);
    }

    public void CopyTo(Span<char> destination)
    {
        if (destination.Length < Length)
        {
            throw new ArgumentException("The destination is shorter than the name.", nameof(destination));
        }

        if (IsUtf16)
        {
            Utf16Characters.CopyTo(destination);
            return;
        }

        Ascii.ToUtf16(_bytes, destination, out _);
    }

    public override string ToString() => IsUtf16 ? new string(Utf16Characters) : Encoding.ASCII.GetString(_bytes);
}
