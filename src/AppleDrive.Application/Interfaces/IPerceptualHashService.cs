using System.Numerics;

namespace AppleDrive.Application.Interfaces;

/// <summary>A 256-bit perceptual hash: slower to get, but far more selective than the 64-bit one.</summary>
public readonly record struct DetailHash(ulong A, ulong B, ulong C, ulong D)
{
    /// <summary>Number of differing bits (0–256).</summary>
    public int DistanceTo(DetailHash other) =>
        BitOperations.PopCount(A ^ other.A) + BitOperations.PopCount(B ^ other.B)
        + BitOperations.PopCount(C ^ other.C) + BitOperations.PopCount(D ^ other.D);
}

/// <summary>A visual fingerprint of an upright image: similar-looking images have similar hashes.</summary>
/// <param name="Hash">64-bit perceptual hash, cheap to compare across a whole library.</param>
/// <param name="Detail">256-bit perceptual hash, to confirm a close 64-bit match.</param>
/// <param name="Width">Displayed width (after EXIF orientation).</param>
/// <param name="Height">Displayed height (after EXIF orientation).</param>
public sealed record ImageFingerprint(ulong Hash, DetailHash Detail, int Width, int Height)
{
    /// <summary>Number of differing bits (0–64): 0 means visually the same at fingerprint scale.</summary>
    public static int Distance(ulong a, ulong b) => BitOperations.PopCount(a ^ b);
}

/// <summary>Computes perceptual hashes of images. Never used for videos.</summary>
public interface IPerceptualHashService
{
    /// <summary>
    /// Fingerprints an encoded image. Returns <c>null</c> when it can't be decoded: a corrupt
    /// file, or a format whose codec isn't installed (HEIC needs the HEIF and HEVC extensions).
    /// </summary>
    Task<ImageFingerprint?> ComputeAsync(Stream image, CancellationToken cancellationToken);

    /// <inheritdoc cref="ComputeAsync(Stream, CancellationToken)"/>
    Task<ImageFingerprint?> ComputeFileAsync(string path, CancellationToken cancellationToken);

    /// <summary>
    /// 64-bit hashes of the image in all 8 orientations (4 rotations, each also mirrored). For
    /// iPhone thumbnails, which are stored as the sensor saw them and without an orientation tag:
    /// a portrait photo's thumbnail is sideways, and a front-camera one is also mirrored. Returns
    /// <c>null</c> when the image can't be decoded.
    /// </summary>
    Task<IReadOnlyList<ulong>?> ComputeAllOrientationsAsync(Stream image, CancellationToken cancellationToken);
}
