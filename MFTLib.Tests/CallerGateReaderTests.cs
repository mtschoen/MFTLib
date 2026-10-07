using System.Reflection;
using System.Reflection.Emit;
using MFTLib.Tests.CallerGateFixtures;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using static MFTLib.Tests.CallerGateGeneratedAssembly;
using static MFTLib.Tests.MetadataSurfaceReader;

namespace MFTLib.Tests;

/// <summary>
///     Controls for the caller-gate reader's credit rules. Each positive and negative goes through real IL: a small
///     generated assembly calls the fixture members, the reader collects its references as it does a sample's, and
///     the fixture surface says what those references reach. Every predicate that compiled code can tell apart has a
///     case that fails when that predicate is removed.
/// </summary>
[TestClass]
public class CallerGateReaderTests
{
    const BindingFlags AnyInstance = BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic;

    static readonly string FixtureAssembly = typeof(CallerGateReaderTests).Assembly.Location;

    static Surface FixtureSurface() => ReadSurface(FixtureAssembly, ["MFTLib.Tests.CallerGateFixtures"]);

    static string Name(Type type) => (type.IsGenericType ? type.GetGenericTypeDefinition() : type).FullName!;

    static SurfaceMember Property(Surface surface, Type type, string name) =>
        surface.Members.Single(member => member.Display.StartsWith($"property {Name(type)}.{name} :", StringComparison.Ordinal));

    static SurfaceMember Method(Surface surface, Type type, string signature) =>
        surface.Members.Single(member => member.Display == $"method {Name(type)}.{signature}");

    static Dictionary<string, HashSet<string>> Collect(Dictionary<string, Action<MethodBuilder>> cases)
    {
        var image = Build(cases);
        return cases.Keys.ToDictionary(name => name,
            name => CollectReferencesFromType(image, name, FixtureAssemblyName));
    }

    static ConstructorInfo Constructor(Type type, params Type[] parameters) =>
        type.GetConstructor(AnyInstance, null, parameters, null)!;

    [TestMethod]
    public void AConstructorCreditsAPropertyOnlyWhenItsIlStoresAnArgumentIntoTheBackingField()
    {
        var surface = FixtureSurface();
        var initialized = typeof(CallerGateInitializedRecord);
        var cases = new Dictionary<string, Action<MethodBuilder>>
        {
            ["Initialized"] = Construct(Constructor(initialized, typeof(int), typeof(int))),
            ["Ignored"] = Construct(Constructor(typeof(CallerGateIgnoredRecord), typeof(int))),
            ["Overload"] = Construct(Constructor(typeof(CallerGateOverloadRecord), typeof(int), typeof(bool))),
            ["OtherField"] = Construct(Constructor(typeof(CallerGateOtherFieldRecord), typeof(int))),
            ["Internal"] = Construct(Constructor(typeof(CallerGateInternalConstructorRecord), typeof(int))),
            ["Generic"] = Construct(Constructor(typeof(CallerGateGenericRecord<int>), typeof(int))),
            ["GenericIgnored"] = Construct(Constructor(typeof(CallerGateGenericIgnoredRecord<int>), typeof(int))),
            ["Struct"] = Construct(Constructor(typeof(CallerGateStructRecord), typeof(int))),
            ["StructIgnored"] = Construct(Constructor(typeof(CallerGateStructIgnoredRecord), typeof(int))),
            ["Plain"] = Construct(Constructor(typeof(CallerGatePlainClass), typeof(int))),
            ["Setter"] = Construct(Constructor(typeof(CallerGateSetterRecord), typeof(int))),
            ["AuthoredClass"] = Construct(Constructor(typeof(CallerGateAuthoredEverythingRecord), typeof(int))),
            ["AuthoredStruct"] = Construct(Constructor(typeof(CallerGateAuthoredEverythingStruct), typeof(int))),
            ["Straight"] = Construct(Constructor(typeof(CallerGateStraightRecord), typeof(int))),
            ["DelegatingStore"] = Construct(Constructor(typeof(CallerGateDelegatingStoreRecord), typeof(int), typeof(string))),
            ["Mutated"] = Construct(Constructor(typeof(CallerGateMutatedRecord), typeof(int))),
            ["Overwritten"] = Construct(Constructor(typeof(CallerGateOverwrittenRecord), typeof(int))),
            ["Conditional"] = Construct(Constructor(typeof(CallerGateConditionalRecord), typeof(int), typeof(bool))),
            ["Guarded"] = Construct(Constructor(typeof(CallerGateGuardedRecord), typeof(int))),
            ["Mixed"] = Construct(Constructor(typeof(CallerGateMixedAccessorRecord), typeof(int))),
            ["Sibling"] = Construct(Constructor(typeof(CallerGateCaseSiblingRecord), typeof(int))),
            ["Distinct"] = Construct(Constructor(typeof(CallerGateTypeDistinctRecord), typeof(int)))
        };
        var references = Collect(cases);

        // (case, declaring type, property, credited by constructing the type)
        var expectations = new (string Case, Type Type, string Property, bool Credited)[]
        {
            ("Initialized", initialized, "First", true),
            ("Initialized", initialized, "Second", true),
            ("Initialized", initialized, "Constant", false),
            ("Ignored", typeof(CallerGateIgnoredRecord), "Value", false),
            ("Overload", typeof(CallerGateOverloadRecord), "Value", false),
            ("OtherField", typeof(CallerGateOtherFieldRecord), "Value", false),
            ("Internal", typeof(CallerGateInternalConstructorRecord), "Stored", false),
            ("Generic", typeof(CallerGateGenericRecord<>), "Item", true),
            ("GenericIgnored", typeof(CallerGateGenericIgnoredRecord<>), "Value", false),
            ("Struct", typeof(CallerGateStructRecord), "Amount", true),
            ("StructIgnored", typeof(CallerGateStructIgnoredRecord), "Value", false),
            ("Plain", typeof(CallerGatePlainClass), "Stored", false),
            ("Setter", typeof(CallerGateSetterRecord), "Value", false),
            ("AuthoredClass", typeof(CallerGateAuthoredEverythingRecord), "Id", true),
            ("AuthoredStruct", typeof(CallerGateAuthoredEverythingStruct), "Id", true),
            ("Straight", typeof(CallerGateStraightRecord), "Value", true),
            ("DelegatingStore", typeof(CallerGateDelegatingStoreRecord), "Value", true),
            ("Mutated", typeof(CallerGateMutatedRecord), "Value", false),
            ("Overwritten", typeof(CallerGateOverwrittenRecord), "Value", false),
            ("Conditional", typeof(CallerGateConditionalRecord), "Value", false),
            ("Guarded", typeof(CallerGateGuardedRecord), "Value", false),
            ("Mixed", typeof(CallerGateMixedAccessorRecord), "First", false),
            ("Sibling", typeof(CallerGateCaseSiblingRecord), "first", false),
            ("Distinct", typeof(CallerGateTypeDistinctRecord), "Count", false)
        };
        foreach (var (name, type, propertyName, credited) in expectations)
        {
            var seen = references[name];
            Assert.IsTrue(seen.Any(key => key.StartsWith($"{Name(type)}::.ctor", StringComparison.Ordinal)),
                $"{name}: the generated IL must reference a constructor of {Name(type)}: {string.Join("; ", seen)}");
            Assert.AreEqual(credited, Property(surface, type, propertyName).IsReferencedBy(seen),
                $"{Name(type)}.{propertyName} credited by constructing it");
        }
    }

