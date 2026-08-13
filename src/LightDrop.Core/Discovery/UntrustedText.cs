using System.Globalization;
using System.Text;

namespace LightDrop.Core.Discovery;

/// <summary>
/// Cleans strings that arrived from the network.
/// </summary>
/// <remarks>
/// Peer metadata is attacker-controlled: anyone on the local link can announce a LightDrop
/// service with arbitrary values. This runs at <em>ingestion</em>, not at render time, so every
/// consumer — the CLI table, the peers endpoint, anything added later — inherits the cleaning
/// rather than each having to remember it.
/// <para>
/// The concrete threat is a device name carrying ANSI escape sequences: cursor movement, screen
/// clears, OSC 52 clipboard writes, or a bare newline fabricating extra rows in the peer table.
/// </para>
/// </remarks>
public static class UntrustedText
{
    /// <summary>
    /// Strips characters that can control a terminal or hide text, then bounds the length.
    /// </summary>
    /// <remarks>
    /// Stripping rather than rejecting the whole announcement is deliberate. Rejecting on one bad
    /// byte would let an attacker erase a real peer from the list by spoofing its identifier with
    /// a single poisoned character.
    /// </remarks>
    public static string Sanitize(string? value, int maxUtf8Bytes)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(maxUtf8Bytes);

        if (string.IsNullOrEmpty(value))
        {
            return string.Empty;
        }

        var cleaned = new StringBuilder(value.Length);

        foreach (var rune in value.EnumerateRunes())
        {
            // Cc covers C0/C1 controls and DEL, including ESC. Cf covers bidi overrides and
            // zero-width characters, which can visually reorder or hide a name. Zl/Zp are the
            // Unicode line and paragraph separators.
            if (Rune.GetUnicodeCategory(rune) is UnicodeCategory.Control
                or UnicodeCategory.Format
                or UnicodeCategory.LineSeparator
                or UnicodeCategory.ParagraphSeparator)
            {
                continue;
            }

            cleaned.Append(rune.ToString());
        }

        // Truncate after stripping: doing it first could leave a dangling partial sequence.
        return TruncateUtf8(cleaned.ToString().Trim(), maxUtf8Bytes);
    }

    private static string TruncateUtf8(string value, int maxUtf8Bytes)
    {
        if (Encoding.UTF8.GetByteCount(value) <= maxUtf8Bytes)
        {
            return value;
        }

        var truncated = new StringBuilder();
        var byteCount = 0;

        foreach (var rune in value.EnumerateRunes())
        {
            // Never split a multi-byte character in half.
            if (byteCount + rune.Utf8SequenceLength > maxUtf8Bytes)
            {
                break;
            }

            byteCount += rune.Utf8SequenceLength;
            truncated.Append(rune.ToString());
        }

        return truncated.ToString().TrimEnd();
    }
}
