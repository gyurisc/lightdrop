namespace LightDrop.Daemon.Transfers;

/// <summary>
/// Reads at most a fixed number of bytes from another stream, then reports end of stream.
/// </summary>
/// <remarks>
/// What <see cref="FileSender"/> sends a file through, so the body always matches the
/// <c>Content-Length</c> taken when the file was opened. On macOS and Linux opening a file for
/// reading does not stop another process appending to it -- a log, a recording, a download still
/// in progress. Read to its end, such a file outgrows the length already declared: the receiver
/// takes exactly the declared bytes and saves them as complete, while the sender's HTTP stack fails
/// on the excess and reports a failed send for a file that arrived. Cut at the declared length,
/// both sides agree on a snapshot of the file as it was when the send began.
/// </remarks>
internal sealed class LengthLimitedStream(Stream inner, long length) : Stream
{
    private long _remaining = length;

    public override bool CanRead => true;

    public override bool CanSeek => false;

    public override bool CanWrite => false;

    public override long Length => throw new NotSupportedException();

    public override long Position
    {
        get => throw new NotSupportedException();
        set => throw new NotSupportedException();
    }

    public override int Read(byte[] buffer, int offset, int count) => Read(buffer.AsSpan(offset, count));

    public override int Read(Span<byte> buffer)
    {
        if (_remaining <= 0)
        {
            return 0;
        }

        var read = inner.Read(buffer[..(int)Math.Min(buffer.Length, _remaining)]);
        _remaining -= read;
        return read;
    }

    public override Task<int> ReadAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken) =>
        ReadAsync(buffer.AsMemory(offset, count), cancellationToken).AsTask();

    public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
    {
        if (_remaining <= 0)
        {
            return 0;
        }

        var read = await inner.ReadAsync(buffer[..(int)Math.Min(buffer.Length, _remaining)], cancellationToken)
            .ConfigureAwait(false);
        _remaining -= read;
        return read;
    }

    public override void Flush()
    {
    }

    public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();

    public override void SetLength(long value) => throw new NotSupportedException();

    public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
}
