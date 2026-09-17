using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace MFTLib.Tests;

internal static class NativeFieldReferenceFixture
{
    public static void Reference() => MFTLibNative._getMftNativeAbiVersion = () => 1;
}

internal static class VolumeFieldReferenceFixture
{
    public static object Reference() => FileUtilities._getVolumeHandle;
}

internal class NativeResetReferenceFixture
{
    public virtual void Reference() => MFTLibNative.ResetToDefaults();
}

internal static class VolumeResetReferenceFixture
{
    public static void Reference() => FileUtilities.ResetToDefaults();
}

internal static class AsyncSeamReferenceFixture
{
    public static async Task Reference()
    {
        await Task.Yield();
        MFTLibNative.ResetToDefaults();
    }
}

internal static class NestedSeamReferenceFixture
{
    internal static class Helper
    {
        public static void Reference() => FileUtilities.ResetToDefaults();
    }
}

internal static class HelperSeamReferenceFixture
{
    public static void Reference() => new NativeResetReferenceFixture().Reference();
}

internal sealed class InheritedSeamReferenceFixture : NativeResetReferenceFixture
{
}

[DoNotParallelize]
internal static class IsolatedSeamReferenceFixture
{
    public static void Reference() => MFTLibNative.ResetToDefaults();
}

internal static class HarmlessReferenceFixture
{
    public static uint Reference() => MFTLibNative.ExpectedMftNativeAbiVersion;
}

internal static class ExternalAsyncHelper
{
    public static async Task ResetAsync()
    {
        await Task.Yield();
        MFTLibNative.ResetToDefaults();
    }
}

internal static class ExternalAsyncHelperSeamReferenceFixture
{
    public static async Task Reference() => await ExternalAsyncHelper.ResetAsync();
}

[DoNotParallelize]
internal static class IsolatedExternalAsyncHelperSeamReferenceFixture
{
    public static async Task Reference() => await ExternalAsyncHelper.ResetAsync();
}

internal static class ExternalIteratorHelper
{
    public static IEnumerable<int> YieldReset()
    {
        yield return 1;
        MFTLibNative.ResetToDefaults();
    }
}

internal static class ExternalIteratorHelperSeamReferenceFixture
{
    public static void Reference()
    {
        foreach (var _ in ExternalIteratorHelper.YieldReset())
        {
        }
    }
}

