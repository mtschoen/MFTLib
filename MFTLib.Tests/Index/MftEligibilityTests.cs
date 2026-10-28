using MFTLib.Index;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace MFTLib.Tests.Index;

[TestClass]
public sealed class MftEligibilityTests
{
    static VolumeFacts Eligible() =>
        new(true, new VolumeKey('T'), @"T:\", 7, true, DriveType.Fixed, "NTFS");

    [TestMethod]
    [DataRow("Platform", MftUnavailableReason.PlatformWithoutMft)]
    [DataRow("NoLetter", MftUnavailableReason.NotLocalVolume)]
    [DataRow("NotReady", MftUnavailableReason.VolumeNotReady)]
    [DataRow("Network", MftUnavailableReason.NotLocalVolume)]
    [DataRow("Optical", MftUnavailableReason.NotLocalVolume)]
    [DataRow("ExFat", MftUnavailableReason.NotNtfs)]
    [DataRow("NoSource", MftUnavailableReason.NoMftSource)]
    public void Evaluate_ReportsTheFirstFailedFact(string departure, MftUnavailableReason expected)
    {
        var facts = Eligible();
        var hasSource = true;
        switch (departure)
        {
            case "Platform":
                facts = facts with { PlatformHasMft = false };
                break;
            case "NoLetter":
                facts = facts with { Volume = null, VolumeRootDirectory = null };
                break;
            case "NotReady":
                facts = facts with { IsReady = false };
                break;
            case "Network":
                facts = facts with { DriveType = DriveType.Network };
                break;
            case "Optical":
                facts = facts with { DriveType = DriveType.CDRom };
                break;
            case "ExFat":
                facts = facts with { Format = "exFAT" };
                break;
            case "NoSource":
                hasSource = false;
                break;
            default:
                Assert.Fail($"Unknown departure {departure}.");
                break;
        }

        Assert.AreEqual(expected, MftEligibility.Evaluate(in facts, hasSource));
    }

    [TestMethod]
    [DataRow(DriveType.Fixed, "NTFS")]
    [DataRow(DriveType.Fixed, "ntfs")]
    [DataRow(DriveType.Removable, "NTFS")]
    public void Evaluate_EligibleFixedAndRemovableNtfs_IsNull(DriveType type, string format)
    {
        var facts = Eligible() with { DriveType = type, Format = format };

        Assert.IsNull(MftEligibility.Evaluate(in facts, hasMftSource: true));
    }

    [TestMethod]
    public void Evaluate_PlatformBeatsEveryOtherReason()
    {
        var facts = new VolumeFacts(false, null, null, 0, false, DriveType.Network, "exFAT");

        Assert.AreEqual(MftUnavailableReason.PlatformWithoutMft, MftEligibility.Evaluate(in facts, hasMftSource: false));
    }

    [TestMethod]
    public void Evaluate_ARemovableDriveWithoutMediaIsNotReady()
    {
        var facts = Eligible() with { DriveType = DriveType.Removable, IsReady = false, Format = null };

        Assert.AreEqual(MftUnavailableReason.VolumeNotReady, MftEligibility.Evaluate(in facts, hasMftSource: true));
    }
}
