using ArchUnitNET.Domain;
using ArchUnitNET.Fluent.Syntax.Elements.Types;
using ArchUnitNET.Loader;
using MFTLib.Index;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using static ArchUnitNET.Fluent.ArchRuleDefinition;

namespace MFTLib.Tests.Index;

/// <summary>
///     MFTLib#118. The packed index is substrate-neutral by design: the block format, queries,
///     snapshots, mutation and the enumeration producer must not reach into the MFT, broker,
///     elevation or interop code. That is a namespace boundary, and because this repository uses
///     a flat <c>MFTLib</c> namespace, <c>namespace MFTLib.Index</c> resolves every one of those
///     types with no <c>using</c> at all, so the boundary can be crossed by accident and no
///     import-based lint can see it. This test reads the built assembly's IL instead.
/// </summary>
/// <remarks>
///     Known and accepted gap: a <c>const</c> inlined from a forbidden type leaves no IL
///     reference, so this rule cannot see it. Everything else (fields, parameters, return types,
///     locals, method calls, attributes, generic arguments) does leave one.
/// </remarks>
[TestClass]
public class NamespaceBoundaryTests
{
    // ResideInNamespaceMatching uses anchored regexes to select only these exact namespaces.
    // One forbidden namespace pattern keeps every allowlist exclusion under the same And chain.
    const string IndexNamespacePattern = @"^MFTLib\.Index$";
    const string ForbiddenNamespacePattern = @"^MFTLib(\.Interop)?$";

    static readonly Architecture ProductionArchitecture =
        new ArchLoader().LoadAssembly(typeof(FileIndex).Assembly).Build();

    static readonly Architecture ProductionAndFixtureArchitecture =
        new ArchLoader().LoadAssembly(
            typeof(FileIndex).Assembly)
            .LoadAssembly(typeof(NamespaceBoundaryTests).Assembly).Build();

    /// <summary>
    ///     The journal value types the index legitimately consumes, by full name, one reason each.
    ///     Growing this list is a review decision, not a mechanical edit: every addition widens
    ///     what the index is allowed to know about.
    /// </summary>
    static TypesShouldConjunctionWithDescription BoundaryRule()
    {
        var forbidden = Types().That()
            .ResideInNamespaceMatching(ForbiddenNamespacePattern)
            // ArchUnitNET 0.13.4: DoNotHaveFullName(string) excludes one exact full type name.
            // The journal batch a JournalMutator applies is a value type, not a substrate. It
            // carries no volume handle and no native dependency.
            .And().DoNotHaveFullName("MFTLib.UsnJournalEntry")
            // The options record that shapes a UsnJournalEntry. Same reason as above.
            .And().DoNotHaveFullName("MFTLib.UsnJournalEntryOptions")
            // The reason flags on a journal entry. A plain [Flags] enum of uint.
            .And().DoNotHaveFullName("MFTLib.UsnReason");

        // MFTLib#118's ruling also names UsnJournalId. There is no such type: the journal id
        // crosses the boundary as a plain ulong (BlockHeader.UsnJournalId at BlockHeader.cs:32),
        // so there is nothing to allow. Recorded here rather than silently dropped.

        return Types().That().ResideInNamespaceMatching(IndexNamespacePattern)
            .Should().NotDependOnAny(forbidden)
            .Because("the packed index is substrate-neutral and must not reach into the MFT, " +
                     "broker, elevation or interop code");
    }

    /// <summary>Checks the namespace boundary against the production assembly alone.</summary>
    [TestMethod]
    public void MFTLibIndex_DoesNotDependOnTheFlatNamespaceOrInterop()
    {
        var rule = BoundaryRule();
        if (rule.HasNoViolations(ProductionArchitecture))
        {
            return;
        }

        Assert.Fail(string.Join(Environment.NewLine,
            rule.Evaluate(ProductionArchitecture).Select(result => result.Description)));
    }

    /// <summary>
    ///     The negative control MFTLib#118 makes mandatory. The same rule object, evaluated over an
    ///     architecture that also holds <see cref="NamespaceBoundaryViolationFixture" />, must
    ///     report a violation and must name that fixture. If this ever passes for a different
    ///     reason, the fixture and forbidden type assertions prevent an unrelated violation from
    ///     satisfying the control.
    /// </summary>
    [TestMethod]
    public void TheBoundaryRule_ReportsADeliberateViolation()
    {
        var rule = BoundaryRule();

        Assert.IsFalse(rule.HasNoViolations(ProductionAndFixtureArchitecture),
            "the rule must report the deliberate violation in NamespaceBoundaryViolationFixture");

        var descriptions = string.Join(Environment.NewLine,
            rule.Evaluate(ProductionAndFixtureArchitecture).Select(result => result.Description));
        StringAssert.Contains(descriptions, nameof(NamespaceBoundaryViolationFixture));
        StringAssert.Contains(descriptions, "MftRecord");
    }
}
