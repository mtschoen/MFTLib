using System.Reflection;
using System.Reflection.Metadata;
using System.Reflection.Metadata.Ecma335;
using System.Reflection.PortableExecutable;

namespace MFTLib.Tests;

/// <summary>
///     Reads compiled assemblies without loading them, for the public-member caller gate. One side lists
///     the authored public members of MFTLib and MFTLib.Index; the other lists which of them a consumer
///     assembly names in its IL. Both sides key a member by declaring type full name, member name and a
///     signature decoded by the same <see cref="SignatureProvider" />, so overloads stay apart.
/// </summary>
internal static partial class MetadataSurfaceReader
{
    public const string LibraryAssemblyName = "MFTLib";

    static readonly string[] SurfaceNamespaces = ["MFTLib", "MFTLib.Index"];

    /// <summary>
    ///     The members that need a caller, the literal fields whose use the compiler inlines, and the exposed
    ///     top-level types that sit outside the surface namespaces and so would escape the gate unseen.
    /// </summary>
    public sealed record Surface(
        IReadOnlyList<SurfaceMember> Members, IReadOnlyList<string> LiteralFields, IReadOnlyList<string> OutOfScopeTypes);

    /// <summary>
    ///     One thing that needs a caller: a type, or a member whose use leaves any one of <see cref="Keys" /> in IL. A
    ///     record's automatic property is also reached through a public constructor whose own straight-line IL stores an
    ///     argument into its backing field (<see cref="ConstructorKeys" />), and an override of an Object virtual
    ///     through a constrained Object call (<see cref="OverrideKeys" />).
    /// </summary>
    public sealed record SurfaceMember(string Display, IReadOnlyList<string> Keys)
    {
        public IReadOnlyList<string> ConstructorKeys { get; init; } = [];

        /// <summary>Set only for a true override of an Object virtual: what a constrained Object call reaches.</summary>
        public IReadOnlyList<string> OverrideKeys { get; init; } = [];

        public bool IsReferencedBy(IReadOnlySet<string> references) =>
            Keys.Any(references.Contains) || ConstructorKeys.Any(references.Contains) ||
            OverrideKeys.Any(references.Contains);
    }

    /// <summary>
    ///     Every key that more than one surface member carries. A function pointer renders as `fnptr`, so two overloads
    ///     that differ only there would share a key and one caller would credit both; the gate fails on any such key.
    /// </summary>
    public static IReadOnlyList<string> DuplicateKeys(Surface surface) =>
        surface.Members.SelectMany(member => member.Keys).GroupBy(key => key, StringComparer.Ordinal)
            .Where(group => group.Count() > 1).Select(group => group.Key).Order(StringComparer.Ordinal).ToList();

    public static string TypeKey(string typeFullName) => "type " + typeFullName;

    public static string MemberKey(string typeFullName, string memberName, string signature) =>
        $"{typeFullName}::{memberName}{signature}";

    /// <summary>The identities of every library type and member named by the IL of the given assembly files.</summary>
    public static HashSet<string> CollectReferences(IEnumerable<string> assemblyPaths,
        string libraryName = LibraryAssemblyName)
    {
        var references = new HashSet<string>(StringComparer.Ordinal);
        foreach (var path in assemblyPaths)
        {
            using var peReader = new PEReader(new MemoryStream(File.ReadAllBytes(path)));
            CollectReferences(peReader, references, libraryName);
        }

        return references;
    }

    /// <summary>The same collection over an assembly image held in memory, naming the assembly it treats as the library.</summary>
    public static HashSet<string> CollectReferencesFromImage(byte[] image, string libraryName)
    {
        var references = new HashSet<string>(StringComparer.Ordinal);
        using var peReader = new PEReader(new MemoryStream(image));
        CollectReferences(peReader, references, libraryName);
        return references;
    }

    /// <summary>
    ///     The library members named by the method bodies of one type of an assembly file, found by walking its IL.
    ///     A whole test assembly names nearly every overload somewhere; one fixture type names only what it calls.
    /// </summary>
    public static HashSet<string> CollectReferencesFromType(string assemblyPath, string typeFullName) =>
        CollectReferencesFromType(File.ReadAllBytes(assemblyPath), typeFullName, LibraryAssemblyName);

