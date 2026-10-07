using System.Reflection;
using System.Runtime.CompilerServices;
using System.Runtime.Loader;
using System.Reflection.Emit;

namespace MFTLib.Tests;

// Builds a small assembly whose static methods call fixture members in chosen ways, so the reader controls collect
// real IL that references the fixture types by assembly reference, exactly as a sample references the library.
// Each case becomes a static class named after it with one static method, Run.
internal static class CallerGateGeneratedAssembly
{
    // The assembly the fixture types live in, which the reader is told to treat as the library.
    public static readonly string FixtureAssemblyName = typeof(CallerGateGeneratedAssembly).Assembly.GetName().Name!;

    public static byte[] Build(IReadOnlyDictionary<string, Action<MethodBuilder>> cases)
    {
        var assembly = new PersistedAssemblyBuilder(new AssemblyName("CallerGateGenerated"), typeof(object).Assembly);
        var module = assembly.DefineDynamicModule("CallerGateGenerated");
        foreach (var (name, emit) in cases)
        {
            var type = module.DefineType(name, TypeAttributes.Public | TypeAttributes.Abstract | TypeAttributes.Sealed);
            var method = type.DefineMethod("Run", MethodAttributes.Public | MethodAttributes.Static, typeof(void), Type.EmptyTypes);
            emit(method);
            type.CreateType();
        }

        using var stream = new MemoryStream();
        assembly.Save(stream);
        return stream.ToArray();
    }

    /// <summary>`new T(...)` with default arguments, the result discarded.</summary>
    public static Action<MethodBuilder> Construct(ConstructorInfo constructor) => method =>
    {
        var il = method.GetILGenerator();
        foreach (var parameter in constructor.GetParameters())
        {
            il.Emit(OpCodes.Ldloc, il.DeclareLocal(parameter.ParameterType));
        }

        il.Emit(OpCodes.Newobj, constructor);
        il.Emit(OpCodes.Pop);
        il.Emit(OpCodes.Ret);
    };

    /// <summary>A direct callvirt of a parameterless instance method on a default instance, the result discarded.</summary>
    public static Action<MethodBuilder> CallDirect(MethodInfo target) => method =>
    {
        var il = method.GetILGenerator();
        il.Emit(OpCodes.Ldloc, il.DeclareLocal(target.DeclaringType!));
        il.Emit(OpCodes.Callvirt, target);
        il.Emit(OpCodes.Pop);
        il.Emit(OpCodes.Ret);
    };

    /// <summary>A call of an instance getter on a default instance, the result discarded.</summary>
    public static Action<MethodBuilder> Read(PropertyInfo property) => method =>
    {
        var il = method.GetILGenerator();
        il.Emit(OpCodes.Ldloc, il.DeclareLocal(property.DeclaringType!));
        il.Emit(OpCodes.Callvirt, property.GetMethod!);
        il.Emit(OpCodes.Pop);
        il.Emit(OpCodes.Ret);
    };

    /// <summary>
    ///     `constrained. constrainedType` then `callvirt callee` on a default instance. With <paramref name="gap" /> a
    ///     nop sits between the prefix and the call, which the prefix must not survive.
    /// </summary>
    public static Action<MethodBuilder> ConstrainedCall(Type constrainedType, MethodInfo callee, bool gap = false) => method =>
    {
        var il = method.GetILGenerator();
        EmitConstrainedCall(il, constrainedType, callee, gap);
    };

    /// <summary>The same call constrained on the type parameter of a generic method.</summary>
    public static Action<MethodBuilder> ConstrainedCallOnTypeParameter(MethodInfo callee) => method =>
    {
        var parameter = method.DefineGenericParameters("T")[0];
        var il = method.GetILGenerator();
        EmitConstrainedCall(il, parameter, callee, gap: false);
    };

    static void EmitConstrainedCall(ILGenerator il, Type constrainedType, MethodInfo callee, bool gap)
    {
        il.Emit(OpCodes.Ldloca, il.DeclareLocal(constrainedType));
        for (var index = 0; index < callee.GetParameters().Length; index++)
        {
            il.Emit(OpCodes.Ldnull);
        }

        il.Emit(OpCodes.Constrained, constrainedType);
        if (gap)
        {
            il.Emit(OpCodes.Nop);
        }

        il.Emit(OpCodes.Callvirt, callee);
        il.Emit(OpCodes.Pop);
        il.Emit(OpCodes.Ret);
    }

    // The namespace the marked library's types live in.
    public const string MarkedNamespace = "CallerGateMarked";

