using MFTLib;

var volume = MFTUtilities.GetFileNameForDriveLetter("G");
var volumeHandle = FileUtilities.GetVolumeHandle(volume);
MFTParse.DumpVolumeInfo(volumeHandle);
MFTParse.ParseMFT(volumeHandle);
