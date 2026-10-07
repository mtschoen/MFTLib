using System.Collections.Immutable;
using System.Reflection;
using System.Reflection.Emit;
using System.Reflection.Metadata;
using System.Reflection.Metadata.Ecma335;

namespace MFTLib.Tests;

// The IL walker and the signature decoder of the metadata reader, split out to keep each file small.
internal static partial class MetadataSurfaceReader
{
    static readonly Dictionary<short, OperandType> OperandTypes = typeof(OpCodes)
        .GetFields(BindingFlags.Public | BindingFlags.Static).Select(field => (OpCode)field.GetValue(null)!)
        .ToDictionary(code => code.Value, code => code.OperandType);

    // The metadata tokens an IL stream names: every operand of the kinds that carry one.
    static IEnumerable<int> InstructionTokens(byte[] il) => Instructions(il).Where(instruction => instruction.HasToken)
        .Select(instruction => instruction.Token);

    const short ConstrainedPrefix = unchecked((short)0xFE16);
    const short CallVirtual = 0x6F;
    const short LoadArgumentZero = 0x02;
    const short LoadArgumentOne = 0x03;
    const short LoadArgumentTwo = 0x04;
    const short LoadArgumentThree = 0x05;
    const short LoadArgumentShort = 0x0E;
    const short LoadArgumentLong = unchecked((short)0xFE09);
    const short StoreArgumentShort = 0x10;
    const short StoreArgumentLong = unchecked((short)0xFE0B);
    const short LoadField = 0x7B;
    const short StoreField = 0x7D;
    const short Return = 0x2A;

    // A constrained call reaches T's method only when that method overrides the Object slot, which the sample's IL
    // cannot show, so the reference is kept apart from a direct call and the surface side decides.
    static string DispatchKey(string memberKey) => "dispatch " + memberKey;

    static readonly HashSet<string> ObjectVirtualMethods = ["ToString", "Equals", "GetHashCode"];

    readonly record struct Instruction(short Code, bool HasToken, int Token, int Variable, bool IsBranch);

    static IEnumerable<Instruction> Instructions(byte[] il)
    {
        var position = 0;
        while (position < il.Length)
        {
            var code = (short)il[position++];
            if (code == 0xFE)
            {
                code = (short)(0xFE00 | il[position++]);
            }

            var operand = OperandTypes[code];
            var hasToken = operand is OperandType.InlineMethod or OperandType.InlineField or OperandType.InlineTok or
                OperandType.InlineType;
            var variable = operand switch
            {
                OperandType.ShortInlineVar => il[position],
                OperandType.InlineVar => BitConverter.ToUInt16(il, position),
                _ => 0
            };
            var isBranch = operand is OperandType.InlineBrTarget or OperandType.ShortInlineBrTarget or OperandType.InlineSwitch;
            yield return new Instruction(code, hasToken, hasToken ? BitConverter.ToInt32(il, position) : 0, variable, isBranch);

            position += operand switch
            {
                OperandType.InlineNone => 0,
                OperandType.ShortInlineBrTarget or OperandType.ShortInlineI or OperandType.ShortInlineVar => 1,
                OperandType.InlineVar => 2,
                OperandType.InlineI8 or OperandType.InlineR => 8,
                OperandType.InlineSwitch => 4 + 4 * BitConverter.ToInt32(il, position),
                _ => 4
            };
        }
    }

    // `constrained. T` then `callvirt` of an Object virtual dispatches to the override T declares, so the call
    // references that override. The callee must be declared on System.Object: an interface method of the same name
    // dispatches elsewhere. T is a library type reference, or a closed instantiation of a library generic type,
    // which resolves to its generic definition; a type parameter names no type and credits nothing. The prefix
    // applies to the instruction that immediately follows it and to nothing later.
    static void AddConstrainedReferences(MetadataReader reader, byte[] il, SignatureProvider provider,
        SignatureProvider erasing, HashSet<string> references)
    {
        EntityHandle? constrainedType = null;
        foreach (var instruction in Instructions(il))
        {
            if (instruction.Code == ConstrainedPrefix)
            {
                constrainedType = MetadataTokens.EntityHandle(instruction.Token);
                continue;
            }

            var type = constrainedType;
            constrainedType = null;
            if (instruction.Code != CallVirtual || type is not { } constrained ||
                MetadataTokens.EntityHandle(instruction.Token) is not { Kind: HandleKind.MemberReference } member)
            {
                continue;
            }

            var called = reader.GetMemberReference((MemberReferenceHandle)member);
            if (!ObjectVirtualMethods.Contains(reader.GetString(called.Name)) || !IsSystemObject(reader, called.Parent))
            {
                continue;
            }

            var typeName = ConstrainedLibraryType(reader, constrained, provider, erasing);
            if (typeName is not null)
            {
                references.Add(DispatchKey(MemberKey(typeName, reader.GetString(called.Name),
                    FormatMethod(called.DecodeMethodSignature(provider, null)))));
            }
        }
    }

