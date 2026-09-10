namespace MFTLib.Tests.Index;

static class TestVolumeSerial
{
    static int _nextVolumeSerial = unchecked((int)0x0BADF00D);

    public static uint GetNext()
    {
        return unchecked((uint)Interlocked.Increment(ref _nextVolumeSerial));
    }
}
