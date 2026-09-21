using MFTLib.Index;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace MFTLib.Tests.Index;

/// <summary>
///     Factual journal sizing settings.
/// </summary>
[TestClass]
public class UsnJournalSettingsTests
{
    [TestMethod]
    public void Settings_ContainOnlyFactsRatherThanFixedRecommendations()
    {
        var settings = new UsnJournalSettings { MaximumSize = 17, AllocationDelta = 3 };
        Assert.AreEqual(17L, settings.MaximumSize);
        Assert.AreEqual(3L, settings.AllocationDelta);
        Assert.IsNull(typeof(UsnJournalSettings).GetProperty("IsBelowRecommended"));
        Assert.IsNull(typeof(UsnJournalSettings).Assembly.GetType("MFTLib.Index.UsnJournalRecommendations"));
    }
}
