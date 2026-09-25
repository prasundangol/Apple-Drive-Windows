using AppleDrive.Domain.Results;

namespace AppleDrive.Application.Interfaces;

/// <summary>
/// Thrown by streams returned from <see cref="IPhonePhotoSource.OpenAssetAsync"/> when the device
/// fails mid-read. Derives from <see cref="IOException"/> so generic stream code handles it,
/// while still carrying the structured <see cref="AppError"/>.
/// </summary>
public sealed class SourceReadException(AppError error) : IOException(error.ToString(), error.HResult ?? 0)
{
    public AppError Error { get; } = error;
}
