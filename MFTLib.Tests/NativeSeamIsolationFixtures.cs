using MFTLib.Index;
using MFTLib.NativeInterop;
using MFTLib.Tests.TestSupport;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace MFTLib.Tests;

static class WalkHooksReferenceFixture
{
    public static void Reference() => EnumerationWalkLimit.Hooks._walkQueued = null;
}

static class DeletionHooksReferenceFixture
{
    public static void Reference() => CacheDirectory.Hooks._beforeDelete = null;
}

static class VolumeDependenciesReferenceFixture
{
    public static void Reference() => IndexedDrive.Dependencies._volumeSerialReader = null;
}

static class OpenDependenciesReferenceFixture
{
    public static object Reference() => FileEntry.Dependencies._openById;
}

static class QueryDependenciesReferenceFixture
{
    public static void Reference() => UsnJournalSettingsQuery.Dependencies._queryOverride = null;
}

static class JournalHooksReferenceFixture
{
    public static void Reference() => JournalCheckpointCheck.Hooks.ObservationOverride = null;
}

static class NativeFieldReferenceFixture
{
    public static void Reference() => MFTLibNative.Hooks._getMftNativeAbiVersion = () => 1;
}

static class VolumeFieldReferenceFixture
{
    public static object Reference() => FileUtilities.Hooks._getVolumeHandle;
}

class NativeResetReferenceFixture
{
    public virtual void Reference() => MFTLibNative.Hooks.ResetToDefaults();
}

static class VolumeResetReferenceFixture
{
    public static void Reference() => FileUtilities.Hooks.ResetToDefaults();
}

static class Win32HooksReferenceFixture
{
    public static object Reference() => Kernel32.Hooks._deviceIoControl;
}

static class AsyncSeamReferenceFixture
{
    public static async Task Reference()
    {
        await Task.Yield();
        MFTLibNative.Hooks.ResetToDefaults();
    }
}

static class NestedSeamReferenceFixture
{
    internal static class Helper
    {
        public static void Reference() => FileUtilities.Hooks.ResetToDefaults();
    }
}

static class HelperSeamReferenceFixture
{
    public static void Reference() => new NativeResetReferenceFixture().Reference();
}

sealed class InheritedSeamReferenceFixture : NativeResetReferenceFixture;

[DoNotParallelize]
static class IsolatedSeamReferenceFixture
{
    public static void Reference() => MFTLibNative.Hooks.ResetToDefaults();
}

static class HarmlessReferenceFixture
{
    public static uint Reference() => MFTLibNative.ExpectedMftNativeAbiVersion;
}

static class ExternalAsyncHelper
{
    public static async Task ResetAsync()
    {
        await Task.Yield();
        MFTLibNative.Hooks.ResetToDefaults();
    }
}

static class ExternalAsyncHelperSeamReferenceFixture
{
    public static Task Reference() => ExternalAsyncHelper.ResetAsync();
}

[DoNotParallelize]
static class IsolatedExternalAsyncHelperSeamReferenceFixture
{
    public static Task Reference() => ExternalAsyncHelper.ResetAsync();
}

static class ExternalIteratorHelper
{
    public static IEnumerable<int> YieldReset()
    {
        yield return 1;
        MFTLibNative.Hooks.ResetToDefaults();
    }
}

static class ExternalIteratorHelperSeamReferenceFixture
{
    public static void Reference()
    {
        foreach (var _ in ExternalIteratorHelper.YieldReset())
        {
        }
    }
}

static class NativeHookReferenceFixture
{
    public static void Reference() => NativeTestHooks.NativeSetAllocFailCountdown(1);
}

[DoNotParallelize]
static class IsolatedNativeHookReferenceFixture
{
    public static void Reference() => NativeTestHooks.NativeSetAllocFailCountdown(1);
}

static class EnvironmentVariableReferenceFixture
{
    public static void Reference() => Environment.SetEnvironmentVariable("MFTLIB_FIXTURE", null);
}

[DoNotParallelize]
static class IsolatedEnvironmentVariableReferenceFixture
{
    public static void Reference() => Environment.SetEnvironmentVariable("MFTLIB_FIXTURE", null);
}

static class CurrentDirectoryReferenceFixture
{
    public static void Reference() => Environment.CurrentDirectory = Path.GetTempPath();
}

static class ConsoleRedirectReferenceFixture
{
    public static void Reference() => Console.SetOut(TextWriter.Null);
}

static class ProductionStaticFieldReferenceFixture
{
    public static void Reference() => BrokerLauncher.Dependencies._startProcess = _ => null;
}

static class ProductionStaticSetterReferenceFixture
{
    public static void Reference() => BrokerDiagnostics.IncludeSelfEntries = true;
}

static class ProductionResetReferenceFixture
{
    public static void Reference() => BrokerLauncher.Dependencies.ResetToDefaults();
}

static class LaunchOwnerReferenceFixture
{
    public static object Reference() => BrokerLauncher.Dependencies;
}

static class LaunchMemberReferenceFixture
{
    public static void Reference(BrokerLauncher.LaunchDependencies dependencies) =>
        dependencies._startProcess = _ => null;
}

[DoNotParallelize]
static class IsolatedLaunchReferenceFixture
{
    public static void Reference() => BrokerLauncher.Dependencies.ResetToDefaults();
}

static class PerProcessHooksReferenceFixture
{
    public static void Reference(BrokerProcess process) =>
        process.Hooks.AfterControlWriteLockAcquired = null;
}

static class ReadOnlyEnvironmentReferenceFixture
{
    public static string Reference() => Environment.GetEnvironmentVariable("MFTLIB_FIXTURE") ?? Environment.CurrentDirectory;
}

static class DiagnosticsOwnerReferenceFixture
{
    public static object Reference() => BrokerDiagnostics.Dependencies;
}

static class DiagnosticsMemberReferenceFixture
{
    public static void Reference(BrokerDiagnostics.DiagnosticsDependencies dependencies,
        BrokerDiagnosticsWriter writer) => dependencies.InstallWriter(writer);
}

[DoNotParallelize]
static class IsolatedDiagnosticsReferenceFixture
{
    public static void Reference() => BrokerDiagnostics.Dependencies.ResetToDefaults();
}