    [TestMethod]
    public void ARecordPropertyIsCreditedByEitherConstructorStoreOrAnAccessorReference()
    {
        var surface = FixtureSurface();
        var cases = new Dictionary<string, Action<MethodBuilder>>
        {
            ["ConstantGetter"] = Read(typeof(CallerGateInitializedRecord).GetProperty("Constant")!),
            ["HandwrittenGetter"] = Read(typeof(CallerGateReplacedPropertyRecord).GetProperty("Alpha")!),
            ["SiblingGetter"] = Read(typeof(CallerGateCaseSiblingRecord).GetProperty("first")!),
            ["MixedGetter"] = Read(typeof(CallerGateMixedAccessorRecord).GetProperty("First")!)
        };
        var references = Collect(cases);

        Assert.IsTrue(Property(surface, typeof(CallerGateInitializedRecord), "Constant").IsReferencedBy(references["ConstantGetter"]));
        Assert.IsTrue(Property(surface, typeof(CallerGateReplacedPropertyRecord), "Alpha").IsReferencedBy(references["HandwrittenGetter"]));
        Assert.IsTrue(Property(surface, typeof(CallerGateCaseSiblingRecord), "first").IsReferencedBy(references["SiblingGetter"]));
        Assert.IsTrue(Property(surface, typeof(CallerGateMixedAccessorRecord), "First").IsReferencedBy(references["MixedGetter"]));
        Assert.IsFalse(Property(surface, typeof(CallerGateInitializedRecord), "First").IsReferencedBy(references["ConstantGetter"]),
            "reading one property credits no sibling");
    }

