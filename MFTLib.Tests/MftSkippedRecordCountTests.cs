using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace MFTLib.Tests;

[TestClass]
public class MftSkippedRecordCountTests
{
    [DataTestMethod]
    [DataRow(0L, 0)]
    [DataRow(17L, 17)]
    [DataRow((long)int.MaxValue, int.MaxValue)]
    [DataRow((long)int.MaxValue + 1, int.MaxValue)]
    [DataRow(long.MaxValue, int.MaxValue)]
    public void DumpAndLiveScanDiagnostic_SaturatesSkippedRecordCount(long count, int expected)
    {
        Assert.AreEqual(expected, MftBlockScan.SaturateSkippedRecordCount(count));
    }
}
