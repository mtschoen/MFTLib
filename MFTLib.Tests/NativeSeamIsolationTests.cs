using System.Reflection;
using System.Reflection.Emit;
using System.Runtime.CompilerServices;
using MFTLib.Tests.TestSupport;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace MFTLib.Tests;

[TestClass]
public class NativeSeamIsolationTests
{
    const BindingFlags DeclaredMembers = BindingFlags.Public | BindingFlags.NonPublic |
                                         BindingFlags.Instance | BindingFlags.Static |
                                         BindingFlags.DeclaredOnly;

    static readonly Dictionary<short, OpCode> Instructions = typeof(OpCodes)
        .GetFields(BindingFlags.Public | BindingFlags.Static)
        .Where(field => field.FieldType == typeof(OpCode))
        .Select(field => (OpCode)field.GetValue(null)!)
        .ToDictionary(instruction => instruction.Value);

    [TestMethod]
    public void NativeSeamReferences_RequireClassLevelDoNotParallelize()
    {
        var candidates = typeof(NativeSeamIsolationTests).Assembly.GetTypes()
            .Where(type => type.IsDefined(typeof(TestClassAttribute), inherit: true));
        var violations = FindViolations(candidates);
        Assert.AreEqual(0, violations.Length, string.Join(Environment.NewLine, violations));
    }

    [DataTestMethod]
    [DataRow(typeof(NativeFieldReferenceFixture), "_getMftNativeAbiVersion")]
    [DataRow(typeof(VolumeFieldReferenceFixture), "_getVolumeHandle")]
    [DataRow(typeof(NativeResetReferenceFixture), "MFTLibNative.ResetToDefaults")]
    [DataRow(typeof(VolumeResetReferenceFixture), "FileUtilities.ResetToDefaults")]
    [DataRow(typeof(AsyncSeamReferenceFixture), "MFTLibNative.ResetToDefaults")]
    [DataRow(typeof(NestedSeamReferenceFixture), "FileUtilities.ResetToDefaults")]
    [DataRow(typeof(HelperSeamReferenceFixture), "MFTLibNative.ResetToDefaults")]
    [DataRow(typeof(InheritedSeamReferenceFixture), "MFTLibNative.ResetToDefaults")]
    [DataRow(typeof(ExternalAsyncHelperSeamReferenceFixture), "MFTLibNative.ResetToDefaults")]
    [DataRow(typeof(ExternalIteratorHelperSeamReferenceFixture), "MFTLibNative.ResetToDefaults")]
    [DataRow(typeof(NativeHookReferenceFixture), "NativeTestHooks.NativeSetAllocFailCountdown")]
    [DataRow(typeof(EnvironmentVariableReferenceFixture), "Environment.SetEnvironmentVariable")]
    [DataRow(typeof(CurrentDirectoryReferenceFixture), "Environment.set_CurrentDirectory")]
    [DataRow(typeof(ConsoleRedirectReferenceFixture), "Console.SetOut")]
    [DataRow(typeof(ProductionStaticFieldReferenceFixture), "BrokerLauncher._startProcess")]
    [DataRow(typeof(ProductionStaticSetterReferenceFixture), "BrokerDiagnostics.set_LogDirectory")]
    [DataRow(typeof(ProductionResetReferenceFixture), "BrokerLauncher.ResetToDefaults")]
    public void Detector_ReportsUnmarkedReferences(Type fixture, string member)
    {
        var violations = FindViolations([fixture]);
        Assert.IsTrue(violations.Length > 0, fixture.FullName);
        var description = string.Join(Environment.NewLine, violations);
        StringAssert.Contains(description, fixture.FullName!);
        StringAssert.Contains(description, member);
    }

    [DataTestMethod]
    [DataRow(typeof(IsolatedSeamReferenceFixture))]
    [DataRow(typeof(IsolatedExternalAsyncHelperSeamReferenceFixture))]
    [DataRow(typeof(HarmlessReferenceFixture))]
    [DataRow(typeof(IsolatedNativeHookReferenceFixture))]
    [DataRow(typeof(IsolatedEnvironmentVariableReferenceFixture))]
    [DataRow(typeof(ReadOnlyEnvironmentReferenceFixture))]
    public void Detector_AcceptsIsolatedAndUnrelatedTypes(Type fixture)
    {
        Assert.AreEqual(0, FindViolations([fixture]).Length);
    }

    static string[] FindViolations(IEnumerable<Type> candidates)
    {
        var violations = new SortedSet<string>(StringComparer.Ordinal);
        foreach (var candidate in candidates)
        {
            if (candidate.IsDefined(typeof(DoNotParallelizeAttribute), inherit: true))
            {
                continue;
            }

            var pending = new Stack<MethodBase>(MethodsIn(candidate));
            var visited = new HashSet<MethodBase>();
            while (pending.TryPop(out var method))
            {
                if (!visited.Add(method))
                {
                    continue;
                }

                if (method.GetCustomAttribute<StateMachineAttribute>() is { StateMachineType: { } stateMachineType } &&
                    stateMachineType.Assembly == candidate.Assembly)
                {
                    foreach (var stateMachineMethod in MethodsIn(stateMachineType))
                    {
                        pending.Push(stateMachineMethod);
                    }
                }

                foreach (var member in ReferencedMembers(method))
                {
                    if (IsNativeSeam(member))
                    {
                        violations.Add($"{candidate.FullName} references " +
                            $"{member.DeclaringType!.Name}.{member.Name}; " +
                            "add [DoNotParallelize] to the test class.");
                    }

                    if (member is MethodBase called && called.Module.Assembly == candidate.Assembly)
                    {
                        pending.Push(called);
                        if (called.DeclaringType?.TypeInitializer is { } initializer &&
                            initializer.Module.Assembly == candidate.Assembly)
                        {
                            pending.Push(initializer);
                        }
                    }
                    else if (member is FieldInfo field &&
                             field.DeclaringType?.TypeInitializer is { } fieldInitializer &&
                             fieldInitializer.Module.Assembly == candidate.Assembly)
                    {
                        pending.Push(fieldInitializer);
                    }
                }
            }
        }

        return violations.ToArray();
    }

