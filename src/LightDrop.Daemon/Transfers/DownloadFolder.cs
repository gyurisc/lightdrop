using System.Buffers;
using LightDrop.Core.Configuration;
using LightDrop.Core.Transfers;

namespace LightDrop.Daemon.Transfers;

/// <summary>
/// Where received files land, and how one gets there without ever appearing half-written or
/// replacing a file already there.
/// </summary>
/// <remarks>
/// <strong>Temp file, then move.</strong> The body streams into a hidden temp file in the
/// download folder itself -- the same volume, so the final move is a rename, not a copy. Only a
/// complete body is moved; an aborted or short one is deleted, and nothing ever appears under the
/// final name.
/// <para>
/// <strong>Never overwrite, even under a race.</strong> The final name is <em>reserved</em> by
/// creating it with <see cref="FileMode.CreateNew"/> -- one atomic create-if-absent on every
/// platform (<c>O_CREAT|O_EXCL</c>, <c>CREATE_NEW</c>) -- and only then is the temp file moved
/// over that reservation. Two transfers of the same name finishing at once each reserve a
/// different name. The alternative, <c>File.Move</c> without overwrite, is atomic on Windows but
/// on Unix is only as atomic as the runtime's fallback for the file system in use (no hard links
/// on exFAT, say), which a USB-stick download folder makes a real case.
/// </para>
/// </remarks>
internal sealed class DownloadFolder(IConfigStore config)
{
    /// <summary>"name (9999).ext" and then give up rather than loop for ever.</summary>
    private const int MaxCopies = 9999;

    private const int CopyBufferBytes = 81920;

    /// <summary>The folder received files go to now. Re-read per file, so an edit to config.json applies at once.</summary>
    public async Task<string> ResolveAsync(CancellationToken cancellationToken)
    {
        var settings = await config.LoadAsync(cancellationToken).ConfigureAwait(false);

        return Resolve(settings.DownloadFolder, Environment.GetFolderPath(Environment.SpecialFolder.UserProfile));
    }

    /// <summary>
    /// <paramref name="configured"/> as a full path: <c>~</c> and relative paths are taken from
    /// <paramref name="home"/>, and unset means <c>home/Downloads</c>.
    /// </summary>
    /// <remarks>
    /// Relative to home rather than to the daemon's working directory, which nobody editing
    /// config.json can know. Always <c>Downloads</c> under the home directory by default, even on
    /// Windows where the known folder can be redirected: right in the common case, and the user
    /// can set it in config.json otherwise.
    /// </remarks>
    internal static string Resolve(string? configured, string home)
    {
        var value = configured?.Trim();
        if (string.IsNullOrEmpty(value))
        {
            return Path.Combine(home, "Downloads");
        }

        if (value == "~")
        {
            return Path.GetFullPath(home);
        }

        if (value.StartsWith("~/", StringComparison.Ordinal) || value.StartsWith("~\\", StringComparison.Ordinal))
        {
            value = value[2..];
        }

        return Path.TrimEndingDirectorySeparator(Path.GetFullPath(value, home));
    }

    /// <summary>Whether <paramref name="path"/> names an entry directly inside <paramref name="folder"/>.</summary>
    /// <remarks>
    /// The last check before anything is created, independent of how the name was made safe: if
    /// <see cref="ReceivedFileName"/> ever lets a separator or a <c>..</c> through, this fails
    /// closed rather than writing outside the folder. Compares the fully resolved parent, so
    /// <c>folder/../x</c> and <c>folder/sub/x</c> are both refused.
    /// </remarks>
    internal static bool IsDirectlyInside(string folder, string path) =>
        string.Equals(
            Path.GetDirectoryName(Path.GetFullPath(path)),
            Path.TrimEndingDirectorySeparator(Path.GetFullPath(folder)),
            StringComparison.Ordinal);

