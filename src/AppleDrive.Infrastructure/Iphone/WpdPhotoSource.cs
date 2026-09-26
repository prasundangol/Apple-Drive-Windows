using System.Runtime.InteropServices;
using AppleDrive.Application.Interfaces;
using AppleDrive.Domain.Entities;
using AppleDrive.Domain.Enums;
using AppleDrive.Domain.Results;
using AppleDrive.Infrastructure.Iphone.Wpd;
using AppleDrive.Infrastructure.Iphone.Wpd.Interop;
using Microsoft.Extensions.Logging;

namespace AppleDrive.Infrastructure.Iphone;

/// <summary>Reads iPhone media through Windows Portable Devices (the same layer File Explorer uses).</summary>
public sealed class WpdPhotoSource(ILogger<WpdPhotoSource> logger) : IPhonePhotoSource
{
    private readonly SemaphoreSlim _connectionLock = new(1, 1);
    private WpdConnection? _connection;

    public DeviceInfo? ConnectedDevice => _connection?.Device;

    public async Task<DeviceConnectionResult> ConnectAsync(DeviceInfo device, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(device);
        await _connectionLock.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            CloseConnection();
            return await Task.Run(() => Connect(device), cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            _connectionLock.Release();
        }
    }

    public async Task<Result<IReadOnlyList<PhotoAsset>>> EnumerateAssetsAsync(
        IProgress<int>? progress,
        CancellationToken cancellationToken)
    {
        var connection = _connection;
        if (connection is null)
        {
            return new AppError(ErrorKind.DeviceNotFound, "No device is connected.");
        }

        var skipped = 0;
        var result = await RunDeviceOperation(
            () =>
            {
                var assets = connection.EnumerateMedia(
                    count => progress?.Report(count),
                    (folder, hr) =>
                    {
                        skipped++;
                        logger.LogWarning("Skipped an unreadable object in {Folder} (0x{HResult:X8})", folder, hr);
                    },
                    cancellationToken);
                return (IReadOnlyList<PhotoAsset>)assets;
            },
            "Enumerating device media",
            cancellationToken).ConfigureAwait(false);

        if (skipped > 0)
        {
            logger.LogWarning("{Skipped} device objects could not be read during enumeration", skipped);
        }

        return result;
    }

    public Task<Result<Stream>> OpenAssetAsync(PhotoAsset asset, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(asset);
        var connection = _connection;
        if (connection is null)
        {
            return Task.FromResult(Result<Stream>.Failure(new AppError(ErrorKind.DeviceNotFound, "No device is connected.")));
        }

        return RunDeviceOperation<Stream>(() => connection.OpenRead(asset.Id, cancellationToken), "Opening a device file", cancellationToken);
    }

    public Task<Result<Stream>> OpenThumbnailAsync(PhotoAsset asset, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(asset);
        var connection = _connection;
        if (connection is null)
        {
            return Task.FromResult(Result<Stream>.Failure(new AppError(ErrorKind.DeviceNotFound, "No device is connected.")));
        }

        return RunDeviceOperation<Stream>(
            () => connection.OpenRead(asset.Id, WpdKeys.ResourceThumbnailKey, cancellationToken),
            "Opening a device thumbnail",
            cancellationToken);
    }

    public async Task DisconnectAsync()
    {
        await _connectionLock.WaitAsync().ConfigureAwait(false);
        try
        {
            CloseConnection();
        }
        finally
        {
            _connectionLock.Release();
        }
    }

    public async ValueTask DisposeAsync()
    {
        await DisconnectAsync().ConfigureAwait(false);
        _connectionLock.Dispose();
    }

    private DeviceConnectionResult Connect(DeviceInfo device)
    {
        WpdConnection connection;
        try
        {
            connection = WpdConnection.Open(device);
        }
        catch (WpdException exception) when (exception.Error.Kind == ErrorKind.DeviceLockedOrUntrusted)
        {
            logger.LogInformation("Device refused access; likely locked or not trusted ({Error})", exception.Error);
            return new DeviceConnectionResult(DeviceConnectionStatus.NeedsUnlockOrTrust, device, exception.Error);
        }
        catch (WpdException exception)
        {
            logger.LogWarning("Could not open device: {Error}", exception.Error);
            return new DeviceConnectionResult(DeviceConnectionStatus.Unavailable, device, exception.Error);
        }
        catch (COMException exception)
        {
            var error = WpdErrors.FromHResult(exception.HResult, "Opening the device");
            logger.LogWarning(exception, "Could not open device");
            return new DeviceConnectionResult(DeviceConnectionStatus.Unavailable, device, error);
        }

        try
        {
            if (!connection.HasReadableContent())
            {
                connection.Dispose();
                logger.LogInformation("Device opened but exposes no content; waiting for unlock/trust");
                return new DeviceConnectionResult(
                    DeviceConnectionStatus.NeedsUnlockOrTrust,
                    device,
                    new AppError(ErrorKind.DeviceLockedOrUntrusted, "The device storage is empty or not accessible."));
            }
        }
        catch (WpdException exception)
        {
            connection.Dispose();
            var status = exception.Error.Kind == ErrorKind.DeviceLockedOrUntrusted
                ? DeviceConnectionStatus.NeedsUnlockOrTrust
                : DeviceConnectionStatus.Unavailable;
            logger.LogWarning("Could not read device storage: {Error}", exception.Error);
            return new DeviceConnectionResult(status, device, exception.Error);
        }

        _connection = connection;
        logger.LogInformation("Connected to device {Manufacturer} {Name}", device.Manufacturer, device.FriendlyName);
        return new DeviceConnectionResult(DeviceConnectionStatus.Connected, device);
    }

    private async Task<Result<T>> RunDeviceOperation<T>(Func<T> operation, string description, CancellationToken cancellationToken)
    {
        try
        {
            return await Task.Run(operation, cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            return AppError.Cancelled;
        }
        catch (WpdException exception)
        {
            logger.LogWarning("{Operation} failed: {Error}", description, exception.Error);
            return exception.Error;
        }
        catch (ObjectDisposedException)
        {
            return new AppError(ErrorKind.DeviceDisconnected, $"{description}: the connection was closed.");
        }
        catch (COMException exception)
        {
            logger.LogWarning(exception, "{Operation} failed", description);
            return WpdErrors.FromHResult(exception.HResult, description);
        }
    }

    private void CloseConnection()
    {
        var connection = Interlocked.Exchange(ref _connection, null);
        if (connection is null)
        {
            return;
        }

        try
        {
            connection.Dispose();
            logger.LogInformation("Disconnected from device");
        }
        catch (COMException exception)
        {
            // Closing a device that was already unplugged can fail; the handle is gone either way.
            logger.LogDebug(exception, "Device close reported an error");
        }
    }
}
