namespace MFTLibTestExtensions;

/// <summary>
///     Seams of an in-process broker started by <see cref="BrokerTestHarness" />. The host's own
///     clock and processor count go through the <c>JournalBrokerHost</c> constructor instead.
/// </summary>
internal sealed record BrokerTestHarnessOptions
{
    /// <summary>The client's clock, which measures its write and reply timeouts.</summary>
    public TimeProvider TimeProvider { get; init; } = TimeProvider.System;

    /// <summary>
    ///     By drive pipe name: an exception the host's connect to that pipe throws, or null to
    ///     connect normally.
    /// </summary>
    public Func<string, Exception?>? FailConnection { get; init; }

    /// <summary>
    ///     By pipe name (<c>"control"</c> or a drive pipe's name): every host write to that pipe
    ///     waits on the returned task before any byte is written. Null holds nothing.
    /// </summary>
    public Func<string, Task>? HoldWrites { get; init; }
}
