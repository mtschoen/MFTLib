using System.Runtime.Versioning;
using MFTLib.Index;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace MFTLib.Tests.Index;

[TestClass]
public sealed class WindowsVolumeDiscoveryTests
{
    [TestMethod]
    [DataRow(DriveType.Fixed, true, "NTFS", null)]
    [DataRow(DriveType.Fixed, true, "ntfs", null)]
    [DataRow(DriveType.Fixed, true, "exFAT", "not NTFS (exFAT)")]
    [DataRow(DriveType.Fixed, false, "NTFS", "not ready")]
    [DataRow(DriveType.Removable, true, "NTFS", null)]
    [DataRow(DriveType.Removable, true, "exFAT", "not NTFS (exFAT)")]
    [DataRow(DriveType.Network, true, "NTFS", "network")]
    [DataRow(DriveType.CDRom, false, "CDFS", "optical")]
    [DataRow(DriveType.NoRootDirectory, false, "", "not ready")]
    [DataRow(DriveType.Ram, true, "NTFS", "other drive type")]
    public void ReadVolumeVerdicts_ClassifiesByTypeThenReadinessThenFormat(
        DriveType type, bool ready, string format, string? reason)
    {
        var readsReadiness = type is DriveType.Fixed or DriveType.Removable;
        var result = WindowsVolume.ReadVolumeVerdicts(new[] { 0 }, _ => @"X:\", _ => type,
            _ => readsReadiness ? ready
                : throw new AssertFailedException("Readiness of a drive that is neither fixed nor removable was read."),
            _ => readsReadiness && ready ? format
                : throw new AssertFailedException("Format was read without a ready fixed or removable root."));

        var volume = result.Single();
        Assert.AreEqual(new VolumeKey('X'), volume.Volume);
        Assert.AreEqual(@"X:\", volume.RootDirectory);
        Assert.AreEqual(type, volume.DriveType);
        Assert.AreEqual(readsReadiness && ready, volume.IsReady);
        Assert.AreEqual(readsReadiness && ready ? format : null, volume.Format);
        Assert.AreEqual(reason is null, volume.IsIndexable);
        Assert.AreEqual(reason, volume.Reason);
    }

    [TestMethod]
    public void ReadVolumeVerdicts_RemovableNotReadyReportsNotReady()
    {
        var volume = WindowsVolume.ReadVolumeVerdicts(new[] { 0 }, _ => @"E:\", _ => DriveType.Removable,
            _ => false, _ => throw new AssertFailedException("Format was read for a drive that is not ready.")).Single();

        Assert.IsFalse(volume.IsReady);
        Assert.IsNull(volume.Format);
        Assert.IsFalse(volume.IsIndexable);
        Assert.AreEqual("not ready", volume.Reason);
    }

    [TestMethod]
    public void ReadVolumeVerdicts_ReportsVanishedRootNotReadyAndContinues()
    {
        var result = WindowsVolume.ReadVolumeVerdicts(new[] { 0, 1 },
            root => root == 0 ? @"Q:\" : @"C:\", _ => DriveType.Fixed,
            root => root == 0 ? throw new IOException("device vanished") : true, _ => "NTFS");

        Assert.HasCount(2, result);
        Assert.AreEqual(new VolumeKey('Q'), result[0].Volume);
        Assert.IsFalse(result[0].IsReady);
        Assert.IsNull(result[0].Format);
        Assert.AreEqual("not ready", result[0].Reason);
        Assert.IsTrue(result[1].IsIndexable);
        Assert.IsTrue(result[1].IsReady);
        Assert.AreEqual("NTFS", result[1].Format);
    }

    [TestMethod]
    [OSCondition(OperatingSystems.Windows)]
    [SupportedOSPlatform("windows")]
    public void EnumerateAll_ReportsTheSystemVolumeIndexable()
    {
        var systemRoot = Path.GetPathRoot(Environment.SystemDirectory)!;
        var system = WindowsVolume.EnumerateAll().Single(volume =>
            string.Equals(volume.RootDirectory, systemRoot, StringComparison.OrdinalIgnoreCase));

        Assert.AreEqual(DriveType.Fixed, system.DriveType);
        Assert.IsTrue(system.IsReady);
    }
}