    [TestMethod]
    public void AConstrainedObjectCall_CreditsExactlyTheOverrideOfTheConstrainedType()
    {
        var surface = FixtureSurface();
        var ordinary = typeof(CallerGateOrdinaryText);
        var objectType = typeof(object);
        var cases = new Dictionary<string, Action<MethodBuilder>>
        {
            ["ToString"] = ConstrainedCall(ordinary, objectType.GetMethod("ToString")!),
            ["EqualsObject"] = ConstrainedCall(ordinary, objectType.GetMethod("Equals", [typeof(object)])!),
            ["GetHashCode"] = ConstrainedCall(ordinary, objectType.GetMethod("GetHashCode")!),
            ["Interface"] = ConstrainedCall(typeof(CallerGateInterfaceText), typeof(ICallerGateText).GetMethod("ToString")!),
            ["Generic"] = ConstrainedCall(typeof(CallerGateGenericText<int>), objectType.GetMethod("ToString")!),
            ["TypeParameter"] = ConstrainedCallOnTypeParameter(objectType.GetMethod("ToString")!),
            ["Gap"] = ConstrainedCall(ordinary, objectType.GetMethod("ToString")!, gap: true),
            ["HiddenConstrained"] = ConstrainedCall(typeof(CallerGateHiddenText), objectType.GetMethod("ToString")!),
            ["HiddenVirtual"] = ConstrainedCall(typeof(CallerGateHiddenVirtualText), objectType.GetMethod("ToString")!),
            ["DerivedToString"] = ConstrainedCall(typeof(CallerGateHiddenDerived), objectType.GetMethod("ToString")!),
            ["DerivedEquals"] = ConstrainedCall(typeof(CallerGateHiddenDerived), objectType.GetMethod("Equals", [typeof(object)])!),
            ["DerivedHashCode"] = ConstrainedCall(typeof(CallerGateHiddenDerived), objectType.GetMethod("GetHashCode")!),
            ["PlainDerived"] = ConstrainedCall(typeof(CallerGatePlainDerived), objectType.GetMethod("ToString")!),
            ["PlainBase"] = ConstrainedCall(typeof(CallerGatePlainBase), objectType.GetMethod("ToString")!),
            ["PlainEquals"] = ConstrainedCall(typeof(CallerGatePlainDerived), objectType.GetMethod("Equals", [typeof(object)])!),
            ["GenericChild"] = ConstrainedCall(typeof(CallerGateGenericChild), objectType.GetMethod("ToString")!),
            ["ForeignBase"] = ConstrainedCall(typeof(CallerGateForeignBaseText), objectType.GetMethod("ToString")!),
            ["HiddenDirect"] = CallDirect(typeof(CallerGateHiddenText).GetMethod("ToString", Type.EmptyTypes)!),
            ["Foreign"] = ConstrainedCall(typeof(DateTime), objectType.GetMethod("ToString")!),
            ["NoOverride"] = ConstrainedCall(typeof(CallerGateNoOverrideText), objectType.GetMethod("ToString")!)
        };
        var references = Collect(cases);

        var toString = Method(surface, ordinary, "ToString() : System.String");
        var equalsObject = Method(surface, ordinary, "Equals(System.Object) : System.Boolean");
        var equalsTyped = Method(surface, ordinary, $"Equals({Name(ordinary)}) : System.Boolean");
        var hashCode = Method(surface, ordinary, "GetHashCode() : System.Int32");

        Assert.IsTrue(toString.IsReferencedBy(references["ToString"]));
        Assert.IsFalse(equalsObject.IsReferencedBy(references["ToString"]) || hashCode.IsReferencedBy(references["ToString"]));
        Assert.IsTrue(equalsObject.IsReferencedBy(references["EqualsObject"]));
        Assert.IsFalse(equalsTyped.IsReferencedBy(references["EqualsObject"]), "a constrained Object.Equals is not Equals(T)");
        Assert.IsFalse(toString.IsReferencedBy(references["EqualsObject"]));
        Assert.IsTrue(hashCode.IsReferencedBy(references["GetHashCode"]));

        var interfaceOverride = Method(surface, typeof(CallerGateInterfaceText), "ToString() : System.String");
        Assert.IsFalse(interfaceOverride.IsReferencedBy(references["Interface"]), "an interface callee dispatches elsewhere");

        var genericOverride = Method(surface, typeof(CallerGateGenericText<>), "ToString() : System.String");
        Assert.IsTrue(genericOverride.IsReferencedBy(references["Generic"]), "a closed generic constraint resolves to its definition");

        Assert.IsFalse(references["TypeParameter"].Any(key => key.Contains('!') || key.Contains("::ToString", StringComparison.Ordinal)),
            "a type-parameter constraint names no type: " + string.Join("; ", references["TypeParameter"]));
        var hidden = Method(surface, typeof(CallerGateHiddenText), "ToString() : System.String");
        Assert.IsFalse(hidden.IsReferencedBy(references["HiddenConstrained"]), "a method that hides Object.ToString is not what the constrained call runs");
        var hiddenVirtual = Method(surface, typeof(CallerGateHiddenVirtualText), "ToString() : System.String");
        Assert.IsFalse(hiddenVirtual.IsReferencedBy(references["HiddenVirtual"]), "a virtual method on a new slot does not override either");
        Assert.IsFalse(Method(surface, typeof(CallerGateHiddenDerived), "ToString() : System.String").IsReferencedBy(references["DerivedToString"]),
            "an override of a hiding slot is not the Object override");
        Assert.IsFalse(Method(surface, typeof(CallerGateHiddenDerived), "Equals(System.Object) : System.Boolean").IsReferencedBy(references["DerivedEquals"]));
        Assert.IsFalse(Method(surface, typeof(CallerGateHiddenDerived), "GetHashCode() : System.Int32").IsReferencedBy(references["DerivedHashCode"]));
        Assert.IsTrue(Method(surface, typeof(CallerGatePlainDerived), "ToString() : System.String").IsReferencedBy(references["PlainDerived"]),
            "an override chain with no hiding reaches the Object slot");
        Assert.IsTrue(Method(surface, typeof(CallerGatePlainDerived), "Equals(System.Object) : System.Boolean").IsReferencedBy(references["PlainEquals"]),
            "a new-slot method of another signature does not hide Equals(object)");
        Assert.IsFalse(Method(surface, typeof(CallerGateGenericChild), "ToString() : System.String").IsReferencedBy(references["GenericChild"]),
            "a generic base cannot be followed to Object, so it is refused");
        Assert.IsTrue(Method(surface, typeof(CallerGatePlainBase), "ToString() : System.String").IsReferencedBy(references["PlainBase"]));
        Assert.IsFalse(Method(surface, typeof(CallerGateForeignBaseText), "ToString() : System.String").IsReferencedBy(references["ForeignBase"]),
            "a base in another assembly cannot be followed to Object, so it is refused");
        Assert.IsTrue(hidden.IsReferencedBy(references["HiddenDirect"]), "a direct call of the hiding method still credits it");
        Assert.AreEqual(0, references["Foreign"].Count, "a type outside the library is not the library's override: " + string.Join("; ", references["Foreign"]));
        Assert.IsFalse(toString.IsReferencedBy(references["Gap"]), "the prefix covers only the next instruction");
        Assert.IsFalse(surface.Members.Any(member => member.Display.StartsWith($"method {Name(typeof(CallerGateNoOverrideText))}.ToString", StringComparison.Ordinal)),
            "a type with no override has no member for the call to credit");
    }

