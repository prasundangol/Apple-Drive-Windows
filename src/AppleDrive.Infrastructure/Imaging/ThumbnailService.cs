using System.Runtime.InteropServices;
using System.Runtime.InteropServices.WindowsRuntime;
using System.Security.Cryptography;
using System.Text;
using AppleDrive.Application.Interfaces;
using AppleDrive.Application.Settings;
using AppleDrive.Domain.Entities;
using AppleDrive.Domain.Enums;
using Microsoft.Extensions.Logging;
using Windows.Graphics.Imaging;
using Windows.Storage;
using Windows.Storage.FileProperties;
using Windows.Storage.Streams;

namespace AppleDrive.Infrastructure.Imaging;

/// <summary>
/// Previews through a memory cache of in-flight requests (one generation per preview), then a disk cache,
/// then decoding only when needed.
/// </summary>
/// <remarks>
/// <para>Phone previews: the device keeps a small thumbnail for each item, but testing on a real
/// iPhone showed it is stored without orientation (portrait photos sideways, front-camera shots
/// mirrored) and, for saved or edited JPEGs, sometimes shows another version. So HEIC and JPEG
/// previews are decoded from the file itself, upright. PNG, WebP and GIF (never rotated) and videos
/// use the device thumbnail.</para>
/// <para>Destination previews come from the Windows shell, which also covers videos and uses the
/// system thumbnail cache.</para>
/// </remarks>
public sealed class ThumbnailService : IThumbnailService, IDisposable
{
    private readonly IPhonePhotoSource _source;
    private readonly ISettingsService _settings;
    private readonly IAppPaths _paths;
    private readonly ILogger<ThumbnailService> _logger;
    private readonly Dictionary<string, InFlight> _requests = new(StringComparer.Ordinal);
    private readonly Lock _gate = new();

    /// <summary>The phone serves one stream at a time; previews queue here rather than at the device.</summary>
    private readonly SemaphoreSlim _phone = new(1, 1);

    private readonly SemaphoreSlim _files = new(4, 4);

    public ThumbnailService(IPhonePhotoSource source, ISettingsService settings, IAppPaths paths, ILogger<ThumbnailService> logger)
    {
        _source = source;
        _settings = settings;
        _paths = paths;
        _logger = logger;
    }

    public Task<string?> GetPhoneThumbnailAsync(PhotoAsset asset, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(asset);
        var key = Key("phone", asset.PersistentId ?? asset.Id, asset.FileName, asset.ReportedSize?.ToString(System.Globalization.CultureInfo.InvariantCulture) ?? "?");
        return GetAsync(key, (target, token) => CreatePhoneThumbnailAsync(asset, target, token), cancellationToken);
    }

    public Task<string?> GetFileThumbnailAsync(string path, CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrEmpty(path);
        FileInfo info;
        try
        {
            info = new FileInfo(path);
            if (!info.Exists)
            {
                return Task.FromResult<string?>(null);
            }
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or ArgumentException)
        {
            return Task.FromResult<string?>(null);
        }

        var key = Key("file", info.FullName.ToUpperInvariant(), info.Length.ToString(System.Globalization.CultureInfo.InvariantCulture), info.LastWriteTimeUtc.Ticks.ToString(System.Globalization.CultureInfo.InvariantCulture));
        return GetAsync(key, (target, token) => CreateFileThumbnailAsync(info.FullName, target, token), cancellationToken);
    }

    public void Dispose()
    {
        _phone.Dispose();
        _files.Dispose();
    }

    private string CacheFolder => _settings.Current.ThumbnailCacheFolder ?? _paths.DefaultThumbnailCacheFolder;

    private async Task<string?> GetAsync(string key, Func<string, CancellationToken, Task<bool>> create, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var target = Path.Combine(CacheFolder, key[..2], key + ".jpg");
        if (File.Exists(target))
        {
            return target;
        }

        // One generation per preview, however many callers ask for it at once. It is cancelled only
        // when every caller waiting for it has given up (for example, all scrolled away).
        InFlight request;
        lock (_gate)
        {
            // A generation already being cancelled (its callers left) is not joined: start afresh.
            if (!_requests.TryGetValue(key, out request!) || request.Cancellation.IsCancellationRequested)
            {
                var cancellation = new CancellationTokenSource();
                var temporary = $"{target}.{Guid.NewGuid():N}.tmp";
                request = new InFlight(
                    Task.Run(() => GenerateAsync(target, temporary, token => create(temporary, token), cancellation.Token), CancellationToken.None),
                    cancellation);
                _requests[key] = request;
                var started = request;
                _ = started.Task.ContinueWith(
                    _ =>
                    {
                        lock (_gate)
                        {
                            if (_requests.TryGetValue(key, out var current) && current == started)
                            {
                                _requests.Remove(key);
                            }
                        }

                        started.Cancellation.Dispose();
                    },
                    CancellationToken.None,
                    TaskContinuationOptions.ExecuteSynchronously,
                    TaskScheduler.Default);
            }

            request.Waiters++;
        }

        try
        {
            var result = await request.Task.WaitAsync(cancellationToken).ConfigureAwait(false);

            // A generation stopped by cancellation yields no preview; report it as cancelled, not as "none".
            if (result is null)
            {
                cancellationToken.ThrowIfCancellationRequested();
            }

            return result;
        }
        finally
        {
            lock (_gate)
            {
                if (--request.Waiters == 0 && !request.Task.IsCompleted)
                {
                    request.Cancellation.Cancel();
                }
            }
        }
    }

    private sealed class InFlight(Task<string?> task, CancellationTokenSource cancellation)
    {
        public Task<string?> Task { get; } = task;

        public CancellationTokenSource Cancellation { get; } = cancellation;