    static bool IsSystemObject(MetadataReader reader, EntityHandle parent)
    {
        if (parent.Kind != HandleKind.TypeReference)
        {
            return false;
        }

        var type = reader.GetTypeReference((TypeReferenceHandle)parent);
        return reader.GetString(type.Namespace) == "System" && reader.GetString(type.Name) == "Object";
    }

    static string? ConstrainedLibraryType(MetadataReader reader, EntityHandle constrained, SignatureProvider provider,
        SignatureProvider erasing)
    {
        switch (constrained.Kind)
        {
            case HandleKind.TypeReference:
                var reference = (TypeReferenceHandle)constrained;
                return IsLibraryScope(reader, reader.GetTypeReference(reference), provider.LibraryName)
                    ? provider.GetTypeFromReference(reader, reference, 0)
                    : null;
            case HandleKind.TypeSpecification:
                var decoded = reader.GetTypeSpecification((TypeSpecificationHandle)constrained).DecodeSignature(erasing, null);
                return erasing.LibraryTypes.Contains(decoded) ? decoded : null;
            default:
                return null;
        }
    }

    /// <summary>Decodes signatures to text, naming types by namespace, nesting and metadata name.</summary>
    sealed class SignatureProvider(bool eraseInstantiation, string libraryName = LibraryAssemblyName)
        : ISignatureTypeProvider<string, object?>
    {
        public string LibraryName { get; } = libraryName;

        public HashSet<string> LibraryTypes { get; } = [];

        public string GetPrimitiveType(PrimitiveTypeCode typeCode) => "System." + typeCode;

        public string GetTypeFromDefinition(MetadataReader reader, TypeDefinitionHandle handle, byte rawTypeKind)
        {
            var type = reader.GetTypeDefinition(handle);
            var name = reader.GetString(type.Name);
            var declaring = type.GetDeclaringType();
            return declaring.IsNil
                ? Qualify(reader.GetString(type.Namespace), name)
                : GetTypeFromDefinition(reader, declaring, 0) + "+" + name;
        }

        public string GetTypeFromReference(MetadataReader reader, TypeReferenceHandle handle, byte rawTypeKind)
        {
            var type = reader.GetTypeReference(handle);
            var name = reader.GetString(type.Name);
            var full = type.ResolutionScope.Kind == HandleKind.TypeReference
                ? GetTypeFromReference(reader, (TypeReferenceHandle)type.ResolutionScope, 0) + "+" + name
                : Qualify(reader.GetString(type.Namespace), name);
            if (IsLibraryScope(reader, type, LibraryName))
            {
                LibraryTypes.Add(full);
            }

            return full;
        }

        public string GetTypeFromSpecification(MetadataReader reader, object? genericContext,
            TypeSpecificationHandle handle, byte rawTypeKind) =>
            reader.GetTypeSpecification(handle).DecodeSignature(this, genericContext);

        public string GetSZArrayType(string elementType) => elementType + "[]";

        public string GetArrayType(string elementType, ArrayShape shape) =>
            elementType + "[" + new string(',', shape.Rank - 1) + "]";

        public string GetByReferenceType(string elementType) => elementType + "&";

        public string GetPointerType(string elementType) => elementType + "*";

        public string GetPinnedType(string elementType) => elementType;

        public string GetModifiedType(string modifier, string unmodifiedType, bool isRequired) => unmodifiedType;

        public string GetFunctionPointerType(MethodSignature<string> signature) => "fnptr";

        public string GetGenericInstantiation(string genericType, ImmutableArray<string> typeArguments) =>
            eraseInstantiation ? genericType : $"{genericType}<{string.Join(",", typeArguments)}>";

        public string GetGenericTypeParameter(object? genericContext, int index) => "!" + index;

        public string GetGenericMethodParameter(object? genericContext, int index) => "!!" + index;

        static string Qualify(string namespaceName, string name) =>
            namespaceName.Length == 0 ? name : namespaceName + "." + name;
    }
}
