using System.Reflection;
using System.Reflection.Metadata;
using System.Reflection.Metadata.Ecma335;
using System.Reflection.PortableExecutable;

namespace MFTLib.Tests;

// The record-synthesis evidence and the constructor-credit rules of the metadata reader, split out to keep each file small.
internal static partial class MetadataSurfaceReader
{
    // Roslyn synthesizes op_Equality for every record class and record struct and marks it CompilerGenerated, and an
    // author cannot declare it; the mark survives an authored ToString, Equals, GetHashCode or PrintMembers.
    static bool IsRecord(MetadataReader reader, TypeDefinition type) =>
        type.GetMethods().Select(reader.GetMethodDefinition).Any(method =>
            reader.GetString(method.Name) == "op_Equality" && IsCompilerGenerated(reader, method.GetCustomAttributes()));

    // Every property is surface except the EqualityContract a record synthesizes, which carries the attribute on the
    // property itself. An automatic property of a record (every exposed accessor CompilerGenerated) is also called by
    // constructing the record through a public constructor whose own IL stores one of its arguments straight into the
    // property's backing field, which is how a positional property is initialized. Any other property needs one of its
    // accessors referenced.
    static void AddProperties(PEReader peReader, MetadataReader reader, SignatureProvider provider, TypeDefinition type,
        string typeName, bool isRecord, HashSet<MethodDefinitionHandle> accessors, List<SurfaceMember> members)
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
            if (exposed.Count == 0 ||
                (name == "EqualityContract" && IsCompilerGenerated(reader, definition.GetCustomAttributes())))
            {
                continue;
            }

