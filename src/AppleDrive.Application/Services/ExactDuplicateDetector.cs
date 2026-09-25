using AppleDrive.Application.Interfaces;
using AppleDrive.Domain.Entities;
using AppleDrive.Domain.Enums;
using AppleDrive.Domain.Results;
using Microsoft.Extensions.Logging;

namespace AppleDrive.Application.Services;

/// <summary>Running totals while phone files are compared with the destination.</summary>
public sealed record DuplicateCheckProgress(int ItemsChecked, int TotalItems, int PhoneFilesRead);

/// <summary>
/// Classifies phone media as new or an exact (byte-for-byte) duplicate of a destination file.
/// </summary>
/// <remarks>
/// Staged to avoid needless reads: a phone file whose size matches no destination file is new
/// without being read. Only when sizes match is the phone file hashed, and destination hashes
/// are computed on first need and stored in the index. A file is an exact duplicate only when
/// SHA-256 digests are equal; matching metadata alone never makes a duplicate.
/// </remarks>
public sealed class ExactDuplicateDetector(
    IPhonePhotoSource source,
    IMediaRepository repository,
    IHashService hashService,
    ILogger<ExactDuplicateDetector> logger)
{
    public async Task<Result<ImportPlan>> ClassifyAsync(
        IReadOnlyList<MediaItem> items,
        string destinationRoot,
        IProgress<DuplicateCheckProgress>? progress,
        CancellationToken cancellationToken)
    {
        var root = DestinationIndexService.NormalizeRoot(destinationRoot);
        var results = new List<ItemClassification>(items.Count);
        var state = new RunState();
        logger.LogInformation("Duplicate check started for {Items} items", items.Count);

        try
        {
            for (var index = 0; index < items.Count; index++)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var components = new List<ComponentClassification>(2);
                foreach (var asset in items[index].Components)
                {
                    var component = await ClassifyComponentAsync(asset, root, state, cancellationToken).ConfigureAwait(false);
                    if (!component.IsSuccess)
                    {
                        return component.Error;
                    }

                    components.Add(component.Value);
                }

                results.Add(new ItemClassification(items[index], components));
                progress?.Report(new DuplicateCheckProgress(index + 1, items.Count, state.PhoneFilesRead));
            }
        }
        catch (OperationCanceledException)
        {
            logger.LogInformation("Duplicate check cancelled");
            return AppError.Cancelled;
        }
        catch (DatabaseException exception)
        {
            logger.LogError(exception, "Media index error during duplicate check");
            return new AppError(ErrorKind.Database, exception.Message);
        }

        var plan = new ImportPlan(root, results, state.Unverified);
        logger.LogInformation(
            "Duplicate check finished: {New} new, {Duplicates} exact duplicates, {Read} phone files read, {Unverified} unverified",
            plan.NewCount, plan.ExactDuplicateCount, state.PhoneFilesRead, state.Unverified);
        return plan;
    }

    private async Task<Result<ComponentClassification>> ClassifyComponentAsync(
        PhotoAsset asset,
        string root,
        RunState state,
        CancellationToken cancellationToken)
    {
        if (asset.ReportedSize is { } reportedSize
            && (await GetCandidatesAsync(reportedSize, root, cancellationToken).ConfigureAwait(false)).Count == 0)
        {
            // No destination file has this size, so none can be identical.
            return new ComponentClassification(asset, AssetStatus.New);
        }

        var phoneHash = await HashPhoneFileAsync(asset, state, cancellationToken).ConfigureAwait(false);
        if (!phoneHash.IsSuccess)
        {
            if (IsFatal(phoneHash.Error))
            {
                return phoneHash.Error;
            }

            // Could not read it to compare: never skip without proof.
            state.Unverified++;
            logger.LogWarning("Could not read {File} to check for duplicates: {Error}", asset.FileName, phoneHash.Error);
            return new ComponentClassification(asset, AssetStatus.New);
        }

        var hash = phoneHash.Value;
        if (asset.ReportedSize != hash.Length)
        {
            // The phone delivered a different length than it reported (e.g. converting HEIC to
            // JPEG on the fly), so the size pre-filter was based on the wrong number.
            logger.LogWarning(
                "{File}: phone reported {Reported} bytes but delivered {Actual}",
                asset.FileName, asset.ReportedSize, hash.Length);
        }

        foreach (var candidate in await GetCandidatesAsync(hash.Length, root, cancellationToken).ConfigureAwait(false))
        {
            var candidateHash = await GetDestinationHashAsync(candidate, cancellationToken).ConfigureAwait(false);
            if (hash.Matches(candidateHash))
            {
                logger.LogDebug("{File} is identical to an existing destination file", asset.FileName);
                return new ComponentClassification(asset, AssetStatus.ExactDuplicate, candidate.FullPath, hash.Sha256);
            }
        }

        logger.LogDebug("{File} has same-size destination files but different content", asset.FileName);
        return new ComponentClassification(asset, AssetStatus.New, Sha256: hash.Sha256);
    }

    private async Task<IReadOnlyList<IndexedMediaFile>> GetCandidatesAsync(long size, string root, CancellationToken cancellationToken)
    {
        var sameSize = await repository.GetAvailableBySizeAsync(size, cancellationToken).ConfigureAwait(false);
        var prefix = root.EndsWith(Path.DirectorySeparatorChar) ? root : root + Path.DirectorySeparatorChar;
        return sameSize.Where(file => file.FullPath.StartsWith(prefix, StringComparison.OrdinalIgnoreCase)).ToList();
    }

    private async Task<Result<HashResult>> HashPhoneFileAsync(PhotoAsset asset, RunState state, CancellationToken cancellationToken)
    {
        var open = await source.OpenAssetAsync(asset, cancellationToken).ConfigureAwait(false);
        if (!open.IsSuccess)
        {
            return open.Error;
        }

        try
        {
            await using var stream = open.Value;
            var result = await hashService.ComputeAsync(stream, null, cancellationToken).ConfigureAwait(false);
            state.PhoneFilesRead++;
            return result;
        }
        catch (SourceReadException exception)
        {
            return exception.Error;
        }
        catch (IOException exception)
        {
            return new AppError(ErrorKind.DeviceIo, exception.Message, exception.HResult);
        }
    }

    /// <summary>
    /// The stored hash, or a freshly computed one (then stored). Returns <c>null</c> when the file
    /// can't be read or no longer matches its index record, in which case it can't be a match.
    /// </summary>
    private async Task<byte[]?> GetDestinationHashAsync(IndexedMediaFile file, CancellationToken cancellationToken)
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

    /// <summary>Errors that mean the phone is gone; continuing would only produce more failures.</summary>
    private static bool IsFatal(AppError error) => error.Kind is
        ErrorKind.Cancelled or ErrorKind.DeviceDisconnected or ErrorKind.DeviceNotFound or ErrorKind.DeviceLockedOrUntrusted;

    private sealed class RunState
    {
        public int PhoneFilesRead { get; set; }

        public int Unverified { get; set; }
    }
}
