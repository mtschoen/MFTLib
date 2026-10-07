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

    // What the compiler synthesizes for a record. A consumer reaches these through the language, not by name.
    static readonly HashSet<string> RecordMemberNames =
        ["<Clone>$", "Equals", "GetHashCode", "ToString", "PrintMembers", "Deconstruct", "op_Equality", "op_Inequality",
            "EqualityContract"];

    /// <summary>The members that need a caller, and the literal fields whose use the compiler inlines.</summary>
    public sealed record Surface(IReadOnlyList<SurfaceMember> Members, IReadOnlyList<string> LiteralFields);

    /// <summary>One thing that needs a caller: a type, or a member whose use leaves any one of <see cref="Keys" /> in IL.</summary>
    public sealed record SurfaceMember(string Display, IReadOnlyList<string> Keys);

    public static string TypeKey(string typeFullName) => "type " + typeFullName;

    public static string MemberKey(string typeFullName, string memberName, string signature) =>
        $"{typeFullName}::{memberName}{signature}";

    /// <summary>The identities of every library type and member named by the IL of the given assembly files.</summary>
    public static HashSet<string> CollectReferences(IEnumerable<string> assemblyPaths)
    {
        var references = new HashSet<string>(StringComparer.Ordinal);
        foreach (var path in assemblyPaths)
        {
            using var peReader = new PEReader(new MemoryStream(File.ReadAllBytes(path)));
            CollectReferences(peReader.GetMetadataReader(), references);
        }

        return references;
    }

    /// <summary>
    ///     The library members named by the method bodies of one type of an assembly file, found by walking its IL.
    ///     A whole test assembly names nearly every overload somewhere; one fixture type names only what it calls.
    /// </summary>
    public static HashSet<string> CollectReferencesFromType(string assemblyPath, string typeFullName)
    {
        using var peReader = new PEReader(new MemoryStream(File.ReadAllBytes(assemblyPath)));
        var reader = peReader.GetMetadataReader();
        var provider = new SignatureProvider(eraseInstantiation: false);
        var erasing = new SignatureProvider(eraseInstantiation: true);
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

            foreach (var token in InstructionTokens(peReader.GetMethodBody(method.RelativeVirtualAddress).GetILBytes()!))
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

    static void CollectReferences(MetadataReader reader, HashSet<string> references)
    {
        var provider = new SignatureProvider(eraseInstantiation: false);
        var erasing = new SignatureProvider(eraseInstantiation: true);

        foreach (var handle in reader.TypeReferences)
        {
            if (IsLibraryScope(reader, reader.GetTypeReference(handle)))
            {
                references.Add(TypeKey(provider.GetTypeFromReference(reader, handle, 0)));
            }
        }

        foreach (var handle in reader.MemberReferences)
        {
            AddMemberReference(reader, reader.GetMemberReference(handle), provider, erasing, references);
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

    static bool IsLibraryScope(MetadataReader reader, TypeReference type)
    {
        var scope = type.ResolutionScope;
        while (scope.Kind == HandleKind.TypeReference)
        {
            scope = reader.GetTypeReference((TypeReferenceHandle)scope).ResolutionScope;
        }

        return scope.Kind == HandleKind.AssemblyReference &&
               reader.GetString(reader.GetAssemblyReference((AssemblyReferenceHandle)scope).Name) == LibraryAssemblyName;
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
            return IsLibraryScope(reader, reader.GetTypeReference(parent))
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
    public static Surface ReadSurface(string libraryPath)
    {
        using var peReader = new PEReader(new MemoryStream(File.ReadAllBytes(libraryPath)));
        var reader = peReader.GetMetadataReader();
        var provider = new SignatureProvider(eraseInstantiation: false);
        var members = new List<SurfaceMember>();
        var literals = new List<string>();
        foreach (var handle in reader.TypeDefinitions)
        {
            var type = reader.GetTypeDefinition(handle);
            if (type.GetDeclaringType().IsNil && IsExposed(reader, type) &&
                SurfaceNamespaces.Contains(reader.GetString(type.Namespace)))
            {
                AddType(reader, provider, handle, members, literals);
            }
        }

        return new Surface(members, literals);
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
            if (parent.Kind == HandleKind.TypeReference &&
                reader.GetString(reader.GetTypeReference((TypeReferenceHandle)parent).Name) == "CompilerGeneratedAttribute")
            {
                return true;
            }
        }

        return false;
    }

    static bool IsEnum(MetadataReader reader, TypeDefinition type) =>
        type.BaseType.Kind == HandleKind.TypeReference &&
        reader.GetString(reader.GetTypeReference((TypeReferenceHandle)type.BaseType).Name) == "Enum";

    static void AddType(MetadataReader reader, SignatureProvider provider, TypeDefinitionHandle handle,
        List<SurfaceMember> members, List<string> literals)
    {
        var type = reader.GetTypeDefinition(handle);
        var typeName = provider.GetTypeFromDefinition(reader, handle, 0);
        members.Add(new SurfaceMember("type " + typeName, [TypeKey(typeName)]));

        foreach (var nested in type.GetNestedTypes())
        {
            if (IsExposed(reader, reader.GetTypeDefinition(nested)))
            {
                AddType(reader, provider, nested, members, literals);
            }
        }

        var methodNames = type.GetMethods().Select(method => reader.GetString(reader.GetMethodDefinition(method).Name))
            .ToHashSet();
        var isRecord = methodNames.Contains("<Clone>$") || methodNames.Contains("PrintMembers");
        var primaryParameters = isRecord ? PrimaryParameterNames(reader, type) : [];

        var accessors = new HashSet<MethodDefinitionHandle>();
        AddProperties(reader, provider, type, typeName, isRecord ? primaryParameters : null, accessors, members);
        AddEvents(reader, provider, type, typeName, accessors, members);
        AddMethods(reader, provider, type, typeName, isRecord, primaryParameters, accessors, members);
        AddFields(reader, provider, type, typeName, IsEnum(reader, type), members, literals);
    }

    // primaryParameters is null for a type that is not a record; a record's positional properties are generated.
    static void AddProperties(MetadataReader reader, SignatureProvider provider, TypeDefinition type, string typeName,
        IReadOnlyList<string>? primaryParameters, HashSet<MethodDefinitionHandle> accessors, List<SurfaceMember> members)
    {
        foreach (var handle in type.GetProperties())
        {
            var definition = reader.GetPropertyDefinition(handle);
            var pair = definition.GetAccessors();
            var exposed = new[] { pair.Getter, pair.Setter }
                .Where(accessor => !accessor.IsNil && IsExposed(reader.GetMethodDefinition(accessor).Attributes)).ToList();
            foreach (var accessor in new[] { pair.Getter, pair.Setter }.Where(accessor => !accessor.IsNil))
            {
                accessors.Add(accessor);
            }

            var name = reader.GetString(definition.Name);
            var generated = primaryParameters is not null &&
                            (name == "EqualityContract" || primaryParameters.Contains(name, StringComparer.OrdinalIgnoreCase));
            if (exposed.Count > 0 && !generated)
            {
                members.Add(new SurfaceMember($"property {typeName}.{name}",
                    exposed.Select(accessor => MethodKey(reader, provider, typeName, accessor)).ToList()));
            }
        }
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
        bool isRecord, IReadOnlyList<string> primaryParameters, HashSet<MethodDefinitionHandle> accessors,
        List<SurfaceMember> members)
    {
        foreach (var handle in type.GetMethods())
        {
            var method = reader.GetMethodDefinition(handle);
            var name = reader.GetString(method.Name);
            if (accessors.Contains(handle) || !IsExposed(method.Attributes) ||
                IsCompilerGenerated(reader, method.GetCustomAttributes()) ||
                (isRecord && IsRecordMember(reader, provider, typeName, method, name, primaryParameters)))
            {
                continue;
            }

            members.Add(new SurfaceMember(
                $"method {typeName}.{name}({string.Join(", ", method.DecodeSignature(provider, null).ParameterTypes)})",
                [MethodKey(reader, provider, typeName, handle)]));
        }
    }

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

    // The primary constructor of a positional record is the constructor whose parameter names match Deconstruct's.
    static List<string> PrimaryParameterNames(MetadataReader reader, TypeDefinition type)
    {
        foreach (var handle in type.GetMethods())
        {
            var method = reader.GetMethodDefinition(handle);
            if (reader.GetString(method.Name) == "Deconstruct")
            {
                return ParameterNames(reader, method);
            }
        }

        return [];
    }

    static List<string> ParameterNames(MetadataReader reader, MethodDefinition method) =>
        method.GetParameters().Select(handle => reader.GetParameter(handle))
            .Where(parameter => parameter.SequenceNumber > 0)
            .Select(parameter => reader.GetString(parameter.Name)).ToList();

    static bool IsRecordMember(MetadataReader reader, SignatureProvider provider, string typeName, MethodDefinition method,
        string name, IReadOnlyList<string> primaryParameters)
    {
        if (RecordMemberNames.Contains(name))
        {
            return true;
        }

        if (name != ".ctor")
        {
            return false;
        }

        var parameters = method.DecodeSignature(provider, null).ParameterTypes;
        var isCopyConstructor = parameters.Length == 1 && parameters[0] == typeName;
        return isCopyConstructor || (primaryParameters.Count > 0 &&
                                     ParameterNames(reader, method).SequenceEqual(primaryParameters,
                                         StringComparer.OrdinalIgnoreCase));
    }
}
