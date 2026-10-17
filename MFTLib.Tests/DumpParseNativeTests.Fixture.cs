using System.Runtime.InteropServices;
using System.Security.Cryptography;
using MFTLib.NativeInterop;
using MFTLib.Tests.TestSupport;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace MFTLib.Tests;

// The native generators' output and the rows a dump parse makes of the fixture are pinned byte for
// byte, so a parser or generator change that moves either shows up here.
public partial class DumpParseNativeTests
{
    [DataTestMethod]
    [DataRow("fixture.mft", DisplayName = "Issue655_AsciiFixture")]
    [DataRow("fixture-\u00e9-\u6d4b\u8bd5.mft", DisplayName = "Issue655_Utf8Fixture")]
    [TestCategory("Smoke")]
    public void Parse_FixtureRowsAndStrings_AreByteIdentical(string fileName)
    {
        var path = Path.Combine(_directory, fileName);
        MftFixture.Generate(path);

        var pointer = ParseNative(path, 256);
        Assert.AreNotEqual(IntPtr.Zero, pointer);
        try
        {
            var result = Marshal.PtrToStructure<MftParseResult>(pointer);
            var entryBytes = checked((int)(result.UsedRecords * result.EntryStride));
            var stringBytes = checked((int)result.EntryStringUnits * 2);
            var data = new byte[entryBytes + stringBytes];
            Marshal.Copy(result.Entries, data, 0, entryBytes);
            Marshal.Copy(result.EntryStrings, data, entryBytes, stringBytes);
            Assert.AreEqual("127f37afbc6b8c338f51aacaca1714029a90ebde340830f9b544267d44574906",
                Convert.ToHexStringLower(SHA256.HashData(data)));
        }
        finally
        {
            MFTLibNative.Hooks.FreeMftResult(pointer);
        }
    }

    [TestMethod]
    [TestCategory("Smoke")]
    public void GenerateSyntheticMFT_Output_IsByteIdentical()
    {
        var path = Path.Combine(_directory, "synthetic.mft");
        MftVolume.GenerateSyntheticMFT(path, 1024, 256);

        Assert.AreEqual("716b83ae4e2fa5b3e67ff07cd948edc97839a7736e95ee3a2d54ae36c8426fbe",
            Convert.ToHexStringLower(SHA256.HashData(File.ReadAllBytes(path))));
    }
}
