using System.Reflection;
using System.Runtime.CompilerServices;
using MFTLib.Index;
using MFTLibTestExtensions;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace MFTLib.Tests;

/// <summary>
///     Pins the public surface of the MFTLib assembly (the MFTLib and MFTLib.Index namespaces) and
///     the MFTLibTestExtensions assembly (the MFTLib.TestExtensions package) to checked-in approved
///     files. A member is public only when a consumer needs it in production code; see
///     docs/architecture.md. Approved files are copied to the test output directory by the csproj.
///     <para>
///         Detection contract: the gate pins the set of public and protected types and members,
///         each with its signature: the declaring type with every nesting level's own generic
///         arguments, the name, the parameter and return types, and the nullable annotations the
///         compiler records on them. Adding or removing a type or member, or changing a
///         signature, fails the test.
///     </para>
///     <para>
///         Modifier and annotation rendering covers the constructs these assemblies use today.
///         A construct the formatter does not render still appears as a line when it is
///         introduced, so its arrival is reviewable, and rendering for it is added when MFTLib
///         first uses it. Not rendered today: type-parameter variance, function pointers,
///         volatile fields, scoped parameters, control characters in literals, and nullability
///         on base types, interface implementations, generic constraints and accessor flow
///         attributes. That is a stated limit of the method, not a defect.
///     </para>
/// </summary>
[TestClass]
public class PublicSurfaceTests
{
    const string RegenerationVariable = "MFTLIB_PUBLIC_SURFACE_REGENERATE_TO";
    const string IndexNamespace = "MFTLib.Index";

    [TestMethod]
    public void PublicSurface_MatchesTheApprovedList()
    {
        var library = PublicSurfaceEnumerator.TopLevelTypes(typeof(FileIndex).Assembly).ToArray();
        var surfaces = new (string FileName, string[] Lines)[]
        {
            ("MFTLib.approved.txt", PublicSurfaceEnumerator.Enumerate(library.Where(type => !IsIndexType(type)))),
            ("MFTLib.Index.approved.txt", PublicSurfaceEnumerator.Enumerate(library.Where(IsIndexType))),
            ("MFTLibTestExtensions.approved.txt",
                PublicSurfaceEnumerator.Enumerate(PublicSurfaceEnumerator.TopLevelTypes(typeof(BrokerTestHarness).Assembly)))
        };

        var regenerateTo = Environment.GetEnvironmentVariable(RegenerationVariable);
        if (!string.IsNullOrEmpty(regenerateTo))
        {
            Directory.CreateDirectory(regenerateTo);
            foreach (var (fileName, lines) in surfaces)
            {
                File.WriteAllText(Path.Combine(regenerateTo, fileName), string.Join('\n', lines) + "\n");
            }

            Assert.Fail($"{RegenerationVariable} is set: wrote the current surface to {regenerateTo}. " +
                        "Review the diff and copy the files into MFTLib.Tests/PublicSurface/ by hand.");
        }

        var failures = surfaces
            .Select(surface => Mismatch(surface.FileName, surface.Lines))
            .Where(failure => failure is not null)
            .ToArray();
        if (failures.Length > 0)
        {
            Assert.Fail(string.Join("\n\n", failures));
        }
    }

