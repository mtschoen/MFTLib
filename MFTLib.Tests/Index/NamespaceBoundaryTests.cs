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
    const string ForbiddenNamespacePattern = @"^MFTLib(\.Interop|\.NativeInterop)?$";
    const string SharedInteropNamespace = "MFTLib.NativeInterop";

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
            .And().DoNotResideInNamespace(SharedInteropNamespace)
            // ArchUnitNET 0.13.4: DoNotHaveFullName(string) excludes one exact full type name.
            // The journal batch a JournalMutator applies is a value type, not a substrate. It
            // carries no volume handle and no native dependency.
            .And().DoNotHaveFullName("MFTLib.UsnJournalEntry")
            // The journal sizing the index reports but does not own; a plain value record of two longs.
            .And().DoNotHaveFullName("MFTLib.UsnJournalSettings")
            // The reason flags on a journal entry. A plain [Flags] enum of uint.
            .And().DoNotHaveFullName("MFTLib.UsnReason");

        // MFTLib#118's ruling also names UsnJournalIdentifier. There is no such type: the journal identifier
        // crosses the boundary as a plain ulong (BlockHeader.UsnJournalIdentifier at BlockHeader.cs:32),
        // so there is nothing to allow. Recorded here rather than silently dropped.

        return Types().That().ResideInNamespaceMatching(IndexNamespacePattern)
            .Should().NotDependOnAny(forbidden)
            .Because("the packed index is substrate-neutral and may only use the explicitly shared " +
                     "native boundary, and must not reach into the MFT, broker, elevation or unshared interop code");
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
        Assert.Contains(nameof(NamespaceBoundaryViolationFixture), descriptions);
        Assert.Contains("MftRecord", descriptions);
    }

    static GivenTypesConjunction OwnedTypes(params string[] names)
    {
        var selection = Types().That().HaveFullNameContaining("MFTLib." + names[0]);
        foreach (var name in names.Skip(1))
        {
            selection = selection.Or().HaveFullNameContaining("MFTLib." + name);
        }
        return selection;
    }

    static GivenTypesConjunction SourceTypes() => OwnedTypes(
        "DirectProgress", "FreedRowTrust", "LiveVolumeSources", "LocalMftBlockProducer",
        "MftBlockRowWriter", "MftBlockScan", "BlockStamp",
        "MftDumpBlockProducer", "MftDumpRecordSource", "MftDumpRecordValidation",
        "MftRecordBatchSource",
        "MftBlockCapacity", "ParseThreadAllocator", "ParseThreadRegistration",
        "IVolumeAccess", "LiveVolumeAccess", "ISourceOperationReporter", "BlockWriteProgress",
        "BlockScanPhase", "BlockWriteReporting", "BlockWriteResult",
        "MftBlockProgressAdapter", "MftBlockValidation", "MftBlockProducerPipeline",
        "Index.SourcesBoundaryViolationFixture");

    static GivenTypesConjunction BrokerTypes() => OwnedTypes(
        "Broker", "IBroker", "JournalBrokerHost", "HostPipeWriter",
        "ChannelOperationKind", "ChannelOperationState", "ClientDisconnectedException",
        "RealBlockSectionWriter", "IBlockSectionWriter", "BlockScanOutcome", "BlockScanTarget",
        "DefaultElevatedEntryRunner", "ElevatedEntryPoint",
        "NamedPipeBrokerPipeFactory");

    static TypesShouldConjunctionWithDescription SourceBoundaryRule() =>
        Types().That().Are(SourceTypes()).Should().NotDependOnAny(BrokerTypes())
            .Because("source-owned scan plumbing must not depend on broker-owned types");

    [TestMethod]
    public void Sources_DoNotDependOnBrokerOwnedTypes()
    {
        Assert.IsNotEmpty(SourceTypes().GetObjects(ProductionArchitecture), "SourceTypes must match production types.");
        Assert.IsNotEmpty(BrokerTypes().GetObjects(ProductionArchitecture), "BrokerTypes must match production types.");
        var rule = SourceBoundaryRule();
        Assert.IsTrue(rule.HasNoViolations(ProductionArchitecture),
            string.Join(Environment.NewLine,
                rule.Evaluate(ProductionArchitecture).Select(result => result.Description)));
    }

    [TestMethod]
    public void SourceBoundary_ReportsTheDeliberateBrokerReference()
    {
        Assert.IsNotEmpty(SourceTypes().GetObjects(ProductionArchitecture), "SourceTypes must match production types.");
        Assert.IsNotEmpty(BrokerTypes().GetObjects(ProductionArchitecture), "BrokerTypes must match production types.");
        var rule = SourceBoundaryRule();
        Assert.IsFalse(rule.HasNoViolations(ProductionAndFixtureArchitecture));
        var descriptions = string.Join(Environment.NewLine,
            rule.Evaluate(ProductionAndFixtureArchitecture).Select(result => result.Description));
        Assert.Contains("SourcesBoundaryViolationFixture", descriptions);
        Assert.Contains("BrokerMftBlockProducer", descriptions);
    }
}
