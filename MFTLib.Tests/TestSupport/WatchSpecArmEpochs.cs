using System.Globalization;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace MFTLib.Tests.TestSupport;

static class WatchSpecArmEpochs
{
    public static uint ForDrive(BrokerFrame startWatch, string driveLetter)
    {
        var watchSpec = startWatch.DrivesSpec!;
        foreach (var token in watchSpec.Split(','))
        {
            var driveParts = token.Split(':');
            if (driveParts.Length > 3 &&
                string.Equals(driveParts[0], driveLetter, StringComparison.OrdinalIgnoreCase))
            {
                return uint.Parse(driveParts[3], CultureInfo.InvariantCulture);
            }
        }

        Assert.Fail($"StartWatch spec '{watchSpec}' has no arm epoch for drive '{driveLetter}'.");
        return BrokerFrame.NoArmEpoch;
    }
}
