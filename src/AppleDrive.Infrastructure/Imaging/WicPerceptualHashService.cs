using System.Runtime.InteropServices;
using AppleDrive.Application.Interfaces;
using Microsoft.Extensions.Logging;
using Windows.Graphics.Imaging;

namespace AppleDrive.Infrastructure.Imaging;

/// <summary>
/// Difference hashes (dHash) over Windows Imaging Component decoders, which cover JPEG, PNG, GIF,
/// TIFF, BMP, WebP and, with the free HEIF/HEVC extensions, HEIC.
/// </summary>
/// <remarks>
/// The decoder scales the image with the Fant (area-averaging) filter, which JPEG decoders do
/// cheaply at decode time, and applies the EXIF orientation. A difference hash has one bit per
/// horizontally adjacent pixel pair, set where the left pixel is brighter: 9×8 pixels give 64
/// bits, 17×16 give 256. It is unchanged by resizing, recompression and uniform brightness
/// changes, and differs strongly between different pictures. Aspect ratio is not kept at this
/// scale, so a cropped copy differs.
/// </remarks>
public sealed class WicPerceptualHashService(ILogger<WicPerceptualHashService> logger) : IPerceptualHashService
{
    /// <summary>Encoded images above this are not fingerprinted from a non-seekable stream.</summary>
    private const long MaxBufferedBytes = 256L * 1024 * 1024;

    private static readonly (BitmapRotation Rotation, BitmapFlip Flip)[] AllOrientations =
    [
        .. new[] { BitmapRotation.None, BitmapRotation.Clockwise90Degrees, BitmapRotation.Clockwise180Degrees, BitmapRotation.Clockwise270Degrees }
            .SelectMany(rotation => new[] { (rotation, BitmapFlip.None), (rotation, BitmapFlip.Horizontal) }),
    ];

    public async Task<ImageFingerprint?> ComputeAsync(Stream image, CancellationToken cancellationToken)
    {
        await using var seekable = await SeekableAsync(image, cancellationToken).ConfigureAwait(false);
        if (seekable is null)
        {
            return null;
        }

        return await DecodeAsync(
            seekable,
            async decoder =>
            {
                var hash = await HashAsync(decoder, 8, BitmapRotation.None, BitmapFlip.None, cancellationToken).ConfigureAwait(false);
                var detail = await HashAsync(decoder, 16, BitmapRotation.None, BitmapFlip.None, cancellationToken).ConfigureAwait(false);
                return hash is null || detail is null
                    ? null
                    : new ImageFingerprint(
                        hash[0],
                        new DetailHash(detail[0], detail[1], detail[2], detail[3]),
                        (int)decoder.OrientedPixelWidth,
                        (int)decoder.OrientedPixelHeight);
            },
            cancellationToken).ConfigureAwait(false);
    }

