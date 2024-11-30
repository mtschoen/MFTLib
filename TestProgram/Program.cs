using MFTLib;

var volume = MFTUtilities.GetFileNameForDriveLetter("C");
var volumeHandle = FileUtilities.GetVolumeHandle(volume);
MFTParse.DumpVolumeInfo(volumeHandle);
MFTParse.ParseMFT(volumeHandle);