    /// <summary>The same walk over an assembly image held in memory, naming the assembly it treats as the library.</summary>
    public static HashSet<string> CollectReferencesFromType(byte[] image, string typeFullName, string libraryName)
    {
        using var peReader = new PEReader(new MemoryStream(image));
        var reader = peReader.GetMetadataReader();
        var provider = new SignatureProvider(eraseInstantiation: false, libraryName);
        var erasing = new SignatureProvider(eraseInstantiation: true, libraryName);
        var references = new HashSet<string>(StringComparer.Ordinal);
        var type = reader.GetTypeDefinition(reader.TypeDefinitions
            .Single(candidate => provider.GetTypeFromDefinition(reader, candidate, 0) == typeFullName));
        foreach (var methodHandle in type.GetMethods())
        {
            var method = reader.GetMethodDefinition(methodHandle);
            if (method.RelativeVirtualAddress == 0)
            {
                continue;
            }

            var body = peReader.GetMethodBody(method.RelativeVirtualAddress).GetILBytes()!;
            AddConstrainedReferences(reader, body, provider, erasing, references);
            foreach (var token in InstructionTokens(body))
            {
                var handle = MetadataTokens.EntityHandle(token);
                if (handle.Kind == HandleKind.MethodSpecification)
                {
                    handle = reader.GetMethodSpecification((MethodSpecificationHandle)handle).Method;
                }

                if (handle.Kind == HandleKind.MemberReference)
                {
                    AddMemberReference(reader, reader.GetMemberReference((MemberReferenceHandle)handle), provider, erasing,
                        references);
                }
            }
        }

        return references;
    }

    static void CollectReferences(PEReader peReader, HashSet<string> references, string libraryName)
    {
        var reader = peReader.GetMetadataReader();
        var provider = new SignatureProvider(eraseInstantiation: false, libraryName);
        var erasing = new SignatureProvider(eraseInstantiation: true, libraryName);

        foreach (var handle in reader.TypeReferences)
        {
            if (IsLibraryScope(reader, reader.GetTypeReference(handle), libraryName))
            {
                references.Add(TypeKey(provider.GetTypeFromReference(reader, handle, 0)));
            }
        }

        foreach (var handle in reader.MemberReferences)
        {
            AddMemberReference(reader, reader.GetMemberReference(handle), provider, erasing, references);
        }

        foreach (var method in reader.MethodDefinitions.Select(reader.GetMethodDefinition)
                     .Where(method => method.RelativeVirtualAddress != 0))
        {
            AddConstrainedReferences(reader, peReader.GetMethodBody(method.RelativeVirtualAddress).GetILBytes()!,
                provider, erasing, references);
        }

        for (var row = 1; row <= reader.GetTableRowCount(TableIndex.MethodSpec); row++)
        {
            var method = reader.GetMethodSpecification(MetadataTokens.MethodSpecificationHandle(row)).Method;
            if (method.Kind == HandleKind.MemberReference)
            {
                AddMemberReference(reader, reader.GetMemberReference((MemberReferenceHandle)method), provider, erasing,
                    references);
            }
        }
    }

    static bool IsLibraryScope(MetadataReader reader, TypeReference type, string libraryName)
    {
        var scope = type.ResolutionScope;
        while (scope.Kind == HandleKind.TypeReference)
        {
            scope = reader.GetTypeReference((TypeReferenceHandle)scope).ResolutionScope;
        }

        return scope.Kind == HandleKind.AssemblyReference &&
               reader.GetString(reader.GetAssemblyReference((AssemblyReferenceHandle)scope).Name) == libraryName;
    }

    static void AddMemberReference(MetadataReader reader, MemberReference member, SignatureProvider provider,
        SignatureProvider erasing, HashSet<string> references)
    {
        var declaringType = DeclaringLibraryType(reader, member, provider, erasing);
        if (declaringType is null)
        {
            return;
        }

        var signature = member.GetKind() == MemberReferenceKind.Field
            ? ":" + member.DecodeFieldSignature(provider, null)
            : FormatMethod(member.DecodeMethodSignature(provider, null));
        references.Add(MemberKey(declaringType, reader.GetString(member.Name), signature));
    }

