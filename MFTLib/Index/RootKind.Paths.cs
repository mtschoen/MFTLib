namespace MFTLib.Index;

sealed partial class RootKind
{
    /// <summary>Copies immutable pool names directly into one result, with bounded caller-owned scratch.</summary>
    internal string JoinPath(BlockFile block, string root, ReadOnlySpan<uint> segments)
    {
        var isDump = _kind == Kind.Dump;
        var prefix = isDump ? root.AsSpan().TrimEnd('/') : root.AsSpan();
        var length = prefix.Length;
        var last = length == 0 ? '\0' : prefix[^1];
        var first = segments.Length - 1;
        for (var index = first; index >= 0; index--)
        {
            var name = NamePool.Read(block, segments[index]);
            if (name.IsEmpty)
            {
                continue;
            }
            if (!isDump && IsRooted(name))
            {
                prefix = default;
                length = 0;
                first = index;
            }
            if (NeedsSeparator(isDump, length, last))
            {
                length = checked(length + 1);
            }
            length = checked(length + name.Length);
            last = name[name.Length - 1];
        }

        return string.Create(length, new PathWriteState(block, prefix, segments[..(first + 1)], isDump),
            static (destination, state) =>
            {
                state.Prefix.CopyTo(destination);
                var written = state.Prefix.Length;
                for (var index = state.Segments.Length - 1; index >= 0; index--)
                {
                    var name = NamePool.Read(state.Block, state.Segments[index]);
                    if (name.IsEmpty)
                    {
                        continue;
                    }
                    if (NeedsSeparator(state.IsDump, written, written == 0 ? '\0' : destination[written - 1]))
                    {
                        destination[written++] = state.IsDump ? '/' : Path.DirectorySeparatorChar;
                    }
                    name.CopyTo(destination[written..]);
                    written += name.Length;
                }
            });
    }

    static bool NeedsSeparator(bool isDump, int length, char last) =>
        length != 0 && (isDump || last != Path.DirectorySeparatorChar && last != Path.AltDirectorySeparatorChar);

    static bool IsRooted(NameView name)
    {
        // Host rootedness depends only on the first two characters, including drive-relative roots.
        ReadOnlySpan<char> prefix = [name[0], name.Length > 1 ? name[1] : '\0'];
        return Path.IsPathRooted(prefix[..Math.Min(2, name.Length)]);
    }

    readonly ref struct PathWriteState(BlockFile block, ReadOnlySpan<char> prefix, ReadOnlySpan<uint> segments, bool isDump)
    {
        internal BlockFile Block { get; } = block;
        internal ReadOnlySpan<char> Prefix { get; } = prefix;
        internal ReadOnlySpan<uint> Segments { get; } = segments;
        internal bool IsDump { get; } = isDump;
    }
}
