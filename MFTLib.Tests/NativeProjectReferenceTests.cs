using System.Xml.Linq;
using MFTLib.Tests.TestSupport;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace MFTLib.Tests;

[TestClass]
public class NativeProjectReferenceTests
{
    [TestMethod]
    public void EveryNativeProjectReference_ExcludesItselfFromNuGetRestore()
    {
        var projects = RepositoryFiles.Git("ls-files", "*.csproj")
            .Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        Assert.IsNotEmpty(projects, "No tracked csproj files were found.");

        var references = 0;
        foreach (var project in projects)
        {
            var document = XDocument.Load(Path.Combine(RepositoryFiles.Root, project));
            var native = document.Descendants("ProjectReference")
                .Where(reference => ((string?)reference.Attribute("Include") ?? "")
                    .EndsWith(".vcxproj", StringComparison.OrdinalIgnoreCase));
            foreach (var reference in native)
            {
                references++;
                Assert.AreEqual("false", (string?)reference.Attribute("ReferenceOutputAssembly"),
                    $"{project} references a native project without ReferenceOutputAssembly=\"false\"; " +
                    "Visual Studio MSBuild would then write it into packages.lock.json.");
            }
        }

        Assert.IsGreaterThan(0, references, "No native ProjectReference was found to check.");
    }
}
