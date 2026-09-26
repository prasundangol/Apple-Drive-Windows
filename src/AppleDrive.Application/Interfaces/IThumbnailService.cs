using AppleDrive.Domain.Entities;

namespace AppleDrive.Application.Interfaces;

/// <summary>
/// Small upright previews (JPEG, longest edge <see cref="Size"/> px) of phone media and destination
/// files. Generated asynchronously on first request and kept in a disk cache, so each preview is
/// decoded only once.
/// </summary>
public interface IThumbnailService
{
    /// <summary>Longest edge of a preview, in pixels.</summary>
    public const int Size = 256;

    /// <summary>
    /// Path of a cached preview of a phone file, or <c>null</c> when none can be made. Phone reads
    /// are done one at a time and always to the end of the file.
    /// </summary>
    Task<string?> GetPhoneThumbnailAsync(PhotoAsset asset, CancellationToken cancellationToken);

    /// <summary>Path of a cached preview of a file on disk (photo or video), or <c>null</c> when none can be made.</summary>
    Task<string?> GetFileThumbnailAsync(string path, CancellationToken cancellationToken);
}
