using MFTLib;

var files = MFTParse.GetFileNodes();
foreach (var kvp in files)
{
    Console.WriteLine(kvp.Key);
}