    static IEnumerable<MethodBase> MethodsIn(Type type)
    {
        var methods = type.GetMethods(DeclaredMembers).Cast<MethodBase>()
            .Concat(type.GetConstructors(DeclaredMembers));
        if (type.TypeInitializer is { } initializer)
        {
            methods = methods.Append(initializer);
        }

        methods = methods.Concat(type.GetNestedTypes(BindingFlags.Public | BindingFlags.NonPublic)
            .SelectMany(MethodsIn));
        if (type.BaseType is { } parent && parent.Assembly == type.Assembly)
        {
            methods = methods.Concat(MethodsIn(parent));
        }

        return methods;
    }

    static bool IsNativeSeam(MemberInfo member)
    {
        if (member.DeclaringType == typeof(NativeTestHooks))
        {
            return member is MethodInfo { IsStatic: true };
        }

        if (member.DeclaringType is not { } declaringType)
        {
            return false;
        }

        if (IsRepositoryAssembly(declaringType.Assembly))
        {
            return member is FieldInfo { IsStatic: true, IsInitOnly: false, IsLiteral: false } ||
                   member is MethodInfo { IsStatic: true, IsSpecialName: true } setter &&
                   setter.Name.StartsWith("set_", StringComparison.Ordinal) ||
                   member is MethodInfo { IsStatic: true } method && ProductionMutators.Contains(method.Name);
        }

        return member is MethodInfo { IsStatic: true } call &&
               ProcessGlobalFrameworkCalls.Contains($"{declaringType.FullName}.{call.Name}");
    }

    // Production assemblies whose static state a test can change for the whole process.
    static bool IsRepositoryAssembly(Assembly assembly) =>
        assembly.GetName().Name is "MFTLib" or "MFTLibTestExtensions" or "SampleProgram.Direct" or "SampleProgram.Watch" or "Benchmark";

    // Static methods in the repository assemblies that install process-wide overrides or reset them.
    static readonly HashSet<string> ProductionMutators = new(StringComparer.Ordinal)
    {
        "ResetToDefaults", "Enable", "OverrideJournalWindow", "ReplaceWriterForTest"
    };

    // Framework members that change state shared by every concurrently running test class.
    static readonly HashSet<string> ProcessGlobalFrameworkCalls = new(StringComparer.Ordinal)
    {
        "System.Environment.set_CurrentDirectory", "System.Environment.set_ExitCode",
        "System.Environment.SetEnvironmentVariable", "System.IO.Directory.SetCurrentDirectory",
        "System.Console.SetOut", "System.Console.SetError", "System.Console.SetIn",
        "System.AppContext.SetSwitch", "System.Threading.ThreadPool.SetMinThreads",
        "System.Threading.ThreadPool.SetMaxThreads",
        "System.Globalization.CultureInfo.set_DefaultThreadCurrentCulture",
        "System.Globalization.CultureInfo.set_DefaultThreadCurrentUICulture"
    };

    static IEnumerable<MemberInfo> ReferencedMembers(MethodBase method)
    {
        var bytes = method.GetMethodBody()?.GetILAsByteArray();
        if (bytes is null)
        {
            yield break;
        }

        for (var offset = 0; offset < bytes.Length;)
        {
            short value = bytes[offset++];
            if (value == 0xfe)
            {
                value = unchecked((short)(0xfe00 | bytes[offset++]));
            }

            var operand = Instructions[value].OperandType;
            if (operand is OperandType.InlineField or OperandType.InlineMethod or OperandType.InlineTok)
            {
                var member = method.Module.ResolveMember(BitConverter.ToInt32(bytes, offset),
                    method.DeclaringType?.GetGenericArguments(),
                    method.IsGenericMethod ? method.GetGenericArguments() : null);
                if (member is not null)
                {
                    yield return member;
                }
            }

            offset += operand switch
            {
                OperandType.InlineNone => 0,
                OperandType.ShortInlineBrTarget or OperandType.ShortInlineI or
                    OperandType.ShortInlineVar => 1,
                OperandType.InlineVar => 2,
                OperandType.InlineI8 or OperandType.InlineR => 8,
                OperandType.InlineSwitch => 4 + 4 * BitConverter.ToInt32(bytes, offset),
                OperandType.InlineBrTarget or OperandType.InlineField or OperandType.InlineI or
                    OperandType.InlineMethod or OperandType.InlineSig or OperandType.InlineString or
                    OperandType.InlineTok or OperandType.InlineType or OperandType.ShortInlineR => 4,
                _ => throw new InvalidOperationException($"Unsupported IL operand: {operand}")
            };
        }
    }
}
