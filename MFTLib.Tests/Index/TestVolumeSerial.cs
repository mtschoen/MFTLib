namespace MFTLib.Tests.Index;

static class TestVolumeSerial
{
    // The upper half is nonzero so every test serial needs all 64 bits.
    static long _nextVolumeSerial = 0x5EED_0000_0BAD_F00D;

    public static ulong GetNext()
    {
        return unchecked((ulong)Interlocked.Increment(ref _nextVolumeSerial));
    }
}
