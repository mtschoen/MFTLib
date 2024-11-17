using MFTLib;

var volume = MFTUtilities.GetFileNameForDriveLetter("C");
var rootNode = MFTParse.GetMFTNode(volume);