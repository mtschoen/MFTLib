using System.Buffers;

namespace MFTLib.Index;

/// <summary>
///     A small set of name identifiers: four inline slots cover nearly every ignore-case exact lookup, and a rented
///     array takes the rest. Not thread-safe to mutate; read-only use after construction is safe.
/// </summary>
struct NameIdCandidates : IDisposable
{
    const int InlineSlots = 4;

    uint _first;
    uint _second;
    uint _third;
    uint _fourth;
    uint[]? _overflow;
    bool _sealed;

    public int Count { get; private set; }

    public void Add(uint nameId)
    {
        if (Count < InlineSlots)
        {
            switch (Count)
            {
                case 0: _first = nameId; break;
                case 1: _second = nameId; break;
                case 2: _third = nameId; break;
                default: _fourth = nameId; break;
            }

            Count++;
            return;
        }

        _sealed = false;
        var overflowCount = Count - InlineSlots;
        if (_overflow is null || overflowCount == _overflow.Length)
        {
            var grown = ArrayPool<uint>.Shared.Rent(Math.Max(16, overflowCount * 2));
            _overflow?.AsSpan(0, overflowCount).CopyTo(grown);
            ReturnOverflow();
            _overflow = grown;
        }

        _overflow[overflowCount] = nameId;
        Count++;
    }

    /// <summary>
    ///     Prepares membership for many lookups: sorts the overflow once so <see cref="Contains" /> is a binary search
    ///     rather than a scan per row. Call after collection; a later <see cref="Add" /> undoes it.
    /// </summary>
    public void Seal()
    {
        if (Count > InlineSlots && !_sealed && _overflow is { } overflow)
        {
            Array.Sort(overflow, 0, Count - InlineSlots);
        }

        _sealed = true;
    }

    /// <summary>The identifier at <paramref name="index" />, in insertion order for the inline slots.</summary>
    public readonly uint this[int index]
    {
        get
        {
            ArgumentOutOfRangeException.ThrowIfNegative(index);
            ArgumentOutOfRangeException.ThrowIfGreaterThanOrEqual(index, Count);
            return index switch
            {
                0 => _first,
                1 => _second,
                2 => _third,
                3 => _fourth,
                _ => _overflow![index - InlineSlots]
            };
        }
    }

    public readonly bool Contains(uint nameId)
    {
        var inline = Math.Min(Count, InlineSlots);
        if ((inline > 0 && _first == nameId) || (inline > 1 && _second == nameId) ||
            (inline > 2 && _third == nameId) || (inline > 3 && _fourth == nameId))
        {
            return true;
        }

        if (Count <= InlineSlots)
        {
            return false;
        }

        var overflow = _overflow.AsSpan(0, Count - InlineSlots);
        return _sealed ? overflow.BinarySearch(nameId) >= 0 : overflow.Contains(nameId);
    }

    public void Dispose()
    {
        ReturnOverflow();
        Count = 0;
        _sealed = false;
    }

    void ReturnOverflow()
    {
        if (_overflow is { } overflow)
        {
            ArrayPool<uint>.Shared.Return(overflow);
            _overflow = null;
        }
    }
}