    // A member of a generic instance has a TypeSpecification parent; erase the instantiation to reach the definition.
    static string? DeclaringLibraryType(MetadataReader reader, MemberReference member, SignatureProvider provider,
        SignatureProvider erasing)
    {
        if (member.Parent.Kind == HandleKind.TypeReference)
        {
            var parent = (TypeReferenceHandle)member.Parent;
            return IsLibraryScope(reader, reader.GetTypeReference(parent), provider.LibraryName)
                ? provider.GetTypeFromReference(reader, parent, 0)
                : null;
        }

        if (member.Parent.Kind != HandleKind.TypeSpecification)
        {
            return null;
        }

        var decoded = reader.GetTypeSpecification((TypeSpecificationHandle)member.Parent).DecodeSignature(erasing, null);
        return erasing.LibraryTypes.Contains(decoded) ? decoded : null;
    }

    static string FormatMethod(MethodSignature<string> signature) =>
        $"`{signature.GenericParameterCount}({string.Join(",", signature.ParameterTypes)}):{signature.ReturnType}";

    /// <summary>Reads the authored public and protected surface of the library assembly file.</summary>
    public static Surface ReadSurface(string libraryPath, IReadOnlyCollection<string>? surfaceNamespaces = null) =>
        ReadSurfaceFromImage(File.ReadAllBytes(libraryPath), surfaceNamespaces);

    /// <summary>The same read over an assembly image held in memory.</summary>
    public static Surface ReadSurfaceFromImage(byte[] image, IReadOnlyCollection<string>? surfaceNamespaces = null)
    {
        surfaceNamespaces ??= SurfaceNamespaces;
        using var peReader = new PEReader(new MemoryStream(image));
        var reader = peReader.GetMetadataReader();
        var provider = new SignatureProvider(eraseInstantiation: false);
        var members = new List<SurfaceMember>();
        var literals = new List<string>();
        var outOfScope = new List<string>();
        foreach (var handle in reader.TypeDefinitions)
        {
            var type = reader.GetTypeDefinition(handle);
            if (!type.GetDeclaringType().IsNil || !IsExposed(reader, type))
            {
                continue;
            }

            if (surfaceNamespaces.Contains(reader.GetString(type.Namespace)))
            {
                AddType(peReader, reader, provider, handle, members, literals);
            }
            else
            {
                outOfScope.Add(provider.GetTypeFromDefinition(reader, handle, 0));
            }
        }

        return new Surface(members, literals, outOfScope);
    }

    static bool IsExposed(MetadataReader reader, TypeDefinition type)
    {
        if (IsCompilerGenerated(reader, type.GetCustomAttributes()))
        {
            return false;
        }

        var visibility = type.Attributes & TypeAttributes.VisibilityMask;
        if (visibility == TypeAttributes.Public)
        {
            return true;
        }

        var nested = visibility is TypeAttributes.NestedPublic or TypeAttributes.NestedFamily or
            TypeAttributes.NestedFamORAssem;
        return nested && IsExposed(reader, reader.GetTypeDefinition(type.GetDeclaringType()));
    }

    static bool IsExposed(MethodAttributes attributes) =>
        (attributes & MethodAttributes.MemberAccessMask) is MethodAttributes.Public or MethodAttributes.Family or
        MethodAttributes.FamORAssem;

    static bool IsCompilerGenerated(MetadataReader reader, CustomAttributeHandleCollection attributes)
    {
        foreach (var handle in attributes)
        {
            var constructor = reader.GetCustomAttribute(handle).Constructor;
            if (constructor.Kind != HandleKind.MemberReference)
            {
                continue;
            }

            var parent = reader.GetMemberReference((MemberReferenceHandle)constructor).Parent;
            if (parent.Kind != HandleKind.TypeReference)
            {
                continue;
            }

            var attribute = reader.GetTypeReference((TypeReferenceHandle)parent);
            if (reader.GetString(attribute.Namespace) == "System.Runtime.CompilerServices" &&
                reader.GetString(attribute.Name) == "CompilerGeneratedAttribute")
            {
                return true;
            }
        }

        return false;
    }