        public int Waiters { get; set; }
    }

    /// <summary>Writes the preview to <paramref name="temporary"/> through <paramref name="createTemporary"/>, then moves it into place.</summary>
    private async Task<string?> GenerateAsync(string target, string temporary, Func<CancellationToken, Task<bool>> createTemporary, CancellationToken cancellationToken)
    {
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(target)!);
            if (!await createTemporary(cancellationToken).ConfigureAwait(false))
            {
                return null;
            }

            File.Move(temporary, target, overwrite: true);
            return target;
        }
        catch (OperationCanceledException)
        {
            return null;
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or COMException)
        {
            _logger.LogDebug(exception, "Could not create a preview");
            return null;
        }
        finally
        {
            // Only left behind when the preview wasn't completed; it is our own cache file.
            try
            {
                File.Delete(temporary);
            }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
            {
                _logger.LogDebug(exception, "Could not remove an unfinished preview");
            }
        }
    }

    private async Task<bool> CreatePhoneThumbnailAsync(PhotoAsset asset, string target, CancellationToken cancellationToken)
    {
        await _phone.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var decodeFile = asset.MediaType == MediaType.Image && asset.Extension is not (".png" or ".webp" or ".gif" or ".bmp");
            if (decodeFile && await EncodeFromPhoneAsync(asset, thumbnail: false, target, cancellationToken).ConfigureAwait(false))
            {
                return true;
            }

            // Videos and unrotated formats, or a file whose format can't be decoded here.
            return await EncodeFromPhoneAsync(asset, thumbnail: true, target, cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            _phone.Release();
        }
    }

    private async Task<bool> EncodeFromPhoneAsync(PhotoAsset asset, bool thumbnail, string target, CancellationToken cancellationToken)
    {
        var open = thumbnail
            ? await _source.OpenThumbnailAsync(asset, cancellationToken).ConfigureAwait(false)
            : await _source.OpenAssetAsync(asset, cancellationToken).ConfigureAwait(false);
        if (!open.IsSuccess)
        {
            return false;
        }

        using var buffer = new MemoryStream();
        try
        {
            await using var stream = open.Value;
            await stream.CopyToAsync(buffer, 81920, cancellationToken).ConfigureAwait(false);
        }
        catch (IOException exception)
        {
            _logger.LogDebug(exception, "Could not read a phone file for its preview");
            return false;
        }

        buffer.Position = 0;
        return await EncodeAsync(buffer, target, cancellationToken).ConfigureAwait(false);
    }

    private async Task<bool> CreateFileThumbnailAsync(string path, string target, CancellationToken cancellationToken)
    {
        await _files.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var file = await StorageFile.GetFileFromPathAsync(path).AsTask(cancellationToken).ConfigureAwait(false);
            using var thumbnail = await file.GetThumbnailAsync(ThumbnailMode.SingleItem, IThumbnailService.Size, ThumbnailOptions.ResizeThumbnail)
                .AsTask(cancellationToken).ConfigureAwait(false);
            if (thumbnail is null)
            {
                return false;
            }

            await using var stream = thumbnail.AsStreamForRead();
            return await EncodeAsync(stream, target, cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            _files.Release();
        }
    }

    /// <summary>Decodes any image, upright, and writes a JPEG whose longest edge is at most <see cref="IThumbnailService.Size"/>.</summary>
    private async Task<bool> EncodeAsync(Stream image, string target, CancellationToken cancellationToken)
    {
        try
        {
            using var input = image.AsRandomAccessStream();
            var decoder = await BitmapDecoder.CreateAsync(input).AsTask(cancellationToken).ConfigureAwait(false);

            // Scaling applies to the stored pixels, before the EXIF rotation.
            var scale = Math.Min(1.0, (double)IThumbnailService.Size / Math.Max(decoder.PixelWidth, decoder.PixelHeight));
            var transform = new BitmapTransform
            {
                ScaledWidth = (uint)Math.Max(1, Math.Round(decoder.PixelWidth * scale)),
                ScaledHeight = (uint)Math.Max(1, Math.Round(decoder.PixelHeight * scale)),
                InterpolationMode = BitmapInterpolationMode.Fant,
            };
            using var bitmap = await decoder.GetSoftwareBitmapAsync(
                    BitmapPixelFormat.Bgra8,
                    BitmapAlphaMode.Ignore,
                    transform,
                    ExifOrientationMode.RespectExifOrientation,
                    ColorManagementMode.ColorManageToSRgb)
                .AsTask(cancellationToken)
                .ConfigureAwait(false);

            using var output = new InMemoryRandomAccessStream();
            var options = new BitmapPropertySet { ["ImageQuality"] = new BitmapTypedValue(0.85, Windows.Foundation.PropertyType.Single) };
            var encoder = await BitmapEncoder.CreateAsync(BitmapEncoder.JpegEncoderId, output, options).AsTask(cancellationToken).ConfigureAwait(false);
            encoder.SetSoftwareBitmap(bitmap);
            await encoder.FlushAsync().AsTask(cancellationToken).ConfigureAwait(false);

            var bytes = new byte[output.Size];
            output.Seek(0);
            await output.ReadAsync(bytes.AsBuffer(), (uint)bytes.Length, InputStreamOptions.None).AsTask(cancellationToken).ConfigureAwait(false);
            await File.WriteAllBytesAsync(target, bytes, cancellationToken).ConfigureAwait(false);
            return true;
        }
        catch (Exception exception) when (exception is COMException or ArgumentException or InvalidOperationException)
        {
            _logger.LogDebug("Image could not be decoded for a preview (0x{HResult:X8})", exception.HResult);
            return false;
        }
    }

    private static string Key(params string[] parts) =>
        Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(string.Join('\n', parts))))[..40];
}
