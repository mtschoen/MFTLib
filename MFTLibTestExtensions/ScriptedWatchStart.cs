using MFTLib;

namespace MFTLibTestExtensions;

/// <summary>One watch start the index asked for: the drive and the cursor it resumed from.</summary>
/// <param name="DriveLetter">The drive the index started a watch on.</param>
/// <param name="Cursor">The journal position the watch resumed from.</param>
public readonly record struct ScriptedWatchStart(char DriveLetter, UsnJournalCursor Cursor);