    /// <summary>
    /// Streams exactly <paramref name="length"/> bytes of <paramref name="body"/> into the download
    /// folder under <paramref name="fileName"/>, or a numbered copy of it.
    /// </summary>
    /// <returns>The name used, and the folder it is in.</returns>
    /// <param name="fileName">Already sanitized by <see cref="ReceivedFileName.Sanitize"/>.</param>
    /// <exception cref="IOException">
    /// The body ended early or the file could not be written. Nothing is left behind either way.
    /// </exception>
    public async Task<(string SavedAs, string Folder)> SaveAsync(
        string fileName,
        Stream body,
        long length,
        CancellationToken cancellationToken)
    {
        var folder = await ResolveAsync(cancellationToken).ConfigureAwait(false);
        Directory.CreateDirectory(folder);

        // Hidden on macOS and Linux by its leading dot; the extension says what it is to anyone
        // who sees one left by a crash. A fresh GUID, so it can never collide with anything.
        var temp = Path.Combine(folder, $".lightdrop-{Guid.NewGuid():n}.partial");

        try
        {
            var file = new FileStream(temp, new FileStreamOptions
            {
                Mode = FileMode.CreateNew,
                Access = FileAccess.Write,
                Share = FileShare.None,
                Options = FileOptions.Asynchronous,
            });

            await using (file.ConfigureAwait(false))
            {
                var received = await CopyAsync(body, file, length, cancellationToken).ConfigureAwait(false);
                if (received != length)
                {
                    throw new IOException($"The body ended after {received} of {length} bytes.");
                }
            }

            return (MoveIntoPlace(folder, fileName, temp), folder);
        }
        catch
        {
            TryDelete(temp);
            throw;
        }
    }

    private static async Task<long> CopyAsync(Stream source, Stream destination, long length, CancellationToken cancellationToken)
    {
        var buffer = ArrayPool<byte>.Shared.Rent(CopyBufferBytes);
        try
        {
            long total = 0;
            int read;
            while ((read = await source.ReadAsync(buffer, cancellationToken).ConfigureAwait(false)) > 0)
            {
                total += read;

                // Kestrel already holds the body to its Content-Length, which the handler checked
                // equals this; counted again so this method's promise does not rest on that.
                if (total > length)
                {
                    throw new IOException($"The body is longer than the declared {length} bytes.");
                }

                await destination.WriteAsync(buffer.AsMemory(0, read), cancellationToken).ConfigureAwait(false);
            }

            return total;
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(buffer);
        }
    }

    private static string MoveIntoPlace(string folder, string fileName, string temp)
    {
        for (var copy = 0; copy <= MaxCopies; copy++)
        {
            var candidate = ReceivedFileName.WithCopyNumber(fileName, copy);
            var destination = Path.Combine(folder, candidate);

            if (!IsDirectlyInside(folder, destination))
            {
                throw new InvalidOperationException(
                    $"Refusing to save '{candidate}': it does not resolve to a file directly inside the download folder.");
            }

            if (!TryReserve(destination))
            {
                continue;
            }

            try
            {
                // Over the empty reservation this transfer just created and nobody else can have:
                // overwrite is what makes it a replace rather than a second create-if-absent.
                File.Move(temp, destination, overwrite: true);
            }
            catch
            {
                TryDelete(destination);
                throw;
            }

            return candidate;
        }

        throw new IOException($"There are already {MaxCopies} files named like '{fileName}' in the download folder.");
    }

    /// <summary>Atomically creates an empty <paramref name="path"/>, or reports that something is already there.</summary>
    private static bool TryReserve(string path)
    {
        try
        {
            new FileStream(path, FileMode.CreateNew, FileAccess.Write, FileShare.None).Dispose();
            return true;
        }
        catch (Exception ex) when ((ex is IOException or UnauthorizedAccessException) && Path.Exists(path))
        {
            // Taken -- by a file, or a folder of that name (which Windows reports as access
            // denied). Any other failure, a full disk or a read-only folder, is not a reason to
            // try the next number, and propagates.
            return false;
        }
    }

    private static void TryDelete(string path)
    {
        try
        {
            File.Delete(path);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // Best effort while already failing; the original exception is the one that matters.
        }
    }
}