            var display = PropertyDisplay(reader, provider, typeName, name, exposed[0]);
            var automatic = exposed.All(accessor =>
                IsCompilerGenerated(reader, reader.GetMethodDefinition(accessor).GetCustomAttributes()));
            members.Add(new SurfaceMember(display,
                exposed.Select(accessor => MethodKey(reader, provider, typeName, accessor)).ToList())
            {
                ConstructorKeys = isRecord && automatic && !pair.Getter.IsNil
                    ? ConstructorsStoring(peReader, reader, provider, type, typeName, pair.Getter)
                    : []
            });
        }
    }

    // The public constructors whose own IL stores an argument (ldarg 1 or higher, immediately followed by stfld) into
    // the field the getter reads, once, in straight-line code. Pure delegation, a store through a setter, a store of
    // a constant and a store into another field credit nothing; a constructor that delegates with this(...) and also
    // stores an argument itself is credited on the same terms. It is evidence of a direct store, not proof of data flow.
    static List<string> ConstructorsStoring(PEReader peReader, MetadataReader reader, SignatureProvider provider,
        TypeDefinition type, string typeName, MethodDefinitionHandle getter)
    {
        var erasing = new SignatureProvider(eraseInstantiation: true, provider.LibraryName);
        var backing = BackingFieldName(peReader, reader, erasing, typeName, reader.GetMethodDefinition(getter));
        if (backing is null)
        {
            return [];
        }

        return type.GetMethods()
            .Where(handle =>
            {
                var method = reader.GetMethodDefinition(handle);
                return reader.GetString(method.Name) == ".ctor" && method.RelativeVirtualAddress != 0 &&
                       (method.Attributes & MethodAttributes.MemberAccessMask) == MethodAttributes.Public &&
                       StoresArgumentInto(peReader, reader, erasing, typeName, method, backing);
            })
            .Select(handle => MethodKey(reader, provider, typeName, handle)).ToList();
    }

    // An automatic getter is exactly `ldarg.0; ldfld field; ret`; the field is read from that IL, never from its name.
    static string? BackingFieldName(PEReader peReader, MetadataReader reader, SignatureProvider erasing, string typeName,
        MethodDefinition getter)
    {
        if (getter.RelativeVirtualAddress == 0)
        {
            return null;
        }

        var body = Instructions(peReader.GetMethodBody(getter.RelativeVirtualAddress).GetILBytes()!).ToList();
        return body is [{ Code: LoadArgumentZero }, { Code: LoadField } load, { Code: Return }]
            ? OwnFieldName(reader, erasing, typeName, load.Token)
            : null;
    }

    // Straight-line evidence only: the constructor has no branch, no starg and exactly one stfld of the field, and
    // that store takes an argument directly. Anything else may overwrite, skip or alter the stored value.
    static bool StoresArgumentInto(PEReader peReader, MetadataReader reader, SignatureProvider erasing, string typeName,
        MethodDefinition constructor, string backing)
    {
        var body = Instructions(peReader.GetMethodBody(constructor.RelativeVirtualAddress).GetILBytes()!).ToList();
        if (body.Any(instruction => instruction.IsBranch || instruction.Code is StoreArgumentShort or StoreArgumentLong))
        {
            return false;
        }

        var stores = Enumerable.Range(0, body.Count).Where(index => body[index].Code == StoreField &&
            OwnFieldName(reader, erasing, typeName, body[index].Token) == backing).ToList();
        return stores is [var only] && only > 0 && IsArgumentLoad(body[only - 1]);
    }

    // ldarg.1 to ldarg.3, ldarg.s and ldarg with an index of 1 or more; ldarg.0 is `this`.
    static bool IsArgumentLoad(Instruction instruction) => instruction.Code switch
    {
        LoadArgumentOne or LoadArgumentTwo or LoadArgumentThree => true,
        LoadArgumentShort or LoadArgumentLong => instruction.Variable >= 1,
        _ => false
    };

    // The name of a field of this very type: a field definition, or a member reference on an instantiation of the type.
    static string? OwnFieldName(MetadataReader reader, SignatureProvider erasing, string typeName, int token)
    {
        var handle = MetadataTokens.EntityHandle(token);
        if (handle.Kind == HandleKind.FieldDefinition)
        {
            var field = reader.GetFieldDefinition((FieldDefinitionHandle)handle);
            return erasing.GetTypeFromDefinition(reader, field.GetDeclaringType(), 0) == typeName
                ? reader.GetString(field.Name)
                : null;
        }

        if (handle.Kind != HandleKind.MemberReference)
        {
            return null;
        }

        var member = reader.GetMemberReference((MemberReferenceHandle)handle);
        return member.Parent.Kind == HandleKind.TypeSpecification &&
               reader.GetTypeSpecification((TypeSpecificationHandle)member.Parent).DecodeSignature(erasing, null) == typeName
            ? reader.GetString(member.Name)
            : null;
    }


    // Roslyn marks every record member it synthesizes CompilerGenerated except the primary constructor, which has
    // the declared parameters verbatim. The marked Deconstruct of a positional record carries those parameters too,
    // so the primary constructor is the constructor whose full signature (names and types) equals it. An authored
    // Deconstruct overload or constructor overload is not marked, or differs in signature, and stays on the surface.
    sealed record PrimaryParameter(string Name, string Type);

    static List<PrimaryParameter> PrimaryParameters(MetadataReader reader, SignatureProvider provider, TypeDefinition type)
    {
        foreach (var handle in type.GetMethods())
        {
            var method = reader.GetMethodDefinition(handle);
            if (reader.GetString(method.Name) == "Deconstruct" && IsCompilerGenerated(reader, method.GetCustomAttributes()))
            {
                return ParameterSignature(reader, provider, method, trimByReference: true);
            }
        }

        return [];
    }

    static List<PrimaryParameter> ParameterSignature(MetadataReader reader, SignatureProvider provider, MethodDefinition method,
        bool trimByReference)
    {
        var types = method.DecodeSignature(provider, null).ParameterTypes;
        var names = method.GetParameters().Select(handle => reader.GetParameter(handle))
            .Where(parameter => parameter.SequenceNumber > 0).OrderBy(parameter => parameter.SequenceNumber)
            .Select(parameter => reader.GetString(parameter.Name)).ToList();
        return names.Count != types.Length
            ? []
            : names.Select((name, index) => new PrimaryParameter(name,
                trimByReference ? types[index].TrimEnd('&') : types[index])).ToList();
    }

    static bool IsPrimaryConstructor(MetadataReader reader, SignatureProvider provider, MethodDefinition method, string name,
        IReadOnlyList<PrimaryParameter> primaryParameters) =>
        name == ".ctor" && primaryParameters.Count > 0 &&
        ParameterSignature(reader, provider, method, trimByReference: false).SequenceEqual(primaryParameters,
            PrimaryParameterComparer.Instance);

    sealed class PrimaryParameterComparer : IEqualityComparer<PrimaryParameter>
    {
        public static readonly PrimaryParameterComparer Instance = new();

        public bool Equals(PrimaryParameter? left, PrimaryParameter? right) =>
            left is not null && right is not null && left.Type == right.Type &&
            string.Equals(left.Name, right.Name, StringComparison.Ordinal);

        public int GetHashCode(PrimaryParameter parameter) => parameter.Type.GetHashCode();
    }
}
