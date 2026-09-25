using AppleDrive.Application.Interfaces;
using AppleDrive.Infrastructure.Iphone.Wpd.Interop;

namespace AppleDrive.Infrastructure.Iphone.Wpd;

/// <summary>
/// Forward-only, read-only <see cref="Stream"/> over a WPD resource stream.
/// The COM stream is synchronous, so async reads run on the thread pool.
/// Disposing releases the device's single stream slot immediately.
/// </summary>
internal sealed class WpdReadStream : Stream
{
    private readonly SemaphoreSlim _deviceLock;
    private readonly Action _onClosed;
    private NativeComStream? _stream;
    private long _position;

    public WpdReadStream(NativeComStream stream, SemaphoreSlim deviceLock, int optimalBufferSize, Action onClosed)
    {
        _stream = stream;
        _deviceLock = deviceLock;
        _onClosed = onClosed;
        OptimalBufferSize = optimalBufferSize;
    }

    /// <summary>Buffer size the driver recommends for reads.</summary>
    public int OptimalBufferSize { get; }

    public override bool CanRead => _stream is not null;

    public override bool CanSeek => false;

    public override bool CanWrite => false;

    public override long Length => throw new NotSupportedException("Device streams have no reliable length.");

    public override long Position
    {
        get => _position;
        set => throw new NotSupportedException();
    }

    public override int Read(byte[] buffer, int offset, int count)
    {
        ValidateBufferArguments(buffer, offset, count);
        return Read(buffer.AsSpan(offset, count));
    }

    public override int Read(Span<byte> buffer)
    {
        var stream = _stream ?? throw new ObjectDisposedException(nameof(WpdReadStream));
        if (buffer.IsEmpty)
        {
            return 0;
        }

        int hr;
        uint read;
        _deviceLock.Wait();
        try
        {
            hr = stream.Read(buffer, out read);
        }
        finally
        {
            _deviceLock.Release();
        }

        if (WpdErrors.Failed(hr))
        {
            throw new SourceReadException(WpdErrors.FromHResult(hr, "Reading from device"));
        }

        _position += read;
        return (int)read;
    }

    public override Task<int> ReadAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken)
    {
        ValidateBufferArguments(buffer, offset, count);
        return ReadAsync(buffer.AsMemory(offset, count), cancellationToken).AsTask();
    }

    public override ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        return new ValueTask<int>(Task.Run(() => Read(buffer.Span), cancellationToken));
    }

    public override void Flush()
    {
    }

    public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();

    public override void SetLength(long value) => throw new NotSupportedException();

    public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();

    protected override void Dispose(bool disposing)
    {
        var stream = Interlocked.Exchange(ref _stream, null);
        if (stream is not null)
        {
            _deviceLock.Wait();
            try
            {
                stream.Dispose();
            }
            finally
            {
                _deviceLock.Release();
                _onClosed();
            }
        }

        base.Dispose(disposing);
    }
}
