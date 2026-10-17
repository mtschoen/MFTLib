using MFTLib.Tests.TestSupport;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace MFTLib.Tests;

/// <summary>Native MFT processing of synthetic images, including geometry and read failures.</summary>
[TestClass]
[DoNotParallelize]
public partial class NativeMftProcessingTests
{
    [TestCleanup]
    public void Cleanup()
    {
        NativeTestHooks.NativeResetTestState();
    }
}
