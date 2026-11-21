using MFTLib.Index;

namespace MFTLib;

/// <summary>
///     The positions a chunk of inline content requests has yet to see answered: one bit per position over the
///     chunk's position range, so answering costs no hash and the chunk is never copied. Positions are distinct.
/// </summary>
sealed class OutstandingPositions
{
    readonly ulong[] _outstanding;
    readonly int _lowest;
    readonly int _span;

    public OutstandingPositions(ReadOnlySpan<InlineContentRequest> requests)
    {
        Remaining = requests.Length;
        if (requests.IsEmpty)
        {
            _outstanding = [];
            return;
        }

        var lowest = requests[0].Position;
        var highest = lowest;
        foreach (var request in requests)
        {
            lowest = Math.Min(lowest, request.Position);
            highest = Math.Max(highest, request.Position);
        }

        _lowest = lowest;
        _span = highest - lowest + 1;
        _outstanding = new ulong[(_span + 63) / 64];
        foreach (var request in requests)
        {
            var offset = request.Position - lowest;
            _outstanding[offset >> 6] |= 1UL << (offset & 63);
        }
    }

    /// <summary>How many requested positions have not been answered.</summary>
    public int Remaining { get; private set; }

    /// <summary>Marks a position answered; false when it was not requested or was already answered.</summary>
    public bool TryAnswer(int position)
    {
        var offset = (long)position - _lowest;
        if (offset < 0 || offset >= _span)
        {
            return false;
        }

        var word = (int)(offset >> 6);
        var bit = 1UL << (int)(offset & 63);
        if ((_outstanding[word] & bit) == 0)
        {
            return false;
        }

        _outstanding[word] &= ~bit;
        Remaining--;
        return true;
    }
}
