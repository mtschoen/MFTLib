using System.Diagnostics.CodeAnalysis;
using System.Reflection;
using System.Text.RegularExpressions;
using MFTLib.Index;
using MFTLibTestExtensions;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace MFTLib.Tests;

/// <summary>
///     Enforces the boolean naming rule of docs/api-decisions.md by reflection over the compiled MFTLib and
///     MFTLibTestExtensions assemblies, the same assemblies the approved public surface files are generated
///     from: a public boolean property that reports state starts with Is, Has or Can, and only the option types
///     named in <see cref="InstructionOptionTypes" /> carry bare instruction booleans such as
///     <c>CaseSensitive</c> or <c>IncludeDeleted</c>. The allowlist names types rather than matching a suffix
///     because <c>SearchQuery</c> and <c>ScriptedWatchSource</c> do not end in Options.
/// </summary>
[TestClass]
public class BooleanNamingTests
{
    const BindingFlags DeclaredPublicProperties =
        BindingFlags.Public | BindingFlags.Instance | BindingFlags.Static | BindingFlags.DeclaredOnly;

    /// <summary>The types whose bare boolean properties read as instructions, not as reported state.</summary>
    static readonly HashSet<string> InstructionOptionTypes = new(StringComparer.Ordinal)
    {
        "MFTLib.MftScanOptions",
        "MFTLib.Index.SearchQuery",
        "MFTLibTestExtensions.ScriptedWatchSource"
    };

    static readonly Assembly[] SurfaceAssemblies =
    [
        typeof(FileIndex).Assembly,
        typeof(BrokerTestHarness).Assembly
    ];

    [TestMethod]
    public void PublicBooleanProperties_ReportingStateTakeAStatePrefix()
    {
        var types = SurfaceAssemblies.SelectMany(assembly => assembly.GetExportedTypes()).ToArray();

        Assert.IsTrue(types.SelectMany(BooleanProperties).Any(property => HasStatePrefix(property.Name)),
            "the assemblies must yield prefixed state booleans, or the reflection matched nothing");
        var offenders = BareStateFlags(types, InstructionOptionTypes);
        Assert.AreEqual(0, offenders.Length,
            "public boolean properties must start with Is, Has or Can unless their type is an instruction option type: " +
            string.Join(", ", offenders));
    }

    [TestMethod]
    public void EveryInstructionOptionType_StillHoldsABareBoolean()
    {
        foreach (var optionTypeName in InstructionOptionTypes)
        {
            var optionType = SurfaceAssemblies.Select(assembly => assembly.GetType(optionTypeName)).FirstOrDefault(type => type is not null);
            Assert.IsNotNull(optionType, $"{optionTypeName} is not a type of the surface assemblies; remove it from the allowlist");
            Assert.IsTrue(BooleanProperties(optionType).Any(property => !HasStatePrefix(property.Name)),
                $"{optionTypeName} no longer holds a bare instruction boolean; remove it from the allowlist");
        }
    }

    /// <summary>
    ///     Negative control: the same rule function, applied directly to compile-time fixture types, must report
    ///     every shape of bare boolean the public surface can declare and accept a correctly named one. It does
    ///     not scan assemblies, so a scan that matched nothing cannot satisfy it.
    /// </summary>
    [TestMethod]
    public void TheRule_ReportsEveryShapeOfBareBooleanAndAcceptsAPrefixedOne()
    {
        var fixtures = new[]
        {
            typeof(PlainFixture), typeof(RequiredFixture), typeof(SealedOverrideFixture), typeof(ReadonlyFixture),
            typeof(ByReferenceFixture), typeof(GenericFixture<,>), typeof(IndexerFixture), typeof(FlowFixture),
            typeof(PrefixedFixture)
        };

        var offenders = BareStateFlags(fixtures, new HashSet<string>(StringComparer.Ordinal));

        CollectionAssert.AreEquivalent(new[]
        {
            "MFTLib.Tests.BooleanNamingTests+PlainFixture.Ready",
            "MFTLib.Tests.BooleanNamingTests+PlainFixture.Maybe",
            "MFTLib.Tests.BooleanNamingTests+RequiredFixture.Pending",
            "MFTLib.Tests.BooleanNamingTests+RequiredFixture.Waiting",
            "MFTLib.Tests.BooleanNamingTests+SealedOverrideFixture.Done",
            "MFTLib.Tests.BooleanNamingTests+ReadonlyFixture.Open",
            "MFTLib.Tests.BooleanNamingTests+ByReferenceFixture.Locked",
            "MFTLib.Tests.BooleanNamingTests+ByReferenceFixture.Sealed",
            "MFTLib.Tests.BooleanNamingTests+GenericFixture`2.Both",
            "MFTLib.Tests.BooleanNamingTests+IndexerFixture.Item",
            "MFTLib.Tests.BooleanNamingTests+FlowFixture.Flowed"
        }, offenders);
    }

    static string[] BareStateFlags(IEnumerable<Type> types, HashSet<string> allowedTypeNames) =>
        types
            .Where(type => !allowedTypeNames.Contains(type.FullName!))
            .SelectMany(BooleanProperties)
            .Where(property => !HasStatePrefix(property.Name))
            .Select(property => $"{property.DeclaringType!.FullName}.{property.Name}")
            .ToArray();

    static IEnumerable<PropertyInfo> BooleanProperties(Type type) =>
        type.GetProperties(DeclaredPublicProperties).Where(property => IsBoolean(property.PropertyType));

    static bool IsBoolean(Type propertyType)
    {
        var underlying = propertyType.IsByRef ? propertyType.GetElementType()! : propertyType;
        return (Nullable.GetUnderlyingType(underlying) ?? underlying) == typeof(bool);
    }

    static bool HasStatePrefix(string name) => Regex.IsMatch(name, "^(Is|Has|Can)[A-Z]");

    public sealed class PlainFixture
    {
        public bool Ready { get; init; }

        public bool? Maybe { get; init; }
    }

    public sealed class RequiredFixture
    {
        public required bool Pending { get; init; }

        public required bool? Waiting { get; init; }
    }

    public abstract class AbstractFixture
    {
        public abstract bool Done { get; }
    }

    public class SealedOverrideFixture : AbstractFixture
    {
        public sealed override bool Done => true;
    }

    public struct ReadonlyFixture
    {
        bool _open;

        public ReadonlyFixture(bool open) => _open = open;

        public readonly bool Open => _open;
    }

    public sealed class ByReferenceFixture
    {
        bool _flag;

        public ref bool Locked => ref _flag;

        public ref readonly bool Sealed => ref _flag;
    }

    public sealed class GenericFixture<TFirst, TSecond>
    {
        public bool Both => typeof(TFirst) == typeof(TSecond);
    }

    public sealed class IndexerFixture
    {
        public bool this[int[] indexes] => indexes.Length > 0;
    }

    public sealed class FlowFixture
    {
        [property: DisallowNull]
        public bool? Flowed { get; set; }
    }

    public sealed class PrefixedFixture
    {
        public bool IsFine { get; init; }

        public bool HasLoss { get; init; }

        public bool CanGrow { get; init; }
    }
}
