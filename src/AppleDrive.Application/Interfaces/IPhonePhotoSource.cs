using AppleDrive.Domain.Entities;
using AppleDrive.Domain.Results;

namespace AppleDrive.Application.Interfaces;

/// <summary>
/// Read-only access to the media on one phone. Implementations never modify or delete
/// anything on the device. One instance holds at most one open connection.
/// </summary>
public interface IPhonePhotoSource : IAsyncDisposable
{
    /// <summary>The currently connected device, or <c>null</c>.</summary>
    DeviceInfo? ConnectedDevice { get; }

    /// <summary>Opens the given device, replacing any existing connection.</summary>
    Task<DeviceConnectionResult> ConnectAsync(DeviceInfo device, CancellationToken cancellationToken);

    /// <summary>Lists every photo and video on the connected device.</summary>
    /// <param name="progress">Receives the running number of media files found.</param>
    Task<Result<IReadOnlyList<PhotoAsset>>> EnumerateAssetsAsync(
        IProgress<int>? progress,
        CancellationToken cancellationToken);

    /// <summary>
    /// Opens a forward-only stream over the asset's bytes. The stream may deliver a different
    /// length than <see cref="PhotoAsset.ReportedSize"/>; always read to the end.
    /// </summary>
    Task<Result<Stream>> OpenAssetAsync(PhotoAsset asset, CancellationToken cancellationToken);

    /// <summary>Closes the connection, if any.</summary>
    Task DisconnectAsync();
}
