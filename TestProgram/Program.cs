using MFTLib;
var volume = MFTParse.GetFileNameForDriveLetter("C");
var rootNode = MFTParse.GetMFTNode(volume);
rootNode.Print();