    /// <summary>
    ///     A library with an authored method and an authored type carrying a lookalike CompilerGeneratedAttribute from
    ///     another assembly, and a method and a type carrying the real one. The lookalike must hide nothing.
    /// </summary>
    public static byte[] BuildMarkedLibrary()
    {
        var decoyAssembly = new PersistedAssemblyBuilder(new AssemblyName("CallerGateDecoy"), typeof(object).Assembly);
        var decoyModule = decoyAssembly.DefineDynamicModule("CallerGateDecoy");
        var decoyType = decoyModule.DefineType("Decoy.CompilerGeneratedAttribute",
            TypeAttributes.Public | TypeAttributes.Sealed, typeof(Attribute));
        decoyType.DefineDefaultConstructor(MethodAttributes.Public);
        decoyType.CreateType();
        using var decoyStream = new MemoryStream();
        decoyAssembly.Save(decoyStream);
        decoyStream.Position = 0;
        var context = new AssemblyLoadContext("CallerGateDecoy", isCollectible: true);
        try
        {
            var decoy = context.LoadFromStream(decoyStream).GetType("Decoy.CompilerGeneratedAttribute")!;
            var decoyAttribute = new CustomAttributeBuilder(decoy.GetConstructor(Type.EmptyTypes)!, []);
            var realAttribute = new CustomAttributeBuilder(typeof(CompilerGeneratedAttribute).GetConstructor(Type.EmptyTypes)!, []);

            var assembly = new PersistedAssemblyBuilder(new AssemblyName("CallerGateMarkedLibrary"), typeof(object).Assembly);
            var module = assembly.DefineDynamicModule("CallerGateMarkedLibrary");
            var marked = module.DefineType(MarkedNamespace + ".Marked", TypeAttributes.Public | TypeAttributes.Class);
            DefineEmptyMethod(marked, "DecoyedMethod").SetCustomAttribute(decoyAttribute);
            DefineEmptyMethod(marked, "RealMethod").SetCustomAttribute(realAttribute);
            marked.CreateType();
            var decoyed = module.DefineType(MarkedNamespace + ".DecoyedType", TypeAttributes.Public | TypeAttributes.Class);
            decoyed.SetCustomAttribute(decoyAttribute);
            decoyed.CreateType();
            var real = module.DefineType(MarkedNamespace + ".RealType", TypeAttributes.Public | TypeAttributes.Class);
            real.SetCustomAttribute(realAttribute);
            real.CreateType();

            using var stream = new MemoryStream();
            assembly.Save(stream);
            return stream.ToArray();
        }
        finally
        {
            context.Unload();
        }
    }

    static MethodBuilder DefineEmptyMethod(TypeBuilder type, string name)
    {
        var method = type.DefineMethod(name, MethodAttributes.Public, typeof(void), Type.EmptyTypes);
        method.GetILGenerator().Emit(OpCodes.Ret);
        return method;
    }

    // The namespace of the hand-built record shapes.
    public const string HandNamespace = "CallerGateHand";

    /// <summary>
    ///     Types shaped like compiled records (a marked op_Equality, an automatic property whose getter is marked) whose
    ///     public constructor is hand-written IL. ThisShort and ThisLong store `this` through ldarg.s 0 and ldarg 0; StargLong overwrites the argument with the long starg form;
    ///     Argument stores argument 1 and is the positive twin.
    /// </summary>
    public static byte[] BuildHandWrittenConstructors()
    {
        var assembly = new PersistedAssemblyBuilder(new AssemblyName("CallerGateHandLibrary"), typeof(object).Assembly);
        var module = assembly.DefineDynamicModule("CallerGateHandLibrary");
        var marked = new CustomAttributeBuilder(typeof(CompilerGeneratedAttribute).GetConstructor(Type.EmptyTypes)!, []);
        DefineHandRecord(module, marked, "ThisShort", (il, field) => { il.Emit(OpCodes.Ldarg_0); il.Emit(OpCodes.Ldarg_S, (byte)0); il.Emit(OpCodes.Stfld, field); });
        DefineHandRecord(module, marked, "ThisLong", (il, field) => { il.Emit(OpCodes.Ldarg_0); il.Emit(OpCodes.Ldarg, (short)0); il.Emit(OpCodes.Stfld, field); });
        DefineHandRecord(module, marked, "StargLong", (il, field) => { il.Emit(OpCodes.Ldc_I4_0); il.Emit(OpCodes.Starg, (short)1); il.Emit(OpCodes.Ldarg_0); il.Emit(OpCodes.Ldarg_1); il.Emit(OpCodes.Stfld, field); });
        DefineHandRecord(module, marked, "Argument", (il, field) => { il.Emit(OpCodes.Ldarg_0); il.Emit(OpCodes.Ldarg_S, (byte)1); il.Emit(OpCodes.Stfld, field); });
        using var stream = new MemoryStream();
        assembly.Save(stream);
        return stream.ToArray();
    }

    static void DefineHandRecord(ModuleBuilder module, CustomAttributeBuilder marked, string name,
        Action<ILGenerator, FieldBuilder> constructorBody)
    {
        var type = module.DefineType(HandNamespace + "." + name, TypeAttributes.Public | TypeAttributes.Class);
        var field = type.DefineField("<Value>k__BackingField", typeof(int), FieldAttributes.Private);
        var getter = type.DefineMethod("get_Value", MethodAttributes.Public | MethodAttributes.SpecialName | MethodAttributes.HideBySig,
            typeof(int), Type.EmptyTypes);
        var getterIl = getter.GetILGenerator();
        getterIl.Emit(OpCodes.Ldarg_0);
        getterIl.Emit(OpCodes.Ldfld, field);
        getterIl.Emit(OpCodes.Ret);
        getter.SetCustomAttribute(marked);
        type.DefineProperty("Value", PropertyAttributes.None, typeof(int), null).SetGetMethod(getter);

        var equality = type.DefineMethod("op_Equality", MethodAttributes.Public | MethodAttributes.Static | MethodAttributes.SpecialName,
            typeof(bool), [type, type]);
        var equalityIl = equality.GetILGenerator();
        equalityIl.Emit(OpCodes.Ldc_I4_0);
        equalityIl.Emit(OpCodes.Ret);
        equality.SetCustomAttribute(marked);

        var constructor = type.DefineConstructor(MethodAttributes.Public, CallingConventions.Standard, [typeof(int)]);
        var il = constructor.GetILGenerator();
        constructorBody(il, field);
        il.Emit(OpCodes.Ret);
        type.CreateType();
    }
}
