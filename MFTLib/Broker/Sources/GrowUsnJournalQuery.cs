using MFTLib.Index;

namespace MFTLib;

/// <summary>
///     Production wires this to <c>MftVolume.GrowUsnJournal</c> (resize a live volume's USN
///     journal in place via <c>FSCTL_CREATE_USN_JOURNAL</c>, grow only); tests inject a
///     fake so <see cref="JournalBrokerHost" /> can be exercised without a real elevated
///     volume handle.
/// </summary>
public delegate UsnJournalSettings GrowUsnJournalQuery(string driveLetter, long maximumSize, long allocationDelta);
