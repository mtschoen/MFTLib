using System.Runtime.InteropServices;
using System.Security.Cryptography;
using MFTLib.Interop;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace MFTLib.Tests;

public partial class NativeParserCoverageTests
{
    [TestMethod]
    [DataRow(3)]
    [DataRow(4)]
    [DataRow(5)]
    public void IncludeFreed_PathMetadataAllocationFailure_ReturnsErrorAndCanParseAgain(int allocation)
    {
        var path = Path.GetTempFileName();
        try
        {
            MftVolume.GenerateFixtureMFT(path);
            MFTLibNative.NativeSetAllocFailCountdown(allocation);
            var pointer = MFTLibNative._parseMftFromFile(path, null,
                MatchFlags.IncludeFreed | MatchFlags.ResolvePaths, 256);
            Assert.AreNotEqual(IntPtr.Zero, pointer);
            try
            {
                var result = Marshal.PtrToStructure<MftParseResult>(pointer);
                StringAssert.Contains(result.ErrorMessage, "Failed to allocate path lookup");
                Assert.AreEqual(0ul, result.UsedRecords);
                Assert.AreEqual(IntPtr.Zero, result.PathEntries);
            }
            finally
            {
                MFTLibNative._freeMftResult(pointer);
            }

            MFTLibNative.NativeResetTestState();
            var records = MftVolume.ParseMFTFromFile(path, null,
                MatchFlags.IncludeFreed | MatchFlags.ResolvePaths, out _);
            Assert.AreEqual(16, records.Length);
        }
        finally
        {
            File.Delete(path);
        }
    }

    [TestMethod]
    [DataRow(MatchFlags.None, "3c7fcbd3d39ca2ae78a8b79379da1d1bb3cc0a51c563826cd2060d1a1ad4d22f")]
    [DataRow(MatchFlags.ResolvePaths, "351f03a11f9c3f36e9db0f508242bfcdb7b8e99aa8c0614debbe6fe4c168e9d1")]
    public void IncludeFreed_DefaultFixtureRowsAndStrings_AreByteIdentical(MatchFlags flags, string expectedHash)
    {
        var path = Path.GetTempFileName();
        try
        {
            MftVolume.GenerateFixtureMFT(path);
            var pointer = MFTLibNative._parseMftFromFile(path, null, flags, 256);
            Assert.AreNotEqual(IntPtr.Zero, pointer);
            try
            {
                var result = Marshal.PtrToStructure<MftParseResult>(pointer);
                var resolvePaths = flags.HasFlag(MatchFlags.ResolvePaths);
                var entryBytes = checked((int)(result.UsedRecords * result.EntryStride));
                var stringBytes = checked((int)(resolvePaths ? result.PathStringUnits : result.EntryStringUnits) * 2);
                var data = new byte[entryBytes + stringBytes];
                Marshal.Copy(resolvePaths ? result.PathEntries : result.Entries, data, 0, entryBytes);
                Marshal.Copy(resolvePaths ? result.PathStrings : result.EntryStrings, data, entryBytes, stringBytes);
                Assert.AreEqual(expectedHash, Convert.ToHexStringLower(SHA256.HashData(data)));
            }
            finally
            {
                MFTLibNative._freeMftResult(pointer);
            }
        }
        finally
        {
            File.Delete(path);
        }
    }

    [TestMethod]
    public void IncludeFreed_RandomGenerator_RemainsByteIdentical()
    {
        var path = Path.GetTempFileName();
        try
        {
            MftVolume.GenerateSyntheticMFT(path, 1024, 256);
            Assert.AreEqual("716b83ae4e2fa5b3e67ff07cd948edc97839a7736e95ee3a2d54ae36c8426fbe",
                Convert.ToHexStringLower(SHA256.HashData(File.ReadAllBytes(path))));
        }
        finally
        {
            File.Delete(path);
        }
    }

    [TestMethod]
    public void IncludeFreed_NamePoolExhaustion_PreservesUnresolvedBareNames()
    {
        var path = Path.GetTempFileName();
        try
        {
            MftVolume.GenerateFixtureMFT(path);
            MFTLibNative.NativeSetMaxThreads(1);
            MFTLibNative.NativeSetNamePoolCapacityOverride(1);
            var pointer = MFTLibNative._parseMftFromFile(path, null,
                MatchFlags.IncludeFreed | MatchFlags.ResolvePaths, 256);
            Assert.AreNotEqual(IntPtr.Zero, pointer);
            try
            {
                var result = Marshal.PtrToStructure<MftParseResult>(pointer);
                StringAssert.Contains(result.ErrorMessage, "Path name pool exhausted");
                Assert.AreEqual(16ul, result.UsedRecords);
                for (var index = 8; index < 16; index++)
                {
                    var entry = result.PathEntries + index * checked((int)result.EntryStride);
                    var flags = (ushort)Marshal.ReadInt16(entry, 28);
                    Assert.AreEqual(0x4000, flags & 0x4001);
                }

                var child = result.PathEntries + 9 * checked((int)result.EntryStride);
                var nameOffset = checked((int)Marshal.ReadInt64(child, 16) * 2);
                var nameLength = (ushort)Marshal.ReadInt16(child, 30);
                Assert.AreEqual("deleted-before.txt", Marshal.PtrToStringUni(result.PathStrings + nameOffset, nameLength));
            }
            finally
            {
                MFTLibNative._freeMftResult(pointer);
            }
        }
        finally
        {
            File.Delete(path);
        }
    }
}
