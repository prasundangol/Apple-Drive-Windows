using AppleDrive.Application.Interfaces;
using AppleDrive.Domain.Entities;
using Microsoft.Extensions.Logging;

namespace AppleDrive.Application.Services;

/// <summary>
/// Finds destination files with given content, using the index. Destination hashes are computed
/// on first need and stored, and a file is trusted only while it still matches its index record.
/// </summary>
public sealed class DestinationContentLookup(
    IMediaRepository repository,
    IHashService hashService,
    ILogger<DestinationContentLookup> logger)
{
    /// <summary>Available indexed files under <paramref name="root"/> with exactly this size.</summary>
    public async Task<IReadOnlyList<IndexedMediaFile>> GetSameSizeAsync(long size, string root, CancellationToken cancellationToken)
    {
        var sameSize = await repository.GetAvailableBySizeAsync(size, cancellationToken).ConfigureAwait(false);
        var prefix = root.EndsWith(Path.DirectorySeparatorChar) ? root : root + Path.DirectorySeparatorChar;
        return sameSize.Where(file => file.FullPath.StartsWith(prefix, StringComparison.OrdinalIgnoreCase)).ToList();
    }

    /// <summary>The first destination file under <paramref name="root"/> that is identical to the given content, or <c>null</c>.</summary>
    public async Task<IndexedMediaFile?> FindIdenticalAsync(HashResult content, string root, CancellationToken cancellationToken)
    {
        foreach (var candidate in await GetSameSizeAsync(content.Length, root, cancellationToken).ConfigureAwait(false))
        {
            if (content.Matches(await GetHashAsync(candidate, cancellationToken).ConfigureAwait(false)))
            {
                return candidate;
            }
        }

        return null;
    }

    /// <summary>
    /// The stored hash, or a freshly computed one (then stored). Returns <c>null</c> when the file
    /// can't be read or no longer matches its index record, in which case it can't be a match.
    /// </summary>
    public async Task<byte[]?> GetHashAsync(IndexedMediaFile file, CancellationToken cancellationToken)
    {
        try
        {
            var info = new FileInfo(file.FullPath);
            if (!info.Exists
                || info.Length != file.FileSize
                || new DateTimeOffset(info.LastWriteTimeUtc).ToUnixTimeMilliseconds() != file.ModifiedAt.ToUnixTimeMilliseconds())
            {
                logger.LogDebug("Destination file changed since it was indexed; not used as a match");
                return null;
            }

            if (file.Sha256 is { } stored)
            {
                return stored;
            }

            var computed = await hashService.ComputeFileAsync(file.FullPath, cancellationToken).ConfigureAwait(false);
            if (computed.Length != file.FileSize)
            {
                return null;
            }

            await repository.SetSha256Async(file.Id, computed.Sha256, cancellationToken).ConfigureAwait(false);
            var moved = await repository.DeleteUnavailableBySha256Async(computed.Sha256, cancellationToken).ConfigureAwait(false);
            if (moved > 0)
            {
                logger.LogInformation("Detected {Count} moved file(s) in the destination", moved);
            }

            return computed.Sha256;
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            logger.LogWarning(exception, "Could not hash a destination file");
            return null;
        }
    }
}
