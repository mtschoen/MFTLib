using Microsoft.Win32.SafeHandles;

namespace MFTLib.Tests.TestSupport;

/// <summary>A non-owning handle for tests whose volume I/O is served by test seams or native hooks.</summary>
internal static class FakeVolumeHandle
{
    internal static SafeFileHandle Create() => new(new IntPtr(1), false);
}
