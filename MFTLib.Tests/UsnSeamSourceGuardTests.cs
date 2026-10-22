using System.Reflection;
using MFTLib.NativeInterop;
using MFTLib.Tests.TestSupport;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using Microsoft.Win32.SafeHandles;

namespace MFTLib.Tests;

/// <summary>
///     The managed USN wrapper is tested through one stack, the native test hooks. These guards keep the four
///     retired USN delegate seams and the per-class fake volume handle factories from coming back.
/// </summary>
[TestClass]
public class UsnSeamSourceGuardTests
{
    const BindingFlags AllDeclared = BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static |
                                     BindingFlags.Instance | BindingFlags.DeclaredOnly;

    static readonly string[] RetiredSeams =
    [
        "_readUsnJournal", "_freeUsnJournalResult", "_watchUsnJournalBatchCancelable", "_cancelUsnJournalWatch"
    ];

    [TestMethod]
    public void NativeBinding_DeclaresNoRetiredUsnDelegateSeam()
    {
        var revived = typeof(MFTLibNative).GetMembers(AllDeclared).Select(member => member.Name)
            .Where(name => RetiredSeams.Contains(name)).ToArray();
        Assert.IsEmpty(revived, $"Retired USN delegate seams reappeared: {string.Join(", ", revived)}");
    }

    [TestMethod]
    public void TestAssembly_HasOneFakeHandleFactory()
    {
        var copies = typeof(UsnSeamSourceGuardTests).Assembly.GetTypes()
            .Where(type => type != typeof(FakeVolumeHandle))
            .SelectMany(type => type.GetMethods(AllDeclared), (type, method) => (type, method))
            .Where(pair => pair.method.Name == "FakeHandle" && pair.method.ReturnType == typeof(SafeFileHandle))
            .Select(pair => pair.type.FullName).ToArray();
        Assert.IsEmpty(copies, $"Copied FakeHandle factories reappeared in: {string.Join(", ", copies)}");
    }
}
