using MFTLib;
var volume = MFTParse.GetFileNameForDriveLetter("C");
var files = MFTParse.GetFileNodes(volume);
foreach (var kvp in files)
{
    Console.WriteLine(kvp.Key);
}