    /// <summary>
    ///     Mandatory negative control. The enumerator, run over fixtures declared in this assembly,
    ///     must report public and protected members and nested types and omit internal, private
    ///     and private protected ones; must report every synthesized record member a consumer can
    ///     call; must keep each nesting level's own generic arguments; must carry nullable
    ///     annotations on generic parameters; and must not drop types in System namespaces.
    ///     Without it an empty or filtered surface would match an empty approved file.
    /// </summary>
    [TestMethod]
    public void PublicSurface_NegativeControl()
    {
        CollectionAssert.AreEqual(new[]
        {
            "MFTLib.Tests.PublicSurfaceFixture :: constructor public ()",
            "MFTLib.Tests.PublicSurfaceFixture :: method protected internal static ProtectedInternalMethod() : void",
            "MFTLib.Tests.PublicSurfaceFixture :: method protected static ProtectedMethod() : void",
            "MFTLib.Tests.PublicSurfaceFixture :: method public static PublicMethod() : void",
            "MFTLib.Tests.PublicSurfaceFixture :: property public PublicProperty { get; protected set; } : int",
            "MFTLib.Tests.PublicSurfaceFixture :: type public class",
            "MFTLib.Tests.PublicSurfaceFixture.NestedProtected :: constructor public ()",
            "MFTLib.Tests.PublicSurfaceFixture.NestedProtected :: type protected class",
            "MFTLib.Tests.PublicSurfaceFixture.NestedPublic :: constructor public ()",
            "MFTLib.Tests.PublicSurfaceFixture.NestedPublic :: type public class"
        }, Surface(typeof(PublicSurfaceFixture)));

        const string sealedRecord = "MFTLib.Tests.PublicSurfaceRecordFixture";
        AssertContains(Surface(typeof(PublicSurfaceRecordFixture)),
            $"{sealedRecord} :: method public Deconstruct(out int Value) : void",
            $"{sealedRecord} :: method public static op_Equality({sealedRecord}? left, {sealedRecord}? right) : bool",
            $"{sealedRecord} :: method public static op_Inequality({sealedRecord}? left, {sealedRecord}? right) : bool",
            $"{sealedRecord} :: method public <Clone>$() : {sealedRecord}",
            $"{sealedRecord} :: method public Equals({sealedRecord}? other) : bool",
            $"{sealedRecord} :: method public override Equals(object? obj) : bool",
            $"{sealedRecord} :: type public sealed class : System.IEquatable<{sealedRecord}>");

        const string openRecord = "MFTLib.Tests.OpenSurfaceRecordFixture";
        AssertContains(Surface(typeof(OpenSurfaceRecordFixture)),
            $"{openRecord} :: constructor protected ({openRecord} original)",
            $"{openRecord} :: method public virtual <Clone>$() : {openRecord}",
            $"{openRecord} :: method protected virtual PrintMembers(System.Text.StringBuilder builder) : bool",
            $"{openRecord} :: property protected virtual EqualityContract {{ get; }} : System.Type");

        const string nested = "MFTLib.Tests.GenericSurfaceOuter<TOuter>.NestedRecord<TValue>";
        var genericOuter = Surface(typeof(GenericSurfaceOuter<>));
        AssertContains(genericOuter,
            $"{nested} :: constructor protected ({nested} original)",
            $"{nested} :: method public virtual <Clone>$() : {nested}",
            $"{nested} :: method public static op_Equality({nested}? left, {nested}? right) : bool",
            $"{nested} :: method public static op_Inequality({nested}? left, {nested}? right) : bool",
            $"{nested} :: method public Deconstruct(out TValue Value) : void",
            "MFTLib.Tests.GenericSurfaceOuter<TOuter>.SiblingRecord<TValue> :: method public virtual <Clone>$() : " +
            "MFTLib.Tests.GenericSurfaceOuter<TOuter>.SiblingRecord<TValue>");
        Assert.IsFalse(genericOuter.Any(line => line.Contains("GenericSurfaceOuter<TOuter, TValue>", StringComparison.Ordinal)),
            "nested generic types must keep each level's own arguments:\n" + string.Join('\n', genericOuter));

        AssertContains(Surface(typeof(NullableSurfaceRecord<>)),
            "MFTLib.Tests.NullableSurfaceRecord<T> :: method public Deconstruct(out T? Value) : void",
            "MFTLib.Tests.NullableSurfaceRecord<T> :: type public sealed class : " +
            "System.IEquatable<MFTLib.Tests.NullableSurfaceRecord<T>> where T : class");
        AssertContains(Surface(typeof(UnconstrainedSurfaceRecord<>)),
            "MFTLib.Tests.UnconstrainedSurfaceRecord<T> :: method public Deconstruct" +
            "(out T Plain, out T? Annotated, out System.Collections.Generic.List<T?> Items) : void",
            "MFTLib.Tests.UnconstrainedSurfaceRecord<T> :: type public sealed class : " +
            "System.IEquatable<MFTLib.Tests.UnconstrainedSurfaceRecord<T>>");

        CollectionAssert.Contains(PublicSurfaceEnumerator.TopLevelTypes(typeof(PublicSurfaceFixture).Assembly).ToArray(),
            typeof(SurfaceProbeExtensions));
        AssertContains(Surface(typeof(SurfaceProbeExtensions)),
            "System.IO.SurfaceProbeExtensions :: method public static ProbeExtension(this string value) : void",
            "System.IO.SurfaceProbeExtensions :: type public static class");

        AssertFixturesRoundTrip();
    }