    static bool IsEnum(MetadataReader reader, TypeDefinition type) =>
        type.BaseType.Kind == HandleKind.TypeReference &&
        reader.GetString(reader.GetTypeReference((TypeReferenceHandle)type.BaseType).Name) == "Enum";

    static void AddType(PEReader peReader, MetadataReader reader, SignatureProvider provider, TypeDefinitionHandle handle,
        List<SurfaceMember> members, List<string> literals)
    {
        var type = reader.GetTypeDefinition(handle);
        var typeName = provider.GetTypeFromDefinition(reader, handle, 0);
        members.Add(new SurfaceMember("type " + typeName, [TypeKey(typeName)]));

        foreach (var nested in type.GetNestedTypes())
        {
            if (IsExposed(reader, reader.GetTypeDefinition(nested)))
            {
                AddType(peReader, reader, provider, nested, members, literals);
            }
        }

        var isRecord = IsRecord(reader, type);
        var primaryParameters = isRecord ? PrimaryParameters(reader, provider, type) : [];

        var accessors = new HashSet<MethodDefinitionHandle>();
        AddProperties(peReader, reader, provider, type, typeName, isRecord, accessors, members);
        AddEvents(reader, provider, type, typeName, accessors, members);
        AddMethods(reader, provider, type, typeName, primaryParameters, accessors, members);
        AddFields(reader, provider, type, typeName, IsEnum(reader, type), members, literals);
    }

    // Display text is the exempt-list key, so it carries everything that tells siblings apart: generic arity,
    // parameter types and the result type (a property carries its index parameters and its type).
    static string MethodDisplay(string typeName, string name, MethodSignature<string> signature) =>
        $"method {typeName}.{name}{(signature.GenericParameterCount > 0 ? $"<{signature.GenericParameterCount}>" : string.Empty)}" +
        $"({string.Join(", ", signature.ParameterTypes)}) : {signature.ReturnType}";

    static string PropertyDisplay(MetadataReader reader, SignatureProvider provider, string typeName, string name,
        MethodDefinitionHandle accessor)
    {
        var signature = reader.GetMethodDefinition(accessor).DecodeSignature(provider, null);
        var isSetter = signature.ReturnType == "System.Void";
        var parameters = isSetter ? signature.ParameterTypes.RemoveAt(signature.ParameterTypes.Length - 1) : signature.ParameterTypes;
        var type = isSetter ? signature.ParameterTypes[^1] : signature.ReturnType;
        return $"property {typeName}.{name}{(parameters.Length > 0 ? $"[{string.Join(", ", parameters)}]" : string.Empty)} : {type}";
    }

    static void AddEvents(MetadataReader reader, SignatureProvider provider, TypeDefinition type, string typeName,
        HashSet<MethodDefinitionHandle> accessors, List<SurfaceMember> members)
    {
        foreach (var handle in type.GetEvents())
        {
            var definition = reader.GetEventDefinition(handle);
            var pair = definition.GetAccessors();
            var present = new[] { pair.Adder, pair.Remover }.Where(accessor => !accessor.IsNil).ToList();
            accessors.UnionWith(present);
            if (present.Any(accessor => IsExposed(reader.GetMethodDefinition(accessor).Attributes)))
            {
                members.Add(new SurfaceMember($"event {typeName}.{reader.GetString(definition.Name)}",
                    present.Select(accessor => MethodKey(reader, provider, typeName, accessor)).ToList()));
            }
        }
    }

