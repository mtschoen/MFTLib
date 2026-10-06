namespace TestProgram;

/// <summary>What an option's value must look like.</summary>
internal enum OptionKind
{
    Flag,
    Text,
    PositiveNumber,
    Size,
    Count,
    Seconds,
    Serial,
    Date,
    Letters,
    CacheTag,
    RecordKey,
    Choice
}

/// <summary>One option of an Index verb.</summary>
internal sealed record OptionSpecification(
    string Name,
    OptionKind Kind,
    string[]? Choices = null,
    bool Repeatable = false)
{
    internal bool Accepts(string value)
    {
        return Kind switch
        {
            OptionKind.Text => value.Length > 0,
            OptionKind.PositiveNumber => long.TryParse(value, System.Globalization.NumberStyles.None,
                System.Globalization.CultureInfo.InvariantCulture, out var number) && number > 0,
            OptionKind.Size => long.TryParse(value, System.Globalization.NumberStyles.None,
                System.Globalization.CultureInfo.InvariantCulture, out _),
            OptionKind.Count => IsPositiveNumberUpTo(value, int.MaxValue),
            OptionKind.Seconds => IsPositiveNumberUpTo(value, 1_000_000),
            OptionKind.Serial => IsPositiveNumberUpTo(value, uint.MaxValue),
            OptionKind.Date => IndexVerbArguments.TryParseDate(value, out _),
            OptionKind.Letters => IndexVerbArguments.TryParseLetters(value, out _),
            OptionKind.CacheTag => IndexVerbArguments.TryParseCacheTag(value, out _),
            OptionKind.RecordKey => IndexVerbArguments.TryParseRecordKey(value, out _),
            OptionKind.Choice => (Choices ?? []).Contains(value, StringComparer.Ordinal),
            _ => true
        };
    }

    static bool IsPositiveNumberUpTo(string value, long limit)
    {
        return long.TryParse(value, System.Globalization.NumberStyles.None,
            System.Globalization.CultureInfo.InvariantCulture, out var number) && number > 0 && number <= limit;
    }

    internal string Describe()
    {
        return Kind switch
        {
            OptionKind.Text => "a non-empty value",
            OptionKind.PositiveNumber => "a positive whole number",
            OptionKind.Size => "a whole number of bytes, zero or more",
            OptionKind.Count => "a positive whole number that fits in an int",
            OptionKind.Seconds => "a positive number of seconds, at most one million",
            OptionKind.Serial => "a positive volume serial that fits in 32 bits",
            OptionKind.Date => "a date such as 2026-01-31 or 2026-01-31T12:00:00Z (UTC when no offset is given)",
            OptionKind.Letters => "drive letters such as G or G,H",
            OptionKind.CacheTag => "FOURCC:VERSION such as TEST:1 (four ASCII characters)",
            OptionKind.RecordKey => "DRIVE:ROW:PRODUCER such as G:5:Mft (producer Mft or Enumeration)",
            OptionKind.Choice => "one of " + string.Join(", ", Choices ?? []),
            _ => "no value"
        };
    }
}

/// <summary>How a verb treats the drive letters on its command line.</summary>
internal enum DriveRule
{
    /// <summary>Any number; the sample drive when none are given.</summary>
    DefaultsToSampleDrive,

    /// <summary>Exactly one, never defaulted: the verb changes a volume.</summary>
    ExactlyOne,

    /// <summary>None: the verb touches no drive.</summary>
    None,

    /// <summary>Any number; none means every drive.</summary>
    OptionalFilter
}

/// <summary>One Index verb: its options, its drive rule and its cross-option checks.</summary>
internal sealed record VerbSpecification(
    string Name,
    string Summary,
    DriveRule Drives,
    IReadOnlyList<OptionSpecification> Options,
    Func<IndexVerbArguments, string?> Validate)
{
    /// <summary>True for a verb that opens a FileIndex, which is every verb that takes --source.</summary>
    internal bool OpensIndex => Options.Any(option => option.Name == "--source");

    internal IndexVerbArguments WithDefaultDrive(IndexVerbArguments arguments)
    {
        return Drives == DriveRule.DefaultsToSampleDrive && arguments.Drives.Count == 0
            ? arguments with { Drives = [TestProgramArguments.DefaultDrive[0]] }
            : arguments;
    }
}
