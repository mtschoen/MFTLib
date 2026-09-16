using MFTLib.Index;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace MFTLib.Tests.Index;

/// <summary>
///     The recommended journal sizing constants and
///     <see cref="UsnJournalSettings.IsBelowRecommended" />.
/// </summary>
[TestClass]
public class UsnJournalSettingsTests
{
    [TestMethod]
    public void Recommendations_AreTheDocumentedValues()
    {
        Assert.AreEqual(128L * 1024 * 1024, UsnJournalRecommendations.RecommendedMaximumSize);
        Assert.AreEqual(16L * 1024 * 1024, UsnJournalRecommendations.RecommendedAllocationDelta);
    }

    [TestMethod]
    public void IsBelowRecommended_ComparesAgainstTheRecommendedMaximum()
    {
        Assert.IsTrue(new UsnJournalSettings
        {
            MaximumSize = 32L * 1024 * 1024,
            AllocationDelta = 8L * 1024 * 1024
        }.IsBelowRecommended);
        Assert.IsFalse(new UsnJournalSettings
        {
            MaximumSize = 128L * 1024 * 1024,
            AllocationDelta = 16L * 1024 * 1024
        }.IsBelowRecommended);
        Assert.IsFalse(new UsnJournalSettings
        {
            MaximumSize = 512L * 1024 * 1024,
            AllocationDelta = 64L * 1024 * 1024
        }.IsBelowRecommended);
    }
}
