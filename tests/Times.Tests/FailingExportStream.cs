namespace Times.Tests;

/// <summary>
/// A write-only <see cref="Stream"/> around a real one that fails halfway: the first <c>Write</c> writes the
/// first half of the buffer to the inner stream, flushes it, runs <c>beforeThrow</c>, then throws
/// <c>IOException("disk full")</c>. Disposing it disposes the inner stream.
/// </summary>
internal sealed class FailingExportStream : Stream
{
    private readonly Stream inner;
    private readonly Action? beforeThrow;

    public FailingExportStream(Stream inner, Action? beforeThrow = null)
    {
        this.inner = inner;
        this.beforeThrow = beforeThrow;
    }

    /// <summary>How many bytes reached the inner stream before the failure.</summary>
    public int BytesWritten { get; private set; }

    public override bool CanRead => false;

    public override bool CanSeek => false;

    public override bool CanWrite => true;

    public override long Length => throw new NotSupportedException();

    public override long Position
    {
        get => throw new NotSupportedException();
        set => throw new NotSupportedException();
    }

    public override void Write(byte[] buffer, int offset, int count) => Write(buffer.AsSpan(offset, count));

    public override void Write(ReadOnlySpan<byte> buffer)
    {
        ReadOnlySpan<byte> half = buffer[..(buffer.Length / 2)];
        inner.Write(half);
        BytesWritten += half.Length;
        inner.Flush();
        beforeThrow?.Invoke();
        throw new IOException("disk full");
    }

    public override void Flush() => inner.Flush();

    public override int Read(byte[] buffer, int offset, int count) => throw new NotSupportedException();

    public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();

    public override void SetLength(long value) => throw new NotSupportedException();

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            inner.Dispose();
        }

        base.Dispose(disposing);
    }
}
