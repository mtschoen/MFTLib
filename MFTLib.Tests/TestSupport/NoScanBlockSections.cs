using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace MFTLib.Tests.TestSupport;

/// <summary>The block section factory of a broker launch whose test never scans.</summary>
static class NoScanBlockSections
{
    internal static readonly BrokerBlockSectionFactory Factory =
        (_, _) => throw new AssertFailedException("No scan runs in this test.");
}