    [TestMethod]
    public void TheSurfaceKeys_AreUniqueOnTheFixturesAndFireOnFunctionPointerOverloads()
    {
        Assert.AreEqual(0, DuplicateKeys(FixtureSurface()).Count, string.Join("; ", DuplicateKeys(FixtureSurface())));

        var pointers = ReadSurface(FixtureAssembly, ["MFTLib.Tests.CallerGateFixturePointers"]);
        var duplicates = DuplicateKeys(pointers);
        Assert.AreEqual(1, duplicates.Count, string.Join("; ", pointers.Members.Select(member => member.Display)));
        StringAssert.Contains(duplicates[0], "Choose");
    }

    [TestMethod]
    public void AnAttributeNamedLikeCompilerGenerated_HidesNothing()
    {
        var surface = ReadSurfaceFromImage(BuildMarkedLibrary(), [MarkedNamespace]);
        var displays = surface.Members.Select(member => member.Display).ToList();
        string Report() => string.Join("; ", displays);

        Assert.IsTrue(displays.Contains($"type {MarkedNamespace}.DecoyedType"), Report());
        Assert.IsTrue(displays.Contains($"method {MarkedNamespace}.Marked.DecoyedMethod() : System.Void"), Report());
        Assert.IsFalse(displays.Contains($"type {MarkedNamespace}.RealType"), "the real attribute still hides: " + Report());
        Assert.IsFalse(displays.Contains($"method {MarkedNamespace}.Marked.RealMethod() : System.Void"), "the real attribute still hides: " + Report());
    }

    [TestMethod]
    public void AConstructorStoringThis_IsNotStoringAnArgument()
    {
        var surface = ReadSurfaceFromImage(BuildHandWrittenConstructors(), [HandNamespace]);
        SurfaceMember Value(string type) => surface.Members.Single(member => member.Display == $"property {HandNamespace}.{type}.Value : System.Int32");

        Assert.AreEqual(1, Value("Argument").ConstructorKeys.Count, "the positive twin proves the hand-built shape is read as a record");
        Assert.AreEqual(0, Value("ThisShort").ConstructorKeys.Count, "ldarg.s 0 is this");
        Assert.AreEqual(0, Value("ThisLong").ConstructorKeys.Count, "ldarg 0 is this");
        Assert.AreEqual(0, Value("StargLong").ConstructorKeys.Count, "a long-form starg overwrites the argument");
    }
}
