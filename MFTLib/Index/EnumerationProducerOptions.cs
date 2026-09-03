namespace MFTLib.Index;

/// <summary>
///     Where an enumeration walk starts and which drive letter its entries report. On a
///     platform without drive letters the caller assigns one; it is a display and lookup key,
///     not a device identifier.
/// </summary>
public sealed record EnumerationProducerOptions
{
    public required string RootDirectory { get; init; }

    public required char DriveLetter { get; init; }
}