    public async Task<ImageFingerprint?> ComputeFileAsync(string path, CancellationToken cancellationToken)
    {
        await using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read, 64 * 1024, FileOptions.Asynchronous);
        return await ComputeAsync(stream, cancellationToken).ConfigureAwait(false);
    }

    public async Task<IReadOnlyList<ulong>?> ComputeAllOrientationsAsync(Stream image, CancellationToken cancellationToken)
    {
        await using var seekable = await SeekableAsync(image, cancellationToken).ConfigureAwait(false);
        if (seekable is null)
        {
            return null;
        }

        return await DecodeAsync<IReadOnlyList<ulong>>(
            seekable,
            async decoder =>
            {
                var hashes = new List<ulong>(AllOrientations.Length);
                foreach (var (rotation, flip) in AllOrientations)
                {
                    if (await HashAsync(decoder, 8, rotation, flip, cancellationToken).ConfigureAwait(false) is not { } hash)
                    {
                        return null;
                    }

                    hashes.Add(hash[0]);
                }

                return hashes;
            },
            cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    /// The difference hash of a (<paramref name="rows"/> + 1) × <paramref name="rows"/> image given
    /// as Bgra8 pixels, as <c>rows × rows</c> bits packed into 64-bit words.
    /// </summary>
    internal static ulong[] DifferenceHash(ReadOnlySpan<byte> bgra, int rows)
    {
        var columns = rows + 1;
        var luminance = new int[columns * rows];
        for (var i = 0; i < luminance.Length; i++)
        {
            var pixel = bgra.Slice(i * 4, 4);
            luminance[i] = (pixel[2] * 299) + (pixel[1] * 587) + (pixel[0] * 114);
        }

        var words = new ulong[Math.Max(1, rows * rows / 64)];
        var bit = 0;
        for (var row = 0; row < rows; row++)
        {
            for (var column = 0; column < columns - 1; column++, bit++)
            {
                if (luminance[(row * columns) + column] > luminance[(row * columns) + column + 1])
                {
                    words[bit / 64] |= 1UL << (bit % 64);
                }
            }
        }

        return words;
    }

    private static async Task<ulong[]?> HashAsync(BitmapDecoder decoder, int rows, BitmapRotation rotation, BitmapFlip flip, CancellationToken cancellationToken)
    {
        // WIC scales before it rotates, so a quarter turn scales to the transposed size.
        var columns = rows + 1;
        var quarterTurn = rotation is BitmapRotation.Clockwise90Degrees or BitmapRotation.Clockwise270Degrees;
        var transform = new BitmapTransform
        {
            ScaledWidth = (uint)(quarterTurn ? rows : columns),
            ScaledHeight = (uint)(quarterTurn ? columns : rows),
            Rotation = rotation,
            Flip = flip,
            InterpolationMode = BitmapInterpolationMode.Fant,
        };
        var pixels = await decoder.GetPixelDataAsync(
                BitmapPixelFormat.Bgra8,
                BitmapAlphaMode.Ignore,
                transform,
                ExifOrientationMode.RespectExifOrientation,
                ColorManagementMode.DoNotColorManage)
            .AsTask(cancellationToken)
            .ConfigureAwait(false);
        var data = pixels.DetachPixelData();
        return data.Length < columns * rows * 4 ? null : DifferenceHash(data, rows);
    }

    /// <summary>The stream itself when seekable; otherwise (device streams) a buffered copy, or <c>null</c> if too large.</summary>
    private static async Task<Stream?> SeekableAsync(Stream image, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(image);
        if (image.CanSeek)
        {
            return new NonClosingStream(image);
        }

        var buffer = new MemoryStream();
        await image.CopyToAsync(buffer, 81920, cancellationToken).ConfigureAwait(false);
        if (buffer.Length > MaxBufferedBytes)
        {
            await buffer.DisposeAsync().ConfigureAwait(false);
            return null;
        }

        buffer.Position = 0;
        return buffer;
    }

    private async Task<T?> DecodeAsync<T>(Stream stream, Func<BitmapDecoder, Task<T?>> read, CancellationToken cancellationToken)
        where T : class
    {
        try
        {
            using var randomAccess = stream.AsRandomAccessStream();
            var decoder = await BitmapDecoder.CreateAsync(randomAccess).AsTask(cancellationToken).ConfigureAwait(false);
            return await read(decoder).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception exception) when (exception is COMException or ArgumentException or InvalidOperationException or IOException)
        {
            // WINCODEC_ERR_COMPONENTNOTFOUND (no codec), bad image data, and so on.
            logger.LogDebug("Image could not be decoded for a visual fingerprint (0x{HResult:X8})", exception.HResult);
            return null;
        }
    }

    /// <summary>Lets the caller keep ownership of a stream it passed in.</summary>
    private sealed class NonClosingStream(Stream inner) : Stream
    {
        public override bool CanRead => inner.CanRead;

        public override bool CanSeek => inner.CanSeek;

        public override bool CanWrite => false;

        public override long Length => inner.Length;

        public override long Position
        {
            get => inner.Position;
            set => inner.Position = value;
        }

        public override int Read(byte[] buffer, int offset, int count) => inner.Read(buffer, offset, count);

        public override ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default) =>
            inner.ReadAsync(buffer, cancellationToken);

        public override long Seek(long offset, SeekOrigin origin) => inner.Seek(offset, origin);

        public override void Flush()
        {
        }

        public override void SetLength(long value) => throw new NotSupportedException();

        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    }
}
