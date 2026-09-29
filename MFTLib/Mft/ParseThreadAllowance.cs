namespace MFTLib;

/// <summary>
///     The number of threads an MFT parse may use. The parser reads it at the start of every chunk
///     and again before path resolution, so a change made while a parse runs takes effect at the next
///     of those points; a chunk already in progress finishes with the count it started with. The
///     parser never uses more threads than the machine has processors.
/// </summary>
public sealed class ParseThreadAllowance
{
    readonly Lock _gate = new();
    int _count;
    unsafe int* _attachedField;

    public ParseThreadAllowance(int count)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(count, 1);
        _count = count;
    }

    /// <summary>
    ///     The thread count, at least 1. While a parse runs, a write goes through to that parse's
    ///     control block.
    /// </summary>
    public int Count
    {
        get
        {
            lock (_gate)
            {
                return _count;
            }
        }
        set
        {
            ArgumentOutOfRangeException.ThrowIfLessThan(value, 1);
            lock (_gate)
            {
                _count = value;
                WriteThroughLocked();
            }
        }
    }

    // Binds this allowance to the parseThreadAllowance field of a control block the caller keeps
    // in place until Detach, and copies the current count into it.
    internal unsafe void Attach(int* allowanceField)
    {
        lock (_gate)
        {
            if (_attachedField != null)
            {
                throw new InvalidOperationException("The parse thread allowance is already attached to a running parse.");
            }

            _attachedField = allowanceField;
            WriteThroughLocked();
        }
    }

    internal unsafe void Detach()
    {
        lock (_gate)
        {
            _attachedField = null;
        }
    }

    unsafe void WriteThroughLocked()
    {
        if (_attachedField != null)
        {
            Volatile.Write(ref *_attachedField, _count);
        }
    }
}
