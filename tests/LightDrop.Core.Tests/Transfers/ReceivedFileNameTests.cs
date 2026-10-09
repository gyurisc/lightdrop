using System.Text;
using LightDrop.Core.Transfers;

namespace LightDrop.Core.Tests.Transfers;

/// <summary>
/// Turning a file name another device claimed into one safe to create in the download folder.
/// </summary>
/// <remarks>
/// The sender is paired, but pairing proves who it is, not that it is careful -- or that it has
/// not been compromised. The name is attacker-controlled input to a file-creating call, and every
/// vector here is one that would otherwise write somewhere other than intended, write something
/// that cannot be opened or deleted on Windows, or drive the receiving user's terminal.
/// </remarks>
public sealed class ReceivedFileNameTests
{
    [Theory]
    [InlineData("report.pdf", "report.pdf")]
    [InlineData("holiday photo (2).jpg", "holiday photo (2).jpg")]
    [InlineData("Ünïcödé 文件.txt", "Ünïcödé 文件.txt")]
    [InlineData(".bashrc", ".bashrc")]
    [InlineData("archive.tar.gz", "archive.tar.gz")]
    public void KeepsAnOrdinaryNameAsItIs(string claimed, string expected) =>
        Assert.Equal(expected, ReceivedFileName.Sanitize(claimed));

    [Theory]
    [InlineData("../../etc/passwd", "passwd")]
    [InlineData("..\\..\\Windows\\System32\\drivers\\etc\\hosts", "hosts")]
    [InlineData("/etc/cron.d/evil", "evil")]
    [InlineData("C:\\Users\\Public\\evil.exe", "evil.exe")]
    [InlineData("subdir/../../escape.txt", "escape.txt")]
    [InlineData("mixed\\separators/both.txt", "both.txt")]
    [InlineData("\\\\server\\share\\unc.txt", "unc.txt")]
    public void KeepsOnlyTheLastSegmentWhicheverSeparatorTheSenderUsed(string claimed, string expected) =>
        Assert.Equal(expected, ReceivedFileName.Sanitize(claimed));

    [Theory]
    [InlineData("C:evil.txt", "C_evil.txt")]
    [InlineData("a<b>c.txt", "a_b_c.txt")]
    [InlineData("what?.txt", "what_.txt")]
    [InlineData("star*.txt", "star_.txt")]
    [InlineData("pipe|quote\".txt", "pipe_quote_.txt")]
    [InlineData("notes.txt:Zone.Identifier", "notes.txt_Zone.Identifier")]
    public void ReplacesCharactersWindowsForbids(string claimed, string expected) =>
        Assert.Equal(expected, ReceivedFileName.Sanitize(claimed));

    [Theory]
    [InlineData("evil\u001b[2J.txt", "evil[2J.txt")]
    [InlineData("line\nbreak.txt", "linebreak.txt")]
    [InlineData("nul\0byte.txt", "nulbyte.txt")]
    [InlineData("del\u007f.txt", "del.txt")]
    [InlineData("invoice\u202Etxt.exe", "invoicetxt.exe")]
    [InlineData("zero\u200Bwidth.txt", "zerowidth.txt")]
    [InlineData("para\u2029sep.txt", "parasep.txt")]
    public void StripsWhatUntrustedTextStrips(string claimed, string expected)
    {
        // The bidi override is the classic one: "invoice\u202Etxt.exe" renders as "invoiceexe.txt"
        // in a file manager while being an executable.
        Assert.Equal(expected, ReceivedFileName.Sanitize(claimed));
    }

    [Theory]
    [InlineData("trailing.", "trailing")]
    [InlineData("trailing. . .", "trailing")]
    [InlineData("spaces.txt   ", "spaces.txt")]
    [InlineData("   leading.txt", "leading.txt")]
    public void TrimsWhatWindowsWouldSilentlyDrop(string claimed, string expected)
    {
        // Windows strips trailing dots and spaces on create, so "a." becomes "a" behind the
        // program's back -- and a file created that way through other APIs cannot be deleted
        // from Explorer.
        Assert.Equal(expected, ReceivedFileName.Sanitize(claimed));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData(".")]
    [InlineData("..")]
    [InlineData("   ")]
    [InlineData(". .")]
    [InlineData("folder/")]
    [InlineData("folder\\")]
    [InlineData("../")]
    [InlineData("\u202E\u200B")]
    [InlineData("\u001b\u0007")]
    public void RefusesANameThatIsEmptyOnceMadeSafe(string? claimed) =>
        Assert.Null(ReceivedFileName.Sanitize(claimed));

