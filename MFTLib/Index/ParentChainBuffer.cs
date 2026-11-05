namespace MFTLib.Index;

/// <summary>
///     The values a parent walk collects, in walk order. A walk starts in caller-provided stack storage of
///     <see cref="StackCapacity" /> values, which holds every real tree but the rare deep one; past it the values move
///     to a heap array that doubles as it fills. Membership is a linear scan while the chain fits the stack storage and
///     a hash set beyond it, so a walk up to <see cref="BlockLayout.MaximumPathDepth" /> hops stays linear. Only a
///     deep walk allocates, and its storage is collected with it, so the buffer needs no disposal.
/// </summary>
ref struct ParentChainBuffer
{
    // Values a caller stackallocs for the fast path; also the length up to which membership scans linearly.
    internal const int StackCapacity = 128;

    Span<uint> _values;
    HashSet<uint>? _members;
    int _count;

    internal ParentChainBuffer(Span<uint> stackStorage)
    {
        _values = stackStorage;
    }

    internal readonly int Count => _count;

    internal readonly ReadOnlySpan<uint> Values => _values[.._count];

    internal void Add(uint value)
    {
        if (_count == _values.Length)
        {
            var larger = new uint[Math.Max(_values.Length * 2, StackCapacity)];
            _values[.._count].CopyTo(larger);
            _values = larger;
        }

        _values[_count++] = value;
        _members?.Add(value);
    }

    /// <summary>Whether the walk already collected <paramref name="value" />.</summary>
    internal bool Contains(uint value)
    {
        if (_count <= StackCapacity)
        {
            return _values[.._count].Contains(value);
        }

        _members ??= [.. _values[.._count]];
        return _members.Contains(value);
    }
}
