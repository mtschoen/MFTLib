using MFTLibTestExtensions;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace MFTLib.Tests;

/// <summary>
///     The test package is consumed by assemblies that cannot see MFTLib's internals, so no public
///     type of it may list a base type or interface a consumer cannot name (finding F25, see
///     docs/api-decisions.md).
/// </summary>
[TestClass]
public class TestPackageSurfaceTests
{
    [TestMethod]
    public void PublicTypes_ListOnlyPublicBaseTypesAndInterfaces()
    {
        var offenders = Offenders(typeof(BrokerTestHarness).Assembly.GetTypes().Where(type => type.IsVisible));

        Assert.AreEqual(0, offenders.Length, "Public types naming non-public bases or interfaces:\n" +
                                             string.Join('\n', offenders));
    }

    /// <summary>
    ///     Mandatory negative control: the same check, run over a fixture that lists an internal
    ///     interface, must report it. Without it a broken check would pass every package.
    /// </summary>
    [TestMethod]
    public void Check_ReportsAPublicTypeThatListsAnInternalInterface()
    {
        var offenders = Offenders([typeof(LeakyFixture), typeof(CleanFixture)]);

        Assert.AreEqual(1, offenders.Length, string.Join('\n', offenders));
        StringAssert.Contains(offenders[0], nameof(LeakyFixture));
        StringAssert.Contains(offenders[0], nameof(IInternalFixture));
        Assert.AreEqual(0, Offenders([typeof(CleanFixture)]).Length);
    }

    static string[] Offenders(IEnumerable<Type> types) =>
    [
        .. types.OrderBy(type => type.FullName, StringComparer.Ordinal)
            .Select(type => (Type: type, Hidden: HiddenBases(type).ToArray()))
            .Where(entry => entry.Hidden.Length > 0)
            .Select(entry => $"{entry.Type.FullName} lists {string.Join(", ", entry.Hidden.Select(hidden => hidden.FullName))}")
    ];

    static IEnumerable<Type> HiddenBases(Type type)
    {
        var listed = type.GetInterfaces().AsEnumerable();
        if (type.BaseType is { } baseType)
        {
            listed = listed.Append(baseType);
        }

        return listed.Where(candidate => !candidate.IsVisible);
    }

    public interface IPublicFixture;

    internal interface IInternalFixture;

    public sealed class LeakyFixture : IInternalFixture, IPublicFixture;

    public sealed class CleanFixture : IPublicFixture, IDisposable
    {
        public void Dispose()
        {
        }
    }
}
