using System.Linq.Expressions;
using System.Reflection;
using System.Runtime.Loader;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace MFTLib.Tests.Index;

/// <summary>
///     Exercises the public <c>SyntheticVolumeFacts</c> refusal before activation. This test assembly activates
///     overrides at load and activation has no reset, so the refusal is observed on private copies of MFTLib and
///     MFTLibTestExtensions loaded into a collectible context whose static state starts unactivated.
/// </summary>
[TestClass]
public sealed class SyntheticVolumeFactsActivationTests
{
    const string ExtensionsAssemblyName = "MFTLibTestExtensions";
    const string LibraryAssemblyName = "MFTLib";

    [TestMethod]
    public void Override_BeforeActivation_ThrowsThroughThePublicApiAndSucceedsAfterAllowOverrides()
    {
        var context = new IsolatedLoadContext();
        try
        {
            var facts = context.LoadFromAssemblyName(new AssemblyName(ExtensionsAssemblyName))
                .GetType("MFTLibTestExtensions.SyntheticVolumeFacts", throwOnError: true)!;
            var overrideMethod = facts.GetMethod("Override", BindingFlags.Public | BindingFlags.Static)!;
            var reader = NullReader(overrideMethod.GetParameters()[0].ParameterType);

            var refusal = Assert.ThrowsExactly<InvalidOperationException>(() => Invoke(overrideMethod, reader));
            StringAssert.Contains(refusal.Message, "AllowOverrides");

            facts.GetMethod("AllowOverrides", BindingFlags.Public | BindingFlags.Static)!.Invoke(null, null);
            var scope = (IDisposable)Invoke(overrideMethod, reader)!;
            scope.Dispose();
        }
        finally
        {
            context.Unload();
        }
    }

    static object? Invoke(MethodInfo method, object argument)
    {
        try
        {
            return method.Invoke(null, [argument]);
        }
        catch (TargetInvocationException failure) when (failure.InnerException is not null)
        {
            System.Runtime.ExceptionServices.ExceptionDispatchInfo.Capture(failure.InnerException).Throw();
            throw;
        }
    }

    static Delegate NullReader(Type delegateType)
    {
        var invoke = delegateType.GetMethod("Invoke")!;
        var parameters = invoke.GetParameters().Select(parameter => Expression.Parameter(parameter.ParameterType)).ToArray();
        return Expression.Lambda(delegateType, Expression.Constant(null, invoke.ReturnType), parameters).Compile();
    }

    sealed class IsolatedLoadContext() : AssemblyLoadContext("unactivated-volume-facts", isCollectible: true)
    {
        protected override Assembly? Load(AssemblyName assemblyName)
        {
            if (assemblyName.Name is not (ExtensionsAssemblyName or LibraryAssemblyName))
            {
                return null;
            }

            return LoadFromAssemblyPath(Path.Combine(AppContext.BaseDirectory, assemblyName.Name + ".dll"));
        }
    }
}
