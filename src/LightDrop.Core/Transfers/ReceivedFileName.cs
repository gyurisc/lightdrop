using System.Globalization;
using System.Text;
using LightDrop.Core.Discovery;

namespace LightDrop.Core.Transfers;

/// <summary>
/// Turns the file name a sending device claims into one that is safe to create in the download
/// folder, on any platform.
/// </summary>
/// <remarks>
/// <strong>The name is attacker-controlled.</strong> Only a paired device can send, but pairing
/// proves who it is, not that it is careful or uncompromised -- and this string ends up as an
/// argument to a call that creates a file. Pure and in Core so every rule is a test vector rather
/// than something only a real file system can show.
/// <para>
/// The same rules on every platform, Windows' included, even when receiving on a Mac: a file
/// saved under a name Windows cannot represent is a trap for whoever later copies it there, and
/// one rule set is one thing to get right instead of two.
/// </para>
/// <para>
/// This is the first line, not the only one. The daemon still checks that the final path's
/// directory <em>is</em> the download folder before it creates anything, so a rule missed here
/// fails closed.
/// </para>
/// </remarks>
public static class ReceivedFileName
{
    /// <summary>
    /// The longest name kept, in UTF-8 bytes. Below the 255-per-component limit of APFS, ext4 and
    /// NTFS, leaving room for a <c>" (9999)"</c> copy number.
    /// </summary>
    public const int MaxUtf8Bytes = 200;

    /// <summary>
    /// An extension longer than this is not kept whole when the name is capped: it is far more
    /// likely to be a dot in the middle of a long name than a real extension.
    /// </summary>
    private const int MaxExtensionUtf8Bytes = 32;

    /// <summary>
    /// Device names Windows resolves before the file system sees them -- <c>NUL.txt</c> opens the
    /// null device, not a file -- with or without an extension. Superset of the usual list: the
    /// <c>0</c> and superscript-digit ports and the console handles are reserved too.
    /// </summary>
    private static readonly HashSet<string> WindowsReservedNames = BuildReservedNames();

    /// <summary>
    /// Returns a safe name for <paramref name="claimed"/>, or null if nothing usable is left.
    /// </summary>
    /// <remarks>
    /// Rejecting rather than inventing a fallback name for an empty result: a sender that names a
    /// file <c>..</c> is not one whose bytes should land under a name it did not choose.
    /// </remarks>
    public static string? Sanitize(string? claimed)
    {
        if (string.IsNullOrEmpty(claimed))
        {
            return null;
        }

        // The last segment after either separator, whichever platform the sender is on. This is
        // what removes traversal: no separator survives, so nothing can name a parent or a root.
        var segment = claimed[(claimed.LastIndexOfAny(['/', '\\']) + 1)..];

        // Controls, bidi overrides, zero-width and line separators -- the set discovery already
        // strips from device names, for the same reason: this name is printed to a terminal and
        // shown in a file manager. Not length-bounded here: a cut now could drop the extension,
        // and the cap below keeps it. The input is already bounded by the metadata header's size.
        var name = UntrustedText.Sanitize(segment, maxUtf8Bytes: int.MaxValue);

        name = TrimTrailingDotsAndSpaces(ReplaceForbidden(name));
        if (name.Length == 0)
        {
            // "", ".", "..", and anything made only of what the steps above remove.
            return null;
        }

        // Prefixed before capping, so the cap cannot cut a prefix off; checked again after,
        // because capping and trimming can expose a reserved name that was not one before
        // ("CON" followed by a few hundred spaces and a letter).
        name = TrimTrailingDotsAndSpaces(Cap(PrefixIfReserved(name)));

        return name.Length == 0 ? null : PrefixIfReserved(name);
    }

    /// <summary>
    /// The name for the <paramref name="copy"/>th file of the same name: <c>report (1).pdf</c>.
    /// </summary>
    /// <remarks>
    /// The number goes before the last extension, as Windows Explorer and every browser's
    /// download manager do, so the copy still opens with the same application. A leading dot is
    /// not an extension: <c>.bashrc (1)</c>, not <c> (1).bashrc</c>.
    /// </remarks>
    public static string WithCopyNumber(string name, int copy)
    {
        ArgumentException.ThrowIfNullOrEmpty(name);
        ArgumentOutOfRangeException.ThrowIfNegative(copy);

        if (copy == 0)
        {
            return name;
        }

        var number = copy.ToString(CultureInfo.InvariantCulture);
        var dot = name.LastIndexOf('.');

        return dot > 0
            ? $"{name[..dot]} ({number}){name[dot..]}"
            : $"{name} ({number})";
    }

    private static string ReplaceForbidden(string name)
    {
        // What Windows refuses in a file name, plus controls -- already gone after UntrustedText,
        // and replaced here too so this step stands on its own. The separators cannot survive the
        // last-segment cut either; they are listed because this is the list Windows documents.
        var builder = new StringBuilder(name.Length);
        foreach (var ch in name)
        {
            builder.Append(ch is '<' or '>' or ':' or '"' or '/' or '\\' or '|' or '?' or '*' || char.IsControl(ch)
                ? '_'
                : ch);
        }

        return builder.ToString();
    }

    /// <summary>
    /// Windows drops trailing dots and spaces on create, so <c>a.</c> silently becomes <c>a</c> --
    /// and a file that got one some other way cannot be deleted from Explorer.
    /// </summary>
    private static string TrimTrailingDotsAndSpaces(string name) => name.TrimEnd('.', ' ');

    private static string PrefixIfReserved(string name)
    {
        // Windows matches the part before the first dot with trailing spaces ignored: "NUL .txt"
        // is the null device too.
        var dot = name.IndexOf('.', StringComparison.Ordinal);
        var stem = (dot < 0 ? name : name[..dot]).TrimEnd(' ');

        return WindowsReservedNames.Contains(stem) ? "_" + name : name;
    }

    private static string Cap(string name)
    {
        if (Encoding.UTF8.GetByteCount(name) <= MaxUtf8Bytes)
        {
            return name;
        }

        var dot = name.LastIndexOf('.');
        if (dot > 0)
        {
            var extension = name[dot..];
            var extensionBytes = Encoding.UTF8.GetByteCount(extension);

            if (extensionBytes <= MaxExtensionUtf8Bytes)
            {
                return TrimTrailingDotsAndSpaces(TruncateUtf8(name[..dot], MaxUtf8Bytes - extensionBytes)) + extension;
            }
        }

        return TruncateUtf8(name, MaxUtf8Bytes);
    }

    /// <summary>Cuts to at most <paramref name="maxBytes"/> of UTF-8, never inside a character.</summary>
    private static string TruncateUtf8(string value, int maxBytes)
    {
        var truncated = new StringBuilder();
        var bytes = 0;

        foreach (var rune in value.EnumerateRunes())
        {
            if (bytes + rune.Utf8SequenceLength > maxBytes)
            {
                break;
            }

            bytes += rune.Utf8SequenceLength;
            truncated.Append(rune.ToString());
        }

        return truncated.ToString();
    }

    private static HashSet<string> BuildReservedNames()
    {
        var names = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { "CON", "PRN", "AUX", "NUL", "CONIN$", "CONOUT$" };

        foreach (var digit in "0123456789¹²³")
        {
            names.Add($"COM{digit}");
            names.Add($"LPT{digit}");
        }

        return names;
    }
}
