using System.Reflection;
using MFTLib.Index;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace MFTLib.Tests.Index;

[TestClass]
public class VolumeKeyTests
{
    [TestMethod]
    [DataRow('c')]
    [DataRow('C')]
    [DataRow('z')]
    [DataRow('Z')]
    public void Constructor_NormalizesAsciiLetter(char letter)
    {
        var key = new VolumeKey(letter);
        VolumeKey converted = letter;
        Assert.AreEqual(char.ToUpperInvariant(letter), key.Letter);
        Assert.AreEqual(key, converted);
        Assert.AreEqual(new VolumeKey(char.ToUpperInvariant(letter)), key);
        Assert.AreEqual(key.Letter.ToString(), key.ToString());
    }

    [TestMethod]
    [DataRow('\0')]
    [DataRow('1')]
    [DataRow(':')]
    [DataRow('@')]
    [DataRow('[')]
    [DataRow('`')]
    [DataRow('{')]
    [DataRow('é')]
    public void Constructor_RejectsNonAsciiLetters(char letter) =>
        Assert.ThrowsExactly<ArgumentException>(() => new VolumeKey(letter));

    [TestMethod]
    public void DefaultKey_RejectsLetterAndFormatting()
    {
        Assert.ThrowsExactly<ArgumentException>(() => _ = default(VolumeKey).Letter);
        Assert.ThrowsExactly<ArgumentException>(() => default(VolumeKey).ToString());
    }

    [TestMethod]
    public void ConversionOperators_AcceptCharOnly()
    {
        var operators = typeof(VolumeKey).GetMethods(BindingFlags.Public | BindingFlags.Static)
            .Where(method => method.Name is "op_Implicit" or "op_Explicit").ToArray();
        Assert.HasCount(1, operators);
        Assert.AreEqual("op_Implicit", operators[0].Name);
        Assert.AreEqual(typeof(char), operators[0].GetParameters().Single().ParameterType);
        Assert.AreEqual(typeof(VolumeKey), operators[0].ReturnType);
    }

    [TestMethod]
    [DataRow("C", 'C')]
    [DataRow("c", 'C')]
    [DataRow("C:", 'C')]
    [DataRow("c:", 'C')]
    [DataRow("C:\\", 'C')]
    [DataRow("c:\\", 'C')]
    [DataRow("C:/", 'C')]
    [DataRow("c:/", 'C')]
    [DataRow("C\\", 'C')]
    [DataRow("c\\", 'C')]
    [DataRow("C/", 'C')]
    [DataRow("c/", 'C')]
    [DataRow(" \tC\r\n", 'C')]
    [DataRow(" \tc: \r\n", 'C')]
    [DataRow(" \tc:\\ \r\n", 'C')]
    [DataRow(" c:/ ", 'C')]
    [DataRow("a", 'A')]
    [DataRow("Z:/", 'Z')]
    public void TryParse_AcceptsDriveSpecs(string text, char expectedLetter)
    {
        Assert.IsTrue(VolumeKey.TryParse(text, out var volumeKey));
        Assert.AreEqual(expectedLetter, volumeKey.Letter);
        Assert.AreEqual(new VolumeKey(expectedLetter), volumeKey);
    }

    [TestMethod]
    [DataRow(null)]
    [DataRow("")]
    [DataRow(" \t\r\n")]
    [DataRow("\\\\server\\share\\")]
    [DataRow("//server/share/")]
    [DataRow("\\\\?\\C:\\")]
    [DataRow("\\\\.\\C:")]
    [DataRow("CD")]
    [DataRow("CC:")]
    [DataRow("C,D")]
    [DataRow("C:\\folder")]
    [DataRow("C:/folder")]
    [DataRow("C:folder")]
    [DataRow("C/folder")]
    [DataRow("C\\folder")]
    [DataRow("C::")]
    [DataRow("C://")]
    [DataRow("C:\\\\")]
    [DataRow("C//")]
    [DataRow("C/:")]
    [DataRow("C :")]
    [DataRow("C: /")]
    [DataRow("C\t:")]
    [DataRow("1:")]
    [DataRow("@")]
    [DataRow("[")]
    [DataRow("`")]
    [DataRow("{")]
    [DataRow("é:")]
    [DataRow("Ｃ:")]
    [DataRow("\0")]
    [DataRow("C\0")]
    [DataRow(":")]
    [DataRow("/")]
    [DataRow("\\")]
    public void TryParse_RejectsNonDriveSpecs(string? text)
    {
        Assert.IsFalse(VolumeKey.TryParse(text, out var volumeKey));
        Assert.AreEqual(default(VolumeKey), volumeKey);
    }
}
