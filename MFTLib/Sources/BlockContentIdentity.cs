using MFTLib.Index;

namespace MFTLib;

/// <summary>The consumer cache tag and the library's scan shape a block carries in its header.</summary>
readonly record struct BlockContentIdentity(CacheTag CacheTag, ulong ScanShape);
