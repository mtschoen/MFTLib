using System.Runtime.InteropServices;
using MFTLib.Interop;
using MFTLib.Tests.TestSupport;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using Microsoft.Win32.SafeHandles;

namespace MFTLib.Tests;

[TestClass]
[DoNotParallelize]
public class ParseThreadAllowanceTests
{
    [TestCleanup]
    public void Cleanup()
    {
        MFTLibNative.ResetToDefaults();
        FileUtilities.ResetToDefaults();
    }

    [DataTestMethod]
    [DataRow(0)]
    [DataRow(-1)]
    public void Constructor_CountBelowOne_Throws(int count)
    {
        Assert.ThrowsException<ArgumentOutOfRangeException>(() => new ParseThreadAllowance(count));
    }

    [DataTestMethod]
    [DataRow(0)]
    [DataRow(-1)]
    public void Count_SetBelowOne_Throws(int count)
    {
        var allowance = new ParseThreadAllowance(3);

        Assert.ThrowsException<ArgumentOutOfRangeException>(() => allowance.Count = count);
        Assert.AreEqual(3, allowance.Count);
    }

    [TestMethod]
    public unsafe void Count_WrittenDuringParse_IsVisibleToTheNativeControlBlock()
    {
        var allowance = new ParseThreadAllowance(2);
        var observed = new List<int>();
        FakeParse(control =>
        {
            observed.Add(control->ParseThreadAllowance);
            allowance.Count = 3;
            observed.Add(control->ParseThreadAllowance);
            GC.Collect();
            GC.WaitForPendingFinalizers();
            GC.Collect();
            allowance.Count = 5;
            observed.Add(control->ParseThreadAllowance);
        });

        using var volume = MftVolume.Open("C");
        using (volume.StreamRecords(null, MatchFlags.None, null, allowance, CancellationToken.None))
        {
            CollectionAssert.AreEqual(new[] { 2, 3, 5 }, observed);
        }

        // Detached when the parse returned: the next parse attaches again and sees the current count.
        observed.Clear();
        allowance.Count = 7;
        using (volume.StreamRecords(null, MatchFlags.None, null, allowance, CancellationToken.None))
        {
            Assert.AreEqual(7, observed[0]);
        }
    }

    [TestMethod]
    public unsafe void StreamRecords_NullAllowance_LeavesTheControlBlockAllowanceZero()
    {
        var observed = -1;
        FakeParse(control => observed = control->ParseThreadAllowance);

        using var volume = MftVolume.Open("C");
        using (volume.StreamRecords(null, MatchFlags.None, null, null, CancellationToken.None))
        {
            Assert.AreEqual(0, observed);
        }
    }

    [TestMethod]
    public unsafe void StreamRecords_TokenCancelledDuringParse_SetsCancelRequested()
    {
        using var cancellation = new CancellationTokenSource();
        var observed = new List<int>();
        FakeParse(control =>
        {
            observed.Add(control->CancelRequested);
            // The fake parse runs inside the using scope.
            // ReSharper disable once AccessToDisposedClosure
            cancellation.Cancel();
            observed.Add(control->CancelRequested);
        });

        using var volume = MftVolume.Open("C");
        using (volume.StreamRecords(null, MatchFlags.None, null, null, cancellation.Token))
        {
            CollectionAssert.AreEqual(new[] { 0, 1 }, observed);
        }
    }

    [TestMethod]
    public unsafe void Attach_WhileAttached_Throws()
    {
        var allowance = new ParseThreadAllowance(4);
        using var first = new ParseControlBlock();
        using var second = new ParseControlBlock();
        var firstField = &((MftParseControl*)first.Pointer)->ParseThreadAllowance;
        var secondField = &((MftParseControl*)second.Pointer)->ParseThreadAllowance;

        allowance.Attach(firstField);
        try
        {
            Assert.AreEqual(4, first.Allowance, "Attaching copies the current count into the control block");
            Assert.ThrowsException<InvalidOperationException>(() => allowance.Attach(secondField));
            Assert.AreEqual(0, second.Allowance);
        }
        finally
        {
            allowance.Detach();
        }
    }

    unsafe delegate void ControlBlockObserver(MftParseControl* control);

    // Replaces the native parse with one that hands its control block to observe, then returns an
    // empty successful result.
    static unsafe void FakeParse(ControlBlockObserver observe)
    {
        FileUtilities._getVolumeHandle = _ => new SafeFileHandle(new IntPtr(1), false);
        MFTLibNative._parseMftRecordsWithProgress = (_, _, _, _, control, _) =>
        {
            observe((MftParseControl*)control);
            var result = new MftParseResult
            {
                AbiVersion = MFTLibNative.ExpectedMftNativeAbiVersion,
                EntryStride = MFTLibNative.NativeCompactEntrySize
            };
            var resultPtr = Marshal.AllocHGlobal(Marshal.SizeOf<MftParseResult>());
            Marshal.StructureToPtr(result, resultPtr, false);
            return resultPtr;
        };
        MFTLibNative._freeMftResult = Marshal.FreeHGlobal;
    }
}
