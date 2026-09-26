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
/// The one shortcut: a file this app itself transferred earlier (same persistent id, name and
/// size, verified by SHA-256 at the time), whose copy is still unchanged in the index, is a
/// duplicate without reading the phone again. The proof is that transfer's own hash.
/// </remarks>
public sealed class ExactDuplicateDetector(
    IPhonePhotoSource source,
    DestinationContentLookup destination,
    ITransferRepository transferHistory,
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
            state.History = (await transferHistory.GetCompletedUnderRootAsync(root, cancellationToken).ConfigureAwait(false))
                .Where(record => record is { SourcePersistentId: not null, Sha256: not null, DestinationPath: not null, FileSize: not null })
                .GroupBy(record => record.SourcePersistentId!, StringComparer.Ordinal)
                .ToDictionary(group => group.Key, group => group.OrderByDescending(record => record.Id).ToList(), StringComparer.Ordinal);

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
            "Duplicate check finished: {New} new, {Duplicates} exact duplicates, {Read} phone files read, {FromHistory} known from earlier transfers, {Unverified} unverified",
            plan.NewCount, plan.ExactDuplicateCount, state.PhoneFilesRead, state.FromHistory, state.Unverified);
        return plan;
    }

    private async Task<Result<ComponentClassification>> ClassifyComponentAsync(
        PhotoAsset asset,
        string root,
        RunState state,
        CancellationToken cancellationToken)
    {
        if (asset.ReportedSize is { } reportedSize
            && (await destination.GetSameSizeAsync(reportedSize, root, cancellationToken).ConfigureAwait(false)).Count == 0)
        {
            // No destination file has this size, so none can be identical.
            return new ComponentClassification(asset, AssetStatus.New);
        }

        if (await FindEarlierTransferAsync(asset, state, cancellationToken).ConfigureAwait(false) is { } earlier)
        {
            state.FromHistory++;
            return new ComponentClassification(asset, AssetStatus.ExactDuplicate, earlier.FullPath, earlier.Sha256);
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

        var identical = await destination.FindIdenticalAsync(hash, root, cancellationToken).ConfigureAwait(false);
        if (identical is not null)
        {
            logger.LogDebug("{File} is identical to an existing destination file", asset.FileName);
            return new ComponentClassification(asset, AssetStatus.ExactDuplicate, identical.FullPath, hash.Sha256);
        }

        logger.LogDebug("{File} has same-size destination files but different content", asset.FileName);
        return new ComponentClassification(asset, AssetStatus.New, Sha256: hash.Sha256);
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
    /// The destination file an earlier transfer made from this very phone file, if it is still
    /// there unchanged. Identity needs the persistent id, the name and the exact size to agree.
    /// </summary>
    private async Task<IndexedMediaFile?> FindEarlierTransferAsync(PhotoAsset asset, RunState state, CancellationToken cancellationToken)
    {
        if (asset.PersistentId is not { } persistentId
            || asset.ReportedSize is not { } size
            || !state.History.TryGetValue(persistentId, out var earlier))
        {
            return null;
        }

        foreach (var record in earlier)
        {
            if (record.FileSize == size
                && string.Equals(record.SourceFileName, asset.FileName, StringComparison.OrdinalIgnoreCase)
                && await destination.GetUnchangedAsync(record.DestinationPath!, record.Sha256!, cancellationToken).ConfigureAwait(false) is { } file)
            {
                return file;
            }
        }

        return null;
    }

    /// <summary>Errors that mean the phone is gone; continuing would only produce more failures.</summary>
    private static bool IsFatal(AppError error) => error.Kind is
        ErrorKind.Cancelled or ErrorKind.DeviceDisconnected or ErrorKind.DeviceNotFound or ErrorKind.DeviceLockedOrUntrusted;

    private sealed class RunState
    {
        public Dictionary<string, List<TransferRecord>> History { get; set; } = [];

        public int FromHistory { get; set; }

        public int PhoneFilesRead { get; set; }

        public int Unverified { get; set; }
    }
}
