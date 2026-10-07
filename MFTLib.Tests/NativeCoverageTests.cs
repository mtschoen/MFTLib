using MFTLib.Tests.TestSupport;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace MFTLib.Tests;

/// <summary>
///     Tests targeting native C++ code paths of the volume export that are otherwise uncovered:
///     extension records, record geometry, read failures and error branches. An image file handle
///     stands in for the volume, so these run only on Windows.
/// </summary>
[TestClass]
[DoNotParallelize]
public partial class NativeCoverageTests
{
    [TestCleanup]
    public void Cleanup()
    {
        NativeTestHooks.NativeResetTestState();
    }
}