    /// <summary>The fixtures are ordinary records: each positional value reads back as constructed.</summary>
    static void AssertFixturesRoundTrip()
    {
        Assert.AreEqual(1, new PublicSurfaceRecordFixture(1).Value);
        Assert.AreEqual(1, new OpenSurfaceRecordFixture(1).Value);
        Assert.AreEqual(1, new GenericSurfaceOuter<string>.NestedRecord<int>(1).Value);
        Assert.AreEqual(1, new GenericSurfaceOuter<string>.SiblingRecord<int>(1).Value);
        Assert.AreEqual("outer", new GenericSurfaceOuter<string> { Current = "outer" }.Current);
        Assert.IsNull(new NullableSurfaceRecord<string>(null).Value);
        var unconstrained = new UnconstrainedSurfaceRecord<string>("plain", null, ["item"]);
        Assert.AreEqual("plain", unconstrained.Plain);
        Assert.IsNull(unconstrained.Annotated);
        Assert.AreEqual("item", unconstrained.Items.Single());
        PublicSurfaceFixture.PublicMethod();
    }

    [TestMethod]
    public void InternalsVisibleTo_NamesOnlyMFTLibAssemblies()
    {
        var names = typeof(FileIndex).Assembly.GetCustomAttributes<InternalsVisibleToAttribute>()
            .Select(attribute => attribute.AssemblyName)
            .OrderBy(name => name, StringComparer.Ordinal)
            .ToArray();

        CollectionAssert.AreEqual(new[] { "Benchmark", "MFTLib.Tests", "MFTLibTestExtensions" }, names);
    }

    /// <summary>
    ///     MFTLib.Index and its child namespaces go to the Index file; every other namespace goes to
    ///     the MFTLib file, the closest parent.
    /// </summary>
    static bool IsIndexType(Type type) =>
        type.Namespace == IndexNamespace ||
        (type.Namespace?.StartsWith(IndexNamespace + ".", StringComparison.Ordinal) ?? false);

    static string[] Surface(Type type) => PublicSurfaceEnumerator.Enumerate([type]);

    static void AssertContains(string[] surface, params string[] expected)
    {
        var missing = expected.Where(line => !surface.Contains(line, StringComparer.Ordinal)).ToArray();
        if (missing.Length > 0)
        {
            Assert.Fail($"Missing ({missing.Length}):\n{string.Join('\n', missing)}\n" +
                        $"Surface:\n{string.Join('\n', surface)}");
        }
    }

    /// <summary>
    ///     Compares one surface with its approved file, ignoring line endings and blank lines, and
    ///     returns a message naming every missing and unexpected line, or null when they match.
    /// </summary>
    static string? Mismatch(string fileName, string[] current)
    {
        var approvedPath = Path.Combine(AppContext.BaseDirectory, "PublicSurface", fileName);
        var approved = File.ReadAllText(approvedPath)
            .Split('\n')
            .Select(line => line.TrimEnd('\r'))
            .Where(line => line.Length > 0)
            .ToArray();
        if (approved.SequenceEqual(current, StringComparer.Ordinal))
        {
            return null;
        }

        var unexpected = current.Except(approved, StringComparer.Ordinal).ToArray();
        var missing = approved.Except(current, StringComparer.Ordinal).ToArray();
        return $"The public surface no longer matches {fileName}. If this change is intended, update " +
               $"the approved file in the same pull request (set {RegenerationVariable} to regenerate).\n" +
               $"Not in the approved file ({unexpected.Length}):\n{string.Join('\n', unexpected)}\n" +
               $"Approved but no longer present ({missing.Length}):\n{string.Join('\n', missing)}" +
               (unexpected.Length + missing.Length == 0
                   ? "\nOnly the order or duplicate lines differ; the approved file must be sorted ordinally."
                   : string.Empty);
    }
}
