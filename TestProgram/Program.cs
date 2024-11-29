using MFTLib;

var volume = MFTUtilities.GetFileNameForDriveLetter("C");
//var dump = VolumeUtilities.DumpVolumeInfo(VolumeUtilities.GetVolumeInfo(volume));
//Console.WriteLine(dump);
//var rootNode = MFTParse.GetMFTNode(volume);
MFTParseC.DumpVolumeInfo();
