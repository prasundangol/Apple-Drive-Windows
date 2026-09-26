using AppleDrive.Application.Interfaces;
using AppleDrive.Domain.Entities;
using AppleDrive.Domain.Enums;
using AppleDrive.Domain.Media;
using AppleDrive.Domain.Results;

namespace AppleDrive.Testing;

/// <summary>
/// In-memory phone used to test the pipeline without hardware. Mirrors the real source's
/// constraints: one open stream at a time and forward-only streams.
/// </summary>
public sealed class FakeIPhonePhotoSource : IPhonePhotoSource
{
    private readonly Dictionary<string, FakeFile> _files = new(StringComparer.Ordinal);
    private readonly Dictionary<string, byte[]> _nextDelivery = new(StringComparer.Ordinal);
    private readonly Dictionary<string, byte[]> _thumbnails = new(StringComparer.Ordinal);
    private int _openStreams;

    public DeviceConnectionStatus ConnectStatus { get; set; } = DeviceConnectionStatus.Connected;

    /// <summary>When set, enumeration fails with this error.</summary>
    public AppError? EnumerationError { get; set; }

    /// <summary>Delay applied per enumerated file, to exercise progress and cancellation.</summary>
    public TimeSpan EnumerationDelayPerFile { get; set; }

    public DeviceInfo? ConnectedDevice { get; private set; }

    public int ConnectCount { get; private set; }

    public int DisconnectCount { get; private set; }

    public int MaxConcurrentStreams { get; private set; }

    /// <summary>Number of times any file was opened for reading.</summary>
    public int OpenCount { get; private set; }

    /// <summary>Adds a file to the fake phone and returns its asset.</summary>
    public PhotoAsset AddFile(string sourcePath, byte[] content, DateTimeOffset? createdAt = null, long? reportedSize = null, bool reportsSize = true)
    {
        var fileName = sourcePath[(sourcePath.LastIndexOf('/') + 1)..];
        var asset = new PhotoAsset
        {
            Id = $"o{_files.Count + 1}",
            PersistentId = $"pid-{_files.Count + 1}",
            FileName = fileName,
            SourcePath = sourcePath,
            MediaType = MediaFormats.GetMediaType(fileName),
            ReportedSize = reportsSize ? reportedSize ?? content.LongLength : null,
            CreatedAt = createdAt,
            MimeType = MediaFormats.GetMimeType(fileName),
        };
        _files[asset.Id] = new FakeFile(asset, content);
        return asset;
    }

    /// <summary>Makes reads of <paramref name="asset"/> fail after <paramref name="afterBytes"/> bytes.</summary>
    public void FailReadsOf(PhotoAsset asset, long afterBytes, ErrorKind kind = ErrorKind.DeviceDisconnected) =>
        _files[asset.Id] = _files[asset.Id] with { FailAfterBytes = afterBytes, FailureKind = kind };

    /// <summary>Removes an injected read failure, as if the problem went away.</summary>
    public void HealReadsOf(PhotoAsset asset) =>
        _files[asset.Id] = _files[asset.Id] with { FailAfterBytes = null };

    /// <summary>
    /// The next read of <paramref name="asset"/> delivers <paramref name="content"/> instead of its own
    /// bytes, once, like the real device handing over a previous file's data after an error.
    /// </summary>
    public void DeliverOnce(PhotoAsset asset, byte[] content) => _nextDelivery[asset.Id] = content;

    /// <summary>Called whenever a file is opened for reading, before the stream is returned.</summary>
    public Action<PhotoAsset>? OnOpen { get; set; }

    public Task<DeviceConnectionResult> ConnectAsync(DeviceInfo device, CancellationToken cancellationToken)
    {
        ConnectCount++;
        ConnectedDevice = ConnectStatus == DeviceConnectionStatus.Connected ? device : null;
        var error = ConnectStatus == DeviceConnectionStatus.Connected
            ? null
            : new AppError(ErrorKind.DeviceLockedOrUntrusted, "fake");
        return Task.FromResult(new DeviceConnectionResult(ConnectStatus, device, error));
    }