    [Theory]
    [InlineData("CON", "_CON")]
    [InlineData("con", "_con")]
    [InlineData("PRN.txt", "_PRN.txt")]
    [InlineData("aux.tar.gz", "_aux.tar.gz")]
    [InlineData("NUL", "_NUL")]
    [InlineData("COM1", "_COM1")]
    [InlineData("com9.log", "_com9.log")]
    [InlineData("LPT1.txt", "_LPT1.txt")]
    [InlineData("LPT9", "_LPT9")]
    [InlineData("COM\u00b9.txt", "_COM\u00b9.txt")]
    [InlineData("CONIN$", "_CONIN$")]
    [InlineData("NUL .txt", "_NUL .txt")]
    [InlineData("nul.", "_nul")]
    public void PrefixesWindowsReservedDeviceNamesWithOrWithoutAnExtension(string claimed, string expected)
    {
        // "NUL.txt" opens the null device on Windows, not a file: the bytes vanish and the
        // sender is told they were saved.
        Assert.Equal(expected, ReceivedFileName.Sanitize(claimed));
    }

    [Theory]
    [InlineData("CONSOLE.txt")]
    [InlineData("COM10")]
    [InlineData("LPT.txt")]
    [InlineData("my CON.txt")]
    [InlineData("auxiliary")]
    public void LeavesNamesThatOnlyResembleReservedOnes(string claimed) =>
        Assert.Equal(claimed, ReceivedFileName.Sanitize(claimed));

    [Fact]
    public void CapsALongNameButKeepsItsExtension()
    {
        var claimed = new string('a', 1000) + ".pdf";

        var name = ReceivedFileName.Sanitize(claimed)!;

        Assert.EndsWith(".pdf", name, StringComparison.Ordinal);
        Assert.True(Encoding.UTF8.GetByteCount(name) <= ReceivedFileName.MaxUtf8Bytes);
        Assert.StartsWith("aaaa", name, StringComparison.Ordinal);
    }

    [Fact]
    public void CapsByBytesWithoutSplittingACharacter()
    {
        // Three bytes each in UTF-8. A cut mid-character would leave an invalid sequence that
        // macOS refuses outright and Windows turns into a replacement character.
        var claimed = new string('文', 500) + ".txt";

        var name = ReceivedFileName.Sanitize(claimed)!;

        Assert.EndsWith(".txt", name, StringComparison.Ordinal);
        Assert.True(Encoding.UTF8.GetByteCount(name) <= ReceivedFileName.MaxUtf8Bytes);
        Assert.DoesNotContain('\uFFFD', name);
        Assert.All(name[..^4], ch => Assert.Equal('文', ch));
    }

    [Fact]
    public void TreatsAnAbsurdExtensionAsPartOfTheName()
    {
        // Keeping a 900-byte "extension" would leave no room for the name at all.
        var claimed = "a." + new string('x', 900);

        var name = ReceivedFileName.Sanitize(claimed)!;

        Assert.True(Encoding.UTF8.GetByteCount(name) <= ReceivedFileName.MaxUtf8Bytes);
        Assert.StartsWith("a.xxx", name, StringComparison.Ordinal);
    }

    [Fact]
    public void DoesNotLeaveATrailingDotWhenTheCapLandsOnOne()
    {
        var claimed = new string('a', ReceivedFileName.MaxUtf8Bytes - 1) + "." + new string('b', 900);

        var name = ReceivedFileName.Sanitize(claimed)!;

        Assert.False(name.EndsWith('.'), name);
        Assert.False(name.EndsWith(' '), name);
    }

    [Fact]
    public void SanitizingIsIdempotent()
    {
        // The receiver may sanitize a name twice on different paths; the second pass must not
        // keep changing it (a reserved-name prefix stacking up, for instance).
        foreach (var claimed in new[] { "CON.txt", "../a<b>.txt", new string('x', 900) + ".md", "nul." })
        {
            var once = ReceivedFileName.Sanitize(claimed)!;
            Assert.Equal(once, ReceivedFileName.Sanitize(once));
        }
    }

    [Theory]
    [InlineData("report.pdf", 0, "report.pdf")]
    [InlineData("report.pdf", 1, "report (1).pdf")]
    [InlineData("report.pdf", 12, "report (12).pdf")]
    [InlineData("archive.tar.gz", 1, "archive.tar (1).gz")]
    [InlineData("README", 2, "README (2)")]
    [InlineData(".bashrc", 1, ".bashrc (1)")]
    public void NumbersACopyBeforeItsExtension(string name, int copy, string expected) =>
        Assert.Equal(expected, ReceivedFileName.WithCopyNumber(name, copy));

    [Fact]
    public void ANumberedCopyOfAMaximalNameStillFitsAFileSystemComponent()
    {
        // 255 is the per-component limit on APFS and ext4 (bytes) and NTFS (UTF-16 units).
        var name = ReceivedFileName.Sanitize(new string('文', 500) + ".txt")!;

        var numbered = ReceivedFileName.WithCopyNumber(name, 9999);

        Assert.True(Encoding.UTF8.GetByteCount(numbered) <= 255);
    }
}
