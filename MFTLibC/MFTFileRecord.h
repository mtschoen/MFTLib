#pragma once

struct MFTFileRecord
{
	GUID Guid;
	LPCWSTR FileName;
	LARGE_INTEGER FileSize;
};