    public async Task<Result<IReadOnlyList<PhotoAsset>>> EnumerateAssetsAsync(IProgress<int>? progress, CancellationToken cancellationToken)
    {
        if (ConnectedDevice is null)
        {
            return new AppError(ErrorKind.DeviceNotFound, "Not connected.");
        }

        if (EnumerationError is not null)
        {
            return EnumerationError;
        }

        var assets = new List<PhotoAsset>();
        try
        {
            foreach (var file in _files.Values)
            {
                if (EnumerationDelayPerFile > TimeSpan.Zero)
                {
                    await Task.Delay(EnumerationDelayPerFile, cancellationToken);
                }

                cancellationToken.ThrowIfCancellationRequested();
                assets.Add(file.Asset);
                progress?.Report(assets.Count);
            }
        }
        catch (OperationCanceledException)
        {
            return AppError.Cancelled;
        }

        return assets;
    }

    /// <summary>Gives an asset a device thumbnail (by default assets have none).</summary>
    public void SetThumbnail(PhotoAsset asset, byte[] thumbnail) => _thumbnails[asset.Id] = thumbnail;

    /// <summary>Number of times any thumbnail was opened.</summary>
    public int ThumbnailOpenCount { get; private set; }

    public Task<Result<Stream>> OpenThumbnailAsync(PhotoAsset asset, CancellationToken cancellationToken)
    {
        if (ConnectedDevice is null)
        {
            return Task.FromResult(Result<Stream>.Failure(new AppError(ErrorKind.DeviceDisconnected, "Not connected.")));
        }

        if (!_thumbnails.TryGetValue(asset.Id, out var thumbnail))
        {
            return Task.FromResult(Result<Stream>.Failure(new AppError(ErrorKind.SourceUnavailable, "No thumbnail.")));
        }

        ThumbnailOpenCount++;
        var open = Interlocked.Increment(ref _openStreams);
        MaxConcurrentStreams = Math.Max(MaxConcurrentStreams, open);
        Stream stream = new FakeDeviceStream(new FakeFile(asset, thumbnail), () => Interlocked.Decrement(ref _openStreams));
        return Task.FromResult(Result<Stream>.Success(stream));
    }

    public Task<Result<Stream>> OpenAssetAsync(PhotoAsset asset, CancellationToken cancellationToken)
    {
        if (ConnectedDevice is null)
        {
            return Task.FromResult(Result<Stream>.Failure(new AppError(ErrorKind.DeviceDisconnected, "Not connected.")));
        }

        if (!_files.TryGetValue(asset.Id, out var file))
        {
            return Task.FromResult(Result<Stream>.Failure(new AppError(ErrorKind.SourceUnavailable, "No such object.")));
        }

        OpenCount++;
        OnOpen?.Invoke(asset);
        if (_nextDelivery.Remove(asset.Id, out var substitute))
        {
            file = file with { Content = substitute };
        }

        var open = Interlocked.Increment(ref _openStreams);
        MaxConcurrentStreams = Math.Max(MaxConcurrentStreams, open);
        Stream stream = new FakeDeviceStream(file, () => Interlocked.Decrement(ref _openStreams));
        return Task.FromResult(Result<Stream>.Success(stream));
    }

    public Task DisconnectAsync()
    {
        DisconnectCount++;
        ConnectedDevice = null;
        return Task.CompletedTask;
    }

    public ValueTask DisposeAsync() => ValueTask.CompletedTask;

    private sealed record FakeFile(PhotoAsset Asset, byte[] Content, long? FailAfterBytes = null, ErrorKind FailureKind = ErrorKind.DeviceDisconnected);

    private sealed class FakeDeviceStream(FakeFile file, Action onClosed) : Stream
    {
        private long _position;
        private bool _closed;

        public override bool CanRead => !_closed;

        public override bool CanSeek => false;

        public override bool CanWrite => false;

        public override long Length => throw new NotSupportedException();

        public override long Position
        {
            get => _position;
            set => throw new NotSupportedException();
        }

        public override int Read(byte[] buffer, int offset, int count)
        {
            ObjectDisposedException.ThrowIf(_closed, this);
            if (file.FailAfterBytes is { } limit && _position >= limit)
            {
                throw new SourceReadException(new AppError(file.FailureKind, "Simulated device failure."));
            }

            var available = file.Content.Length - _position;
            if (file.FailAfterBytes is { } failAt)
            {
                available = Math.Min(available, failAt - _position);
            }

            var toCopy = (int)Math.Min(count, available);
            Array.Copy(file.Content, _position, buffer, offset, toCopy);
            _position += toCopy;
            return toCopy;
        }

        public override void Flush()
        {
        }

        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();

        public override void SetLength(long value) => throw new NotSupportedException();

        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();

        protected override void Dispose(bool disposing)
        {
            if (!_closed)
            {
                _closed = true;
                onClosed();
            }

            base.Dispose(disposing);
        }
    }
}
