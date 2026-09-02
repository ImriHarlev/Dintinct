namespace NetworkA.FileProcessing.Splitters;

/// <summary>
/// Write-only stream that counts bytes written without buffering any data.
/// Use for measuring encoded output size without allocating the buffer — works with
/// any encoder that writes forward-only (e.g. ImageMagick PNG output).
/// Not suitable for encoders that seek back to patch headers (e.g. ZIP-based formats).
/// </summary>
internal sealed class CountingStream : Stream
{
    private long _bytesWritten;

    public override bool CanWrite => true;
    public override bool CanRead  => false;
    public override bool CanSeek  => false;
    public override long Length   => _bytesWritten;
    public override long Position
    {
        get => _bytesWritten;
        set => throw new NotSupportedException();
    }

    public override void Write(byte[] buffer, int offset, int count) => _bytesWritten += count;
    public override void Write(ReadOnlySpan<byte> buffer)            => _bytesWritten += buffer.Length;
    public override void Flush() { }

    public override int  Read(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    public override long Seek(long offset, SeekOrigin origin)       => throw new NotSupportedException();
    public override void SetLength(long value)                      => throw new NotSupportedException();
}
