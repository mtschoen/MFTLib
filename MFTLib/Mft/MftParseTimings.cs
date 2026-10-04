namespace MFTLib;

/// <summary>Timing breakdown for one MFT parse operation.</summary>
public readonly struct MftParseTimings
{
    /// <summary>Number of MFT records processed.</summary>
    public ulong TotalRecords { get; }
    /// <summary>Native disk I/O duration in milliseconds.</summary>
    public double NativeIoMs { get; }
    /// <summary>Native update-sequence fixup duration in milliseconds.</summary>
    public double NativeFixupMs { get; }
    /// <summary>Native record-parsing duration in milliseconds.</summary>
    public double NativeParseMs { get; }
    /// <summary>Total native duration in milliseconds.</summary>
    public double NativeTotalMs { get; }
    /// <summary>Managed marshalling duration in milliseconds.</summary>
    public double MarshalMs { get; }

    internal MftParseTimings(ulong totalRecords, double ioMs, double fixupMs, double parseMs, double nativeTotalMs,
        double marshalMs)
    {
        TotalRecords = totalRecords;
        NativeIoMs = ioMs;
        NativeFixupMs = fixupMs;
        NativeParseMs = parseMs;
        NativeTotalMs = nativeTotalMs;
        MarshalMs = marshalMs;
    }

    /// <summary>Formats the timing breakdown for diagnostics.</summary>
    /// <returns>A human-readable timing summary.</returns>
    public override string ToString()
    {
        return
            $"Native: {NativeTotalMs:F1}ms (IO: {NativeIoMs:F1}ms, Fixup: {NativeFixupMs:F1}ms, Parse: {NativeParseMs:F1}ms), Marshal: {MarshalMs:F1}ms, Total records: {TotalRecords:N0}";
    }

    internal MftParseTimings WithMarshalMs(double marshalMs)
    {
        return new MftParseTimings(TotalRecords, NativeIoMs, NativeFixupMs, NativeParseMs, NativeTotalMs, marshalMs);
    }
}