    static void AddMethods(MetadataReader reader, SignatureProvider provider, TypeDefinition type, string typeName,
        IReadOnlyList<PrimaryParameter> primaryParameters, HashSet<MethodDefinitionHandle> accessors,
        List<SurfaceMember> members)
    {
        foreach (var handle in type.GetMethods())
        {
            var method = reader.GetMethodDefinition(handle);
            var name = reader.GetString(method.Name);
            if (accessors.Contains(handle) || !IsExposed(method.Attributes) ||
                IsCompilerGenerated(reader, method.GetCustomAttributes()) ||
                IsPrimaryConstructor(reader, provider, method, name, primaryParameters))
            {
                continue;
            }

            var key = MethodKey(reader, provider, typeName, handle);
            members.Add(new SurfaceMember(
                MethodDisplay(typeName, name, method.DecodeSignature(provider, null)), [key])
            {
                OverrideKeys = OverridesObjectSlot(reader, provider, type, method, name) ? [DispatchKey(key)] : []
            });
        }
    }

    // The constrained call reaches the method only when it reuses a slot that provably starts at System.Object: it is
    // virtual without a new slot, and walking up the base chain no ancestor introduces a method of the same name and
    // signature on a new slot (a `new virtual` hides Object's, and an override of that reuses the hiding slot). A base
    // from another assembly, other than Object, ValueType and Enum, or a generic base, cannot be followed and is refused.
    static bool OverridesObjectSlot(MetadataReader reader, SignatureProvider provider, TypeDefinition type,
        MethodDefinition method, string name)
    {
        if (!ObjectVirtualMethods.Contains(name) || (method.Attributes & MethodAttributes.Virtual) == 0 ||
            (method.Attributes & MethodAttributes.NewSlot) != 0)
        {
            return false;
        }

        var signature = method.DecodeSignature(provider, null);
        var baseType = type.BaseType;
        while (true)
        {
            switch (baseType.Kind)
            {
                case HandleKind.TypeReference:
                    var reference = reader.GetTypeReference((TypeReferenceHandle)baseType);
                    return reader.GetString(reference.Namespace) == "System" &&
                           reader.GetString(reference.Name) is "Object" or "ValueType" or "Enum";
                case HandleKind.TypeDefinition:
                    var ancestor = reader.GetTypeDefinition((TypeDefinitionHandle)baseType);
                    if (ancestor.GetMethods().Select(reader.GetMethodDefinition).Any(candidate =>
                            (candidate.Attributes & MethodAttributes.NewSlot) != 0 &&
                            reader.GetString(candidate.Name) == name && SameSignature(candidate.DecodeSignature(provider, null), signature)))
                    {
                        return false;
                    }

                    baseType = ancestor.BaseType;
                    break;
                default:
                    return false;
            }
        }
    }

    static bool SameSignature(MethodSignature<string> left, MethodSignature<string> right) =>
        left.GenericParameterCount == right.GenericParameterCount && left.ReturnType == right.ReturnType &&
        left.ParameterTypes.SequenceEqual(right.ParameterTypes);

    static void AddFields(MetadataReader reader, SignatureProvider provider, TypeDefinition type, string typeName,
        bool isEnum, List<SurfaceMember> members, List<string> literals)
    {
        foreach (var handle in type.GetFields())
        {
            var field = reader.GetFieldDefinition(handle);
            var access = field.Attributes & FieldAttributes.FieldAccessMask;
            if (access is not (FieldAttributes.Public or FieldAttributes.Family or FieldAttributes.FamORAssem) ||
                IsCompilerGenerated(reader, field.GetCustomAttributes()))
            {
                continue;
            }

            if (field.Attributes.HasFlag(FieldAttributes.SpecialName))
            {
                continue;
            }

            var name = reader.GetString(field.Name);
            if (field.Attributes.HasFlag(FieldAttributes.Literal))
            {
                // An enum's literals ride with the enum type; every other literal is inlined into consumer IL.
                if (!isEnum)
                {
                    literals.Add($"{typeName}.{name}");
                }

                continue;
            }

            members.Add(new SurfaceMember($"field {typeName}.{name}",
                [MemberKey(typeName, name, ":" + field.DecodeSignature(provider, null))]));
        }
    }

    static string MethodKey(MetadataReader reader, SignatureProvider provider, string typeName,
        MethodDefinitionHandle handle)
    {
        var method = reader.GetMethodDefinition(handle);
        return MemberKey(typeName, reader.GetString(method.Name), FormatMethod(method.DecodeSignature(provider, null)));
    }
}
