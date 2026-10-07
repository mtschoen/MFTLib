using System.Collections.Immutable;
using System.Reflection;
using System.Reflection.Emit;
using System.Reflection.Metadata;

namespace MFTLib.Tests;

// The IL walker and the signature decoder of the metadata reader, split out to keep each file small.
internal static partial class MetadataSurfaceReader
{
    static readonly Dictionary<short, OperandType> OperandTypes = typeof(OpCodes)
        .GetFields(BindingFlags.Public | BindingFlags.Static).Select(field => (OpCode)field.GetValue(null)!)
        .ToDictionary(code => code.Value, code => code.OperandType);

    // The metadata tokens an IL stream names: every operand of the kinds that carry one.
    static IEnumerable<int> InstructionTokens(byte[] il)
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
            if (operand is OperandType.InlineMethod or OperandType.InlineField or OperandType.InlineTok or
                OperandType.InlineType)
            {
                yield return BitConverter.ToInt32(il, position);
            }

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

    /// <summary>Decodes signatures to text, naming types by namespace, nesting and metadata name.</summary>
    sealed class SignatureProvider(bool eraseInstantiation) : ISignatureTypeProvider<string, object?>
    {
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
            if (IsLibraryScope(reader, type))
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
