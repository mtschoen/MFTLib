namespace MFTLib;

public readonly struct MftParseTimings
{
    public ulong TotalRecords { get; }
    public double NativeIoMs { get; }
    public double NativeFixupMs { get; }
    public double NativeParseMs { get; }
    public double NativeTotalMs { get; }
    public double MarshalMs { get; }

    internal MftParseTimings(ulong totalRecords, double ioMs, double fixupMs, double parseMs, double nativeTotalMs, double marshalMs)
    {
        TotalRecords = totalRecords;
        NativeIoMs = ioMs;
        NativeFixupMs = fixupMs;
        NativeParseMs = parseMs;
        NativeTotalMs = nativeTotalMs;
        MarshalMs = marshalMs;
    }

    public override string ToString() =>
        $"Native: {NativeTotalMs:F1}ms (IO: {NativeIoMs:F1}ms, Fixup: {NativeFixupMs:F1}ms, Parse: {NativeParseMs:F1}ms), Marshal: {MarshalMs:F1}ms, Total records: {TotalRecords:N0}";
}
