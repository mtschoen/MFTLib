namespace MFTLib;

/// <summary>Native timing breakdown for one MFT parse operation.</summary>
internal readonly struct MftParseTimings
{
    /// <summary>Native disk I/O duration.</summary>
    public TimeSpan NativeIo { get; }
    /// <summary>Native update-sequence fixup duration.</summary>
    public TimeSpan NativeFixup { get; }
    /// <summary>Native record-parsing duration.</summary>
    public TimeSpan NativeParse { get; }
    /// <summary>Total native duration.</summary>
    public TimeSpan NativeTotal { get; }

    internal MftParseTimings(double ioMilliseconds, double fixupMilliseconds, double parseMilliseconds,
        double totalMilliseconds)
    {
        NativeIo = TimeSpan.FromMilliseconds(ioMilliseconds);
        NativeFixup = TimeSpan.FromMilliseconds(fixupMilliseconds);
        NativeParse = TimeSpan.FromMilliseconds(parseMilliseconds);
        NativeTotal = TimeSpan.FromMilliseconds(totalMilliseconds);
    }

    /// <summary>Formats the timing breakdown for diagnostics.</summary>
    /// <returns>A human-readable timing summary.</returns>
    public override string ToString()
    {
        return
            $"Native: {NativeTotal.TotalMilliseconds:F1}ms (IO: {NativeIo.TotalMilliseconds:F1}ms, Fixup: {NativeFixup.TotalMilliseconds:F1}ms, Parse: {NativeParse.TotalMilliseconds:F1}ms)";
    }
}
