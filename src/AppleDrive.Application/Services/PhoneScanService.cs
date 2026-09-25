using AppleDrive.Application.Interfaces;
using AppleDrive.Domain.Media;
using AppleDrive.Domain.Results;
using Microsoft.Extensions.Logging;

namespace AppleDrive.Application.Services;

/// <summary>Enumerates the connected phone and groups its files into media items.</summary>
public sealed class PhoneScanService(IPhonePhotoSource photoSource, ILogger<PhoneScanService> logger)
{
    public async Task<Result<PhoneScanResult>> ScanAsync(IProgress<int>? progress, CancellationToken cancellationToken)
    {
        var device = photoSource.ConnectedDevice;
        if (device is null)
        {
            return new AppError(ErrorKind.DeviceNotFound, "No device is connected.");
        }

        logger.LogInformation("Phone scan started");
        var started = DateTimeOffset.UtcNow;

        var assets = await photoSource.EnumerateAssetsAsync(progress, cancellationToken).ConfigureAwait(false);
        if (!assets.IsSuccess)
        {
            logger.LogWarning("Phone scan failed: {Error}", assets.Error);
            return assets.Error;
        }

        var result = new PhoneScanResult(device, LivePhotoGrouper.Group(assets.Value));
        logger.LogInformation(
            "Phone scan finished in {Elapsed}: {Files} files, {Photos} photos ({LivePhotos} live), {Videos} videos",
            DateTimeOffset.UtcNow - started,
            result.FileCount,
            result.PhotoCount,
            result.LivePhotoCount,
            result.VideoCount);
        return result;
    }
}
