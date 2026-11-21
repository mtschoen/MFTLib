using System.Text;
using MFTLib.Index;

namespace SampleProgram.Direct;

partial class SampleHost
{
    // The bytes a hex preview and a text preview show; the content itself can be larger.
    const int PreviewBytes = 64;

    /// <summary>
    ///     Reads one file's resident <c>$DATA</c> straight from its MFT record and prints what the read found. A dump
    ///     source answers from the saved records, so this runs unattended with no elevation. The bytes are previewed only
    ///     on the console: an elevated run writes to output.log, an ordinary file, which gets the outcome and byte count
    ///     alone so the elevated read never copies a file's content anywhere its permissions do not reach.
    /// </summary>
    async Task ReadContentAsync(FileIndex index, DirectArguments parsed, CancellationToken cancellationToken)
    {
        var entry = Resolve(index, parsed.Path, cancellationToken);
        InlineContentOutcome? outcome = null;
        byte[] content = [];
        var position = -1;
        var result = await index.ReadInlineContentAsync([entry], answer =>
        {
            position = answer.Index;
            outcome = answer.Outcome;
            content = answer.Content.ToArray();
        }, cancellationToken).ConfigureAwait(false);
        if (result.Outcome is RootOperationOutcome.Failed)
        {
            throw new InvalidOperationException($"Reading {entry.Path} failed.", result.Failure);
        }

        if (result.Outcome is RootOperationOutcome.NotApplicable)
        {
            _output.WriteLine($"{entry.Path}: this source cannot read file content from the MFT.");
            return;
        }

        if (position != 0)
        {
            throw new InvalidOperationException($"The read answered position {position} for the one entry it was given.");
        }

        _output.WriteLine($"{entry.Path}: {outcome}, {content.Length} bytes");
        if (outcome is InlineContentOutcome.Returned && content.Length > 0 && !_outputRedirectedToFile)
        {
            var shown = content.AsSpan(0, Math.Min(content.Length, PreviewBytes));
            _output.WriteLine($"  hex: {Convert.ToHexString(shown)}{(content.Length > shown.Length ? "..." : string.Empty)}");
            _output.WriteLine($"  text: {PreviewText(shown)}");
        }
    }

    // The preview as text when it is valid UTF-8 whose only control characters are line breaks and tabs, otherwise a
    // note that it is binary.
    static string PreviewText(ReadOnlySpan<byte> bytes)
    {
        try
        {
            var text = new UTF8Encoding(false, throwOnInvalidBytes: true).GetString(bytes);
            return text.Any(IsBinaryControl) ? "<binary>" : text;
        }
        catch (DecoderFallbackException)
        {
            return "<binary>";
        }
    }

    static bool IsBinaryControl(char character) => char.IsControl(character) && character is not ('\r' or '\n' or '\t');
}
