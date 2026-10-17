using System.Runtime.InteropServices;
using MFTLib.NativeInterop;
using MFTLib.Tests.TestSupport;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace MFTLib.Tests;

public partial class NativeCoverageTests
{
    [TestMethod]
    public void GenerateDump_ThreadStartExceptionReturnsFalseAndNextGenerationSucceeds()
    {
        var path = Path.GetTempFileName();
        try
        {
            NativeTestHooks.NativeSetMaxThreads(2);
            NativeTestHooks.NativeSetAllocFailCountdown(-2);
            Assert.IsFalse(MFTLibNative._generateSyntheticMftSized(path, 1024, 256, 1024));
            NativeTestHooks.NativeResetTestState();
            NativeTestHooks.NativeSetMaxThreads(2);
            Assert.IsTrue(MFTLibNative._generateSyntheticMftSized(path, 1024, 256, 1024));
            using var input = MftDumpInput.Open(path);
            using var result = input.Parse();
            Assert.IsTrue(result.UsedRecords > 0);
        }
        finally
        {
            File.Delete(path);
        }
    }

    [TestMethod]
    [DataRow(-1, "Out of memory in native operation")]
    [DataRow(-2, "Native exception in operation")]
    [DataRow(-4, "Native exception in operation")]
    [DataRow(-5, "Native exception in operation")]
    [TestCategory("Smoke")]
    public void ParseDump_ExceptionReturnsEmptyFailureAndNextParseSucceeds(int mode, string message)
    {
        var path = Path.GetTempFileName();
        try
        {
            NativeTestHooks.NativeSetMaxThreads(2);
            Assert.IsTrue(MFTLibNative._generateSyntheticMftSized(path, 1024, 256, 1024));
            using var input = MFTLibNative.OpenMftDumpInput(MFTLibNative.NullTerminatedUtf8(path), out var info);
            Assert.IsFalse(input.IsInvalid, info.ErrorMessage);
            NativeTestHooks.NativeSetAllocFailCountdown(mode);
            var pointer = MFTLibNative.ParseMftDumpInput(input, 256, IntPtr.Zero, null);
            Assert.AreNotEqual(IntPtr.Zero, pointer);
            try
            {
                var result = Marshal.PtrToStructure<MftParseResult>(pointer);
                Assert.AreEqual(message, result.ErrorMessage);
                Assert.AreEqual(0UL, result.UsedRecords);
                Assert.AreEqual(IntPtr.Zero, result.Entries);
                Assert.AreEqual(IntPtr.Zero, result.EntryStrings);
            }
            finally
            {
                MFTLibNative._freeMftResult(pointer);
            }

            NativeTestHooks.NativeResetTestState();
            NativeTestHooks.NativeSetMaxThreads(2);
            pointer = MFTLibNative.ParseMftDumpInput(input, 256, IntPtr.Zero, null);
            Assert.AreNotEqual(IntPtr.Zero, pointer);
            try
            {
                var result = Marshal.PtrToStructure<MftParseResult>(pointer);
                Assert.AreEqual(string.Empty, result.ErrorMessage);
                Assert.IsTrue(result.UsedRecords > 0);
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
    [DataRow(1u)]
    [DataRow(2u)]
    public void ParseDump_ExceptionAfterCompletedChunkDiscardsPartialOutput(uint threads)
    {
        var path = Path.GetTempFileName();
        try
        {
            NativeTestHooks.NativeSetMaxThreads(threads);
            Assert.IsTrue(MFTLibNative._generateSyntheticMftSized(path, 1024, 256, 1024));
            using var input = MFTLibNative.OpenMftDumpInput(MFTLibNative.NullTerminatedUtf8(path), out var info);
            Assert.IsFalse(input.IsInvalid, info.ErrorMessage);
            var pointer = MFTLibNative.ParseMftDumpInput(input, 256, IntPtr.Zero,
                (records, _, _) =>
                {
                    if (records == 256)
                    {
                        NativeTestHooks.NativeSetAllocFailCountdown(-1);
                    }
                });
            Assert.AreNotEqual(IntPtr.Zero, pointer);
            try
            {
                var result = Marshal.PtrToStructure<MftParseResult>(pointer);
                StringAssert.Contains(result.ErrorMessage, "Out of memory");
                Assert.AreEqual(0UL, result.UsedRecords);
                Assert.AreEqual(IntPtr.Zero, result.Entries);
                Assert.AreEqual(IntPtr.Zero, result.EntryStrings);
            }
            finally
            {
                MFTLibNative._freeMftResult(pointer);
            }

            NativeTestHooks.NativeResetTestState();
            using var managedInput = MftDumpInput.Open(path);
            using var ordinaryResult = managedInput.Parse();
            Assert.IsTrue(ordinaryResult.UsedRecords > 0);
            NativeTestHooks.NativeSetAllocFailCountdown(-1);
            var failure = Assert.ThrowsException<InvalidOperationException>(() =>
            {
                using var failingInput = MftDumpInput.Open(path);
                using var unexpectedResult = failingInput.Parse();
            });
            StringAssert.Contains(failure.Message, "Out of memory");
        }
        finally
        {
            File.Delete(path);
        }
    }
}
