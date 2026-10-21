using MFTLib.Index;
using MFTLib.NativeInterop;
using MFTLib.Tests.TestSupport;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace MFTLib.Tests;

internal static class WalkHooksReferenceFixture
{
    public static void Reference() => EnumerationWalkLimit.Hooks._walkQueued = null;
}

internal static class DeletionHooksReferenceFixture
{
    public static void Reference() => CacheDirectory.Hooks._beforeDelete = null;
}

internal static class VolumeDependenciesReferenceFixture
{
    public static void Reference() => IndexedDrive.Dependencies._volumeSerialReader = null;
}

internal static class OpenDependenciesReferenceFixture
{
    public static object Reference() => FileEntry.Dependencies._openById;
}

internal static class QueryDependenciesReferenceFixture
{
    [System.Runtime.Versioning.SupportedOSPlatform("windows")]
    public static void Reference() => UsnJournalSettingsQuery.Dependencies._queryOverride = null;
}

internal static class JournalHooksReferenceFixture
{
    public static void Reference() => JournalCheckpointCheck.Hooks.ObservationOverride = null;
}

internal static class NativeFieldReferenceFixture
{
    public static void Reference() => MFTLibNative.Hooks._getMftNativeAbiVersion = () => 1;
}

internal static class VolumeFieldReferenceFixture
{
    public static object Reference() => FileUtilities.Hooks._getVolumeHandle;
}

internal class NativeResetReferenceFixture
{
    public virtual void Reference() => MFTLibNative.Hooks.ResetToDefaults();
}

internal static class VolumeResetReferenceFixture
{
    public static void Reference() => FileUtilities.Hooks.ResetToDefaults();
}

internal static class Win32HooksReferenceFixture
{
    public static object Reference() => Kernel32.Hooks._deviceIoControl;
}

internal static class AsyncSeamReferenceFixture
{
    public static async Task Reference()
    {
        await Task.Yield();
        MFTLibNative.Hooks.ResetToDefaults();
    }
}

internal static class NestedSeamReferenceFixture
{
    internal static class Helper
    {
        public static void Reference() => FileUtilities.Hooks.ResetToDefaults();
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
    public static void Reference() => MFTLibNative.Hooks.ResetToDefaults();
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
        MFTLibNative.Hooks.ResetToDefaults();
    }
}

internal static class ExternalAsyncHelperSeamReferenceFixture
{
    public static Task Reference() => ExternalAsyncHelper.ResetAsync();
}

[DoNotParallelize]
internal static class IsolatedExternalAsyncHelperSeamReferenceFixture
{
    public static Task Reference() => ExternalAsyncHelper.ResetAsync();
}

internal static class ExternalIteratorHelper
{
    public static IEnumerable<int> YieldReset()
    {
        yield return 1;
        MFTLibNative.Hooks.ResetToDefaults();
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

internal static class NativeHookReferenceFixture
{
    public static void Reference() => NativeTestHooks.NativeSetAllocFailCountdown(1);
}

[DoNotParallelize]
internal static class IsolatedNativeHookReferenceFixture
{
    public static void Reference() => NativeTestHooks.NativeSetAllocFailCountdown(1);
}

internal static class EnvironmentVariableReferenceFixture
{
    public static void Reference() => Environment.SetEnvironmentVariable("MFTLIB_FIXTURE", null);
}

[DoNotParallelize]
internal static class IsolatedEnvironmentVariableReferenceFixture
{
    public static void Reference() => Environment.SetEnvironmentVariable("MFTLIB_FIXTURE", null);
}

internal static class CurrentDirectoryReferenceFixture
{
    public static void Reference() => Environment.CurrentDirectory = Path.GetTempPath();
}

internal static class ConsoleRedirectReferenceFixture
{
    public static void Reference() => Console.SetOut(TextWriter.Null);
}

internal static class ProductionStaticFieldReferenceFixture
{
    public static void Reference() => BrokerLauncher.Dependencies._startProcess = _ => null;
}

internal static class ProductionStaticSetterReferenceFixture
{
    public static void Reference() => BrokerDiagnostics.IncludeSelfEntries = true;
}

internal static class ProductionResetReferenceFixture
{
    public static void Reference() => BrokerLauncher.Dependencies.ResetToDefaults();
}

internal static class LaunchOwnerReferenceFixture
{
    public static object Reference() => BrokerLauncher.Dependencies;
}

internal static class LaunchMemberReferenceFixture
{
    public static void Reference(BrokerLauncher.LaunchDependencies dependencies) =>
        dependencies._startProcess = _ => null;
}

[DoNotParallelize]
internal static class IsolatedLaunchReferenceFixture
{
    public static void Reference() => BrokerLauncher.Dependencies.ResetToDefaults();
}

internal static class PerProcessHooksReferenceFixture
{
    public static void Reference(BrokerProcess process) =>
        process.Hooks.AfterControlWriteLockAcquired = null;
}

internal static class ReadOnlyEnvironmentReferenceFixture
{
    public static string Reference() => Environment.GetEnvironmentVariable("MFTLIB_FIXTURE") ?? Environment.CurrentDirectory;
}

internal static class DiagnosticsOwnerReferenceFixture
{
    public static object Reference() => BrokerDiagnostics.Dependencies;
}

internal static class DiagnosticsMemberReferenceFixture
{
    public static void Reference(BrokerDiagnostics.DiagnosticsDependencies dependencies,
        BrokerDiagnosticsWriter writer) => dependencies.InstallWriter(writer);
}

[DoNotParallelize]
internal static class IsolatedDiagnosticsReferenceFixture
{
    public static void Reference() => BrokerDiagnostics.Dependencies.ResetToDefaults();
